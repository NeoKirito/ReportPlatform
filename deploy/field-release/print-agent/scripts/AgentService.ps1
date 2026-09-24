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
$exePath = Join-Path $appRoot 'PEIS.PrintAgent.exe'
$stateRoot = Join-Path $packageRoot 'run'
$statePath = Join-Path $stateRoot 'agent.json'
$lockHandle = $null
$exitCode = 0

function Get-OwnedProcess {
    @(Get-Process -Name 'PEIS.PrintAgent' -ErrorAction SilentlyContinue | Where-Object {
        try {
            $_.Path -and [string]::Equals($_.Path, $exePath, [StringComparison]::OrdinalIgnoreCase)
        }
        catch { $false }
    })
}

function Test-Blank([object]$value) {
    if ($null -eq $value) { return $true }
    $str = [string]$value
    return ($str.Trim().Length -eq 0)
}

function Read-AgentConfig {
    $configPath = Join-Path $packageRoot 'config.ini'
    if (!(Test-Path -LiteralPath $configPath)) {
        $configPath = Join-Path $packageRoot 'agent.ini'
    }
    if (!(Test-Path -LiteralPath $configPath)) {
        throw '未在当前目录下找到 config.ini 或 agent.ini 配置文件。'
    }

    $values = @{}
    foreach ($line in [IO.File]::ReadAllLines($configPath, [Text.Encoding]::UTF8)) {
        $trimmed = $line.Trim()
        if (!$trimmed -or $trimmed.StartsWith('#') -or $trimmed.StartsWith(';') -or ($trimmed.StartsWith('[') -and $trimmed.EndsWith(']'))) { continue }
        $separator = $trimmed.IndexOf('=')
        if ($separator -lt 0) { continue }
        $key = $trimmed.Substring(0, $separator).Trim()
        $val = $trimmed.Substring($separator + 1).Trim()
        $values[$key] = $val
    }

    if (!$values.ContainsKey('ServerUrl') -or (Test-Blank $values['ServerUrl'])) {
        throw '启动前请先在 config.ini 中配置 ServerUrl 报表服务地址（例如：ServerUrl=http://192.168.0.237:82）。'
    }

    return $values
}

try {
    New-Item -ItemType Directory -Path $stateRoot -Force | Out-Null
    try {
        $lockHandle = [IO.File]::Open((Join-Path $stateRoot 'control.lock'), [IO.FileMode]::OpenOrCreate, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
    }
    catch {
        throw '已有另一个启动或停止操作正在执行中，请稍后再试。'
    }

    $running = @(Get-OwnedProcess)

    if ($Action -eq 'Stop') {
        if ($running.Count -eq 0) {
            Write-Host 'PrintAgent 打印代理当前处于停止状态。'
        }
        else {
            foreach ($process in $running) {
                $current = Get-Process -Id $process.Id -ErrorAction SilentlyContinue
                if ($current -and $current.Path -eq $exePath -and $current.StartTime -eq $process.StartTime) {
                    Stop-Process -InputObject $current -Force
                    if (!$current.WaitForExit(10000)) {
                        throw '打印代理进程在 10 秒内未退出。'
                    }
                }
            }
            Remove-Item -LiteralPath $statePath -Force -ErrorAction SilentlyContinue
            Write-Host 'PrintAgent 打印代理已成功停止。'
        }
    }
    elseif ($Action -eq 'Status') {
        if ($running.Count -eq 0) {
            Write-Host 'PrintAgent 打印代理当前处于停止状态。'
        }
        else {
            Write-Host ('PrintAgent 打印代理正在运行中。进程 PID: ' + (($running | ForEach-Object { $_.Id }) -join ', '))
            if (Test-Path -LiteralPath $statePath) {
                try {
                    $rawState = [IO.File]::ReadAllText($statePath, [Text.Encoding]::UTF8)
                    if ($rawState -match '"ServerUrl"\s*:\s*"([^"]+)"') { Write-Host "服务器地址 : $($matches[1])" }
                    if ($rawState -match '"StationId"\s*:\s*"([^"]+)"') { Write-Host "工作站标识 : $($matches[1])" }
                    if ($rawState -match '"StartedAt"\s*:\s*"([^"]+)"') { Write-Host "启动时间   : $($matches[1])" }
                }
                catch { }
            }

            $logsRoot = Join-Path $packageRoot 'logs'
            $recentLog = Get-ChildItem -Path $logsRoot -Filter "agent-*.log" -ErrorAction SilentlyContinue | Sort-Object LastWriteTime -Descending | Select-Object -First 1
            if ($recentLog) {
                Write-Host "`n最新日志 (${recentLog.Name}) 尾部内容:"
                @(Get-Content -LiteralPath $recentLog.FullName -ErrorAction SilentlyContinue) | Select-Object -Last 5 | ForEach-Object { Write-Host "  $_" }
            }
        }
    }
    elseif ($running.Count -gt 0) {
        Write-Host ('PrintAgent 打印代理已在运行中。进程 PID: ' + (($running | ForEach-Object { $_.Id }) -join ', '))
        Write-Host '若需修改配置，请先双击 [关闭服务.cmd] 停止代理。'
    }
    else {
        if (!(Test-Path -LiteralPath $exePath)) {
            throw '未找到 app/PEIS.PrintAgent.exe，请勿在压缩包内直接运行，请先完整解压整个 ZIP 压缩包。'
        }

        $config = Read-AgentConfig
        $serverUrl = $config['ServerUrl']
        $stationId = if ($config.ContainsKey('StationId') -and $config['StationId'] -and $config['StationId'] -ne 'AUTO') { $config['StationId'] } else { $env:COMPUTERNAME }

        $logsRoot = Join-Path $packageRoot 'logs'
        New-Item -ItemType Directory -Path $logsRoot -Force | Out-Null
        $stamp = Get-Date -Format 'yyyyMMdd-HHmmss-fff'
        $stdout = Join-Path $logsRoot "$stamp.stdout.log"
        $stderr = Join-Path $logsRoot "$stamp.stderr.log"

        $settings = @{
            'DOTNET_ENVIRONMENT' = 'Production'
        }
        $saved = @{}
        $child = $null
        try {
            foreach ($key in $settings.Keys) {
                $saved[$key] = [Environment]::GetEnvironmentVariable($key, 'Process')
                [Environment]::SetEnvironmentVariable($key, $settings[$key], 'Process')
            }
            try {
                $child = Start-Process -FilePath $exePath -WorkingDirectory $appRoot -PassThru
            }
            finally {
                foreach ($key in $saved.Keys) { [Environment]::SetEnvironmentVariable($key, $saved[$key], 'Process') }
            }

            Start-Sleep -Milliseconds 1500
            $child.Refresh()
            if ($child.HasExited) {
                $errText = ''
                $recentLog = Get-ChildItem -Path $logsRoot -Filter "agent-*.log" -ErrorAction SilentlyContinue | Sort-Object LastWriteTime -Descending | Select-Object -First 1
                if ($recentLog) { $errText = (@(Get-Content -LiteralPath $recentLog.FullName -ErrorAction SilentlyContinue) | Select-Object -Last 10) -join "`n" }
                throw "PrintAgent 打印代理异常退出 (退出码: $($child.ExitCode))。$errText"
            }

            $stateText = "{`"ProcessId`":$($child.Id),`"ServerUrl`":`"$serverUrl`",`"StationId`":`"$stationId`",`"StartedAt`":`"$($child.StartTime.ToUniversalTime().ToString('o'))`"}"
            [IO.File]::WriteAllText($statePath, $stateText, [Text.Encoding]::UTF8)

            Write-Host '============================================================' -ForegroundColor Green
            Write-Host " PrintAgent 打印代理已成功在后台启动！进程 PID: $($child.Id)" -ForegroundColor Green
            Write-Host " 服务器地址 : $serverUrl"
            Write-Host " 工作站标识 : $stationId"
            Write-Host " 日志目录   : $logsRoot"
            Write-Host '============================================================' -ForegroundColor Green
            Write-Host '提示：已托盘运行，没有控制台黑框占用屏幕，您可以直接关闭本黑框窗口！'
            Write-Host '如需停止服务，请双击 [关闭服务.cmd] 或右键系统右下角托盘图标退出。'
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
