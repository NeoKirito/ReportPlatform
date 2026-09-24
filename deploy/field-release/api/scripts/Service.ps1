[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('Start', 'Stop', 'Status')]
    [string]$Action
)

# Compatible with Windows PowerShell 2.0+ and modern PowerShell.
$scriptDir = if (Test-Path Variable:PSScriptRoot) { $PSScriptRoot } else { $null }
if (!$scriptDir -and $MyInvocation.MyCommand.Path) { $scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path }
if (!$scriptDir -and $MyInvocation.MyCommand.Definition) { $scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Definition }
$packageRoot = Split-Path -Parent $scriptDir

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
try { [Console]::OutputEncoding = [System.Text.Encoding]::UTF8 } catch { }
$appRoot = Join-Path $packageRoot 'app'
$exePath = Join-Path $appRoot 'PEIS.Report.Api.exe'
$stateRoot = Join-Path $packageRoot 'run'
$statePath = Join-Path $stateRoot 'service.json'
$lockHandle = $null
$exitCode = 0

function Get-OwnedProcess {
    @(Get-Process -Name 'PEIS.Report.Api' -ErrorAction SilentlyContinue | Where-Object {
        try { $_.Path -and [string]::Equals($_.Path, $exePath, [StringComparison]::OrdinalIgnoreCase) }
        catch { $false }
    })
}

function Test-Blank([object]$value) {
    if ($null -eq $value) { return $true }
    $str = [string]$value
    return ($str.Trim().Length -eq 0)
}

function Read-ServiceConfig {
    $configPath = Join-Path $packageRoot 'config.ini'
    $values = @{}
    foreach ($line in [IO.File]::ReadAllLines($configPath, [Text.Encoding]::UTF8)) {
        $trimmed = $line.Trim()
        if (!$trimmed -or $trimmed.StartsWith('#') -or $trimmed.StartsWith(';') -or ($trimmed.StartsWith('[') -and $trimmed.EndsWith(']'))) { continue }
        $separator = $trimmed.IndexOf('=')
        if ($separator -lt 0) { throw 'config.ini 配置行格式无效，必须为“键=值”（例如 Port=82）。' }
        $key = $trimmed.Substring(0, $separator).Trim()
        if ($values.ContainsKey($key)) { throw "config.ini 中存在重复的配置项: $key" }
        $values[$key] = $trimmed.Substring($separator + 1).Trim()
    }
    $portNumber = 0
    if (!$values.ContainsKey('Port') -or ![int]::TryParse($values['Port'], [ref]$portNumber) -or $portNumber -lt 1 -or $portNumber -gt 65535) {
        throw 'config.ini 中的 Port 端口必须为 1 到 65535 之间的有效数字。'
    }
    if (!$values.ContainsKey('ConnectionString') -or (Test-Blank $values['ConnectionString'])) {
        throw '启动前请先在 config.ini 中填写 ConnectionString 数据库连接字符串。'
    }
    # Parse without opening the database or printing any part of the secret.
    try {
        $connection = New-Object System.Data.Common.DbConnectionStringBuilder
        $connection.set_ConnectionString($values['ConnectionString'])
        $hasServer = @('Server', 'Data Source', 'Address', 'Addr', 'Network Address') | Where-Object { $connection.ContainsKey($_) -and !(Test-Blank $connection[$_]) }
        $hasDatabase = @('Database', 'Initial Catalog') | Where-Object { $connection.ContainsKey($_) -and !(Test-Blank $connection[$_]) }
        if (!$hasServer -or !$hasDatabase) { throw 'Missing server/database.' }
    }
    catch { throw 'ConnectionString 连接字符串格式无效，必须包含 Server 和 Database 配置。' }
    return @{ Port = $portNumber; ConnectionString = $values['ConnectionString'] }
}

try {
    New-Item -ItemType Directory -Path $stateRoot -Force | Out-Null
    try {
        $lockHandle = [IO.File]::Open((Join-Path $stateRoot 'control.lock'), [IO.FileMode]::OpenOrCreate, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
    }
    catch { throw '已有另一个启动或停止操作正在执行中，请稍后再试。' }

    $running = @(Get-OwnedProcess)
    if ($Action -eq 'Stop') {
        foreach ($process in $running) {
            $current = Get-Process -Id $process.Id -ErrorAction SilentlyContinue
            if ($current -and $current.Path -eq $exePath -and $current.StartTime -eq $process.StartTime) {
                Stop-Process -InputObject $current -Force
                if (!$current.WaitForExit(15000)) { throw '服务进程在 15 秒内未正常退出。' }
            }
        }
        Remove-Item -LiteralPath $statePath -Force -ErrorAction SilentlyContinue
        Write-Host '报表服务已停止。'
    }
    elseif ($Action -eq 'Status') {
        if ($running.Count -eq 0) { Write-Host '报表服务当前处于停止状态。' }
        else {
            Write-Host ('报表服务正在运行中。进程 PID: ' + (($running | ForEach-Object { $_.Id }) -join ', '))
            if (Test-Path -LiteralPath $statePath) {
                try {
                    $rawState = [IO.File]::ReadAllText($statePath, [Text.Encoding]::UTF8)
                    if ($rawState -match '"Port"\s*:\s*(\d+)') {
                        Write-Host "上次启动接口地址: http://127.0.0.1:$($matches[1])/BaseInfo/Report/GetReportByJson"
                    }
                }
                catch { }
            }
        }
    }
    elseif ($running.Count -gt 0) {
        Write-Host '报表服务已在运行中。若需应用新的配置，请先运行 [关闭服务.cmd] 停止服务。'
    }
    else {
        if (!(Test-Path -LiteralPath $exePath)) { throw '未找到 app/PEIS.Report.Api.exe，请勿在压缩包内直接运行，请先完整解压整个 ZIP 压缩包。' }
        $config = Read-ServiceConfig
        $port = $config.Port
        $probe = New-Object Net.Sockets.TcpListener([Net.IPAddress]::Any, $port)
        try {
            $probe.Server.ExclusiveAddressUse = $true
            $probe.Start()
        }
        catch { throw "端口 $port 已被其他程序占用。请在 config.ini 中修改 Port 或关闭占用该端口的程序。" }
        finally { $probe.Stop() }

        $logsRoot = Join-Path $packageRoot 'logs'
        New-Item -ItemType Directory -Path $logsRoot -Force | Out-Null
        $stamp = Get-Date -Format 'yyyyMMdd-HHmmss-fff'
        $stdout = Join-Path $logsRoot "$stamp.stdout.log"
        $stderr = Join-Path $logsRoot "$stamp.stderr.log"
        # Process-local environment only; secrets never appear in command lines,
        # generated configuration files, service state or launcher output.
        $settings = @{
            'DOTNET_ENVIRONMENT' = 'Production'
            'ASPNETCORE_ENVIRONMENT' = 'Production'
            'ReportDatabase__ConnectionString' = $config.ConnectionString
            'WatermarkDatabase__ConnectionString' = $config.ConnectionString
            'ReportEngine__DefinitionSource' = 'LegacySqlServer'
            'ReportEngine__Renderer' = 'FastReportOpenSource'
        }
        $saved = @{}
        $child = $null
        try {
            foreach ($key in $settings.Keys) {
                $saved[$key] = [Environment]::GetEnvironmentVariable($key, 'Process')
                [Environment]::SetEnvironmentVariable($key, $settings[$key], 'Process')
            }
            try {
                $child = Start-Process -FilePath $exePath -ArgumentList @('--urls', "http://0.0.0.0:$port") -WorkingDirectory $appRoot -WindowStyle Hidden -RedirectStandardOutput $stdout -RedirectStandardError $stderr -PassThru
            }
            finally {
                foreach ($key in $saved.Keys) { [Environment]::SetEnvironmentVariable($key, $saved[$key], 'Process') }
            }
            $stateText = "{`"ProcessId`":$($child.Id),`"Port`":$port,`"StartedAt`":`"$($child.StartTime.ToUniversalTime().ToString('o'))`"}"
            [IO.File]::WriteAllText($statePath, $stateText, [Text.Encoding]::UTF8)
            $deadline = [DateTime]::UtcNow.AddSeconds(45)
            $healthy = $false
            while ([DateTime]::UtcNow -lt $deadline) {
                $child.Refresh()
                if ($child.HasExited) {
                    $errText = ''
                    if (Test-Path -LiteralPath $stderr) { $errText = (@(Get-Content -LiteralPath $stderr -ErrorAction SilentlyContinue) | Select-Object -Last 10) -join "`n" }
                    if (!$errText -and (Test-Path -LiteralPath $stdout)) { $errText = (@(Get-Content -LiteralPath $stdout -ErrorAction SilentlyContinue) | Select-Object -Last 10) -join "`n" }
                    throw "服务在启动过程中异常退出 (退出码: $($child.ExitCode))。$errText"
                }
                try {
                    $client = New-Object System.Net.WebClient
                    $client.Encoding = [Text.Encoding]::UTF8
                    $responseContent = $client.DownloadString("http://127.0.0.1:$port/health")
                    if ($responseContent -match '"service"\s*:\s*"PEIS\.Report\.Api"') {
                        $healthy = $true
                        break
                    }
                }
                catch { }
                finally {
                    if ($client) { $client.Dispose() }
                }
                Start-Sleep -Milliseconds 300
            }
            $child.Refresh()
            if (!$healthy -or $child.HasExited) {
                $errText = ''
                if (Test-Path -LiteralPath $stderr) { $errText = (@(Get-Content -LiteralPath $stderr -ErrorAction SilentlyContinue) | Select-Object -Last 10) -join "`n" }
                if (!$errText -and (Test-Path -LiteralPath $stdout)) { $errText = (@(Get-Content -LiteralPath $stdout -ErrorAction SilentlyContinue) | Select-Object -Last 10) -join "`n" }
                throw "服务在 45 秒内未响应健康检查 (退出码: $($child.ExitCode))。$errText"
            }
            Write-Host "============================================================" -ForegroundColor Green
            Write-Host " PEIS 报表服务已成功在后台启动！进程 PID: $($child.Id)" -ForegroundColor Green
            Write-Host " 本地接口地址 : http://127.0.0.1:$port/BaseInfo/Report/GetReportByJson" -ForegroundColor Cyan
            Write-Host " 局域网访问   : 请将 127.0.0.1 替换为当前服务器的局域网 IP 地址"
            Write-Host " 健康检查状态 : 正常通过 (服务已就绪)"
            Write-Host "============================================================" -ForegroundColor Green
            Write-Host "提示：服务已在后台静默运行，您可以直接关闭此黑框窗口！"
            Write-Host "如需停止服务，请双击运行 [关闭服务.cmd]；查看状态请双击 [查看状态.cmd]。"
        }
        catch {
            if ($child) {
                $child.Refresh()
                if (!$child.HasExited) { Stop-Process -InputObject $child -Force }
            }
            Remove-Item -LiteralPath $statePath -Force -ErrorAction SilentlyContinue
            throw
        }
    }
}
catch {
    Write-Host ('【错误】' + $_.Exception.Message) -ForegroundColor Red
    $exitCode = 1
}
finally {
    if ($lockHandle) { $lockHandle.Dispose() }
}
exit $exitCode
