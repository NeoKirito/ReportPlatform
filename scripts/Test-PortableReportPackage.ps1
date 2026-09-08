[CmdletBinding()]
param([Parameter(Mandatory = $true)][string]$ZipPath)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$testRoot = Join-Path $repositoryRoot ('artifacts\portable-smoke-' + [Guid]::NewGuid().ToString('N'))
$primaryRoot = Join-Path $testRoot 'Chinese space path 测试包'
$otherRoot = Join-Path $testRoot 'Other installation'
$evidence = New-Object 'System.Collections.Generic.List[string]'
$configEncoding = New-Object Text.UTF8Encoding($true)
$controlLock = $null
$portProbe = $null
$ready = $false

function Assert-Check([bool]$Condition, [string]$Name) {
    if (!$Condition) { throw "FAIL: $Name" }
    $evidence.Add($Name)
    Write-Host "PASS: $Name"
}

function Get-FreePort {
    $listener = New-Object Net.Sockets.TcpListener([Net.IPAddress]::Loopback, 0)
    try { $listener.Start(); return $listener.LocalEndpoint.Port }
    finally { $listener.Stop() }
}

function Write-TestConfig([string]$Root, [int]$Port) {
    # Synthetic credentials only. No connection/query to any real database.
    # Special characters must survive the launcher without CMD expansion.
    $text = 'Port=' + $Port + "`r`n" + 'ConnectionString=Server=127.0.0.1,1;Database=PortableSmoke;User ID=not_real;Password="smoke<&!%=$;word";Encrypt=Optional;Connect Timeout=1;' + "`r`n"
    [IO.File]::WriteAllText((Join-Path $Root 'config.ini'), $text, $configEncoding)
}

function Invoke-Control([string]$Root, [string]$Name, [int]$ExpectedExit = 0) {
    # Wait for cmd.exe itself, not its persistent background child. Capturing a
    # native pipeline waits for inherited pipe handles; Start-Process -Wait waits
    # for the whole process tree. Neither models a double-click launcher correctly.
    $capture = Join-Path $testRoot ([Guid]::NewGuid().ToString('N') + '.launcher')
    $command = '""' + (Join-Path $Root $Name) + '" --no-pause > "' + $capture + '.out" 2> "' + $capture + '.err""'
    $launcher = Start-Process -FilePath $env:ComSpec -ArgumentList @('/d', '/c', $command) -WindowStyle Hidden -PassThru
    if (!$launcher.WaitForExit(60000)) { throw "Launcher did not exit in time: $Name" }
    $launcher.Refresh()
    $code = $launcher.ExitCode
    $output = ''
    foreach ($file in @("$capture.out", "$capture.err")) {
        $stream = [IO.File]::Open($file, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::ReadWrite)
        $reader = New-Object IO.StreamReader($stream)
        try { $output += $reader.ReadToEnd() }
        finally { $reader.Dispose() }
    }
    if ($code -ne $ExpectedExit) { throw "Unexpected exit $code from $Name. $output" }
    return $output
}

function Assert-HttpStatus([int]$Port, [string]$Path, [string]$Method, [int]$Expected) {
    $args = @{ UseBasicParsing = $true; Uri = "http://127.0.0.1:$Port$Path"; Method = $Method; TimeoutSec = 5 }
    if ($Method -eq 'POST') { $args['ContentType'] = 'application/json'; $args['Body'] = '{' }
    try { $status = [int](Invoke-WebRequest @args).StatusCode }
    catch {
        if (!$_.Exception.Response) { throw }
        $status = [int]$_.Exception.Response.StatusCode
    }
    Assert-Check ($status -eq $Expected) "$Method $Path returns $Expected"
}

try {
    New-Item -ItemType Directory -Path $testRoot -Force | Out-Null
    Expand-Archive -LiteralPath (Resolve-Path -LiteralPath $ZipPath).Path -DestinationPath $testRoot
    $extracted = @(Get-ChildItem -LiteralPath $testRoot -Directory)
    Assert-Check ($extracted.Count -eq 1) 'ZIP has one root directory'
    # Verify both endpoints of this move are strictly inside our new test root.
    foreach ($candidate in @($extracted[0].FullName, $primaryRoot)) {
        if (![IO.Path]::GetFullPath($candidate).StartsWith($testRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe test move path.' }
    }
    Move-Item -LiteralPath $extracted[0].FullName -Destination $primaryRoot
    Copy-Item -LiteralPath $primaryRoot -Destination $otherRoot -Recurse
    $ready = $true

    $runtime = Get-Content -LiteralPath (Join-Path $primaryRoot 'app\PEIS.Report.Api.runtimeconfig.json') -Raw | ConvertFrom-Json
    Assert-Check (@($runtime.runtimeOptions.includedFrameworks).Count -gt 0) 'ZIP includes its own .NET runtime'
    $defaults = Get-Content -LiteralPath (Join-Path $primaryRoot 'app\appsettings.json') -Raw | ConvertFrom-Json
    Assert-Check ($defaults.ReportEngine.Renderer -eq 'FastReportOpenSource' -and $defaults.ReportEngine.DefinitionSource -eq 'LegacySqlServer') 'Real SQL Server and FastReport defaults are enabled'
    Assert-Check ([string]::IsNullOrEmpty($defaults.ReportDatabase.ConnectionString)) 'Published application does not contain database credentials'
    $shippedFiles = @(Get-ChildItem -LiteralPath $primaryRoot -Recurse -File)
    $forbidden = @($shippedFiles | Where-Object { $_.Extension -in '.pdf', '.frx', '.db', '.sqlite', '.har', '.trace' -or $_.Name -eq 'appsettings.Production.json' })
    Assert-Check ($forbidden.Count -eq 0) 'ZIP does not contain report data or private production configuration'

    $result = Invoke-Control $primaryRoot '启动服务.cmd' 1
    Assert-Check ($result.Contains('Fill ConnectionString')) 'Blank connection string is rejected clearly'
    [IO.File]::WriteAllText((Join-Path $primaryRoot 'config.ini'), "Port=70000`r`nConnectionString=bad", $configEncoding)
    $result = Invoke-Control $primaryRoot '启动服务.cmd' 1
    Assert-Check ($result.Contains('Port must be')) 'Invalid port is rejected'

    $port1 = Get-FreePort
    Write-TestConfig $primaryRoot $port1
    $portProbe = New-Object Net.Sockets.TcpListener([Net.IPAddress]::Any, $port1)
    $portProbe.Start()
    $result = Invoke-Control $primaryRoot '启动服务.cmd' 1
    Assert-Check ($result.Contains('unavailable')) 'Occupied port is rejected without stopping its owner'
    $portProbe.Stop()
    $portProbe = $null

    $controlLock = [IO.File]::Open((Join-Path $primaryRoot 'run\control.lock'), [IO.FileMode]::OpenOrCreate, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
    $result = Invoke-Control $primaryRoot '启动服务.cmd' 1
    Assert-Check ($result.Contains('Another start/stop')) 'Concurrent control actions are blocked'
    $controlLock.Dispose()
    $controlLock = $null

    $result = Invoke-Control $primaryRoot '启动服务.cmd'
    Assert-Check ($result.Contains('Service started')) 'Double-click launcher works under Windows PowerShell in a Chinese/space path'
    $firstState = Get-Content -LiteralPath (Join-Path $primaryRoot 'run\service.json') -Raw | ConvertFrom-Json
    $health = Invoke-RestMethod -Uri "http://127.0.0.1:$port1/health" -TimeoutSec 5
    Assert-Check ($health.service -eq 'PEIS.Report.Api') 'Background process remains available after launcher exits'
    $diagnostics = Invoke-RestMethod -Uri "http://127.0.0.1:$port1/internal/diagnostics/rendering" -TimeoutSec 5
    Assert-Check ($diagnostics.definitionSource -eq 'LegacySqlServer') 'Running process uses SQL Server mode'
    foreach ($route in @('/BaseInfo/Report/GetReportByJson', '/api/Reports/GetReportByJson')) {
        Assert-HttpStatus $port1 $route 'GET' 405
        Assert-HttpStatus $port1 $route 'POST' 400
    }
    $result = Invoke-Control $primaryRoot '启动服务.cmd'
    $sameState = Get-Content -LiteralPath (Join-Path $primaryRoot 'run\service.json') -Raw | ConvertFrom-Json
    Assert-Check ($result.Contains('already running') -and $firstState.ProcessId -eq $sameState.ProcessId) 'Repeated start keeps the same process'
    $result = Invoke-Control $primaryRoot '查看状态.cmd'
    Assert-Check ($result.Contains('Service is running')) 'Status launcher reports running state'

    $port2 = Get-FreePort
    Write-TestConfig $otherRoot $port2
    $null = Invoke-Control $otherRoot '启动服务.cmd'
    $otherState = Get-Content -LiteralPath (Join-Path $otherRoot 'run\service.json') -Raw | ConvertFrom-Json
    # A corrupt/stale PID record must not make Stop kill the other installation.
    @{ ProcessId = $otherState.ProcessId; Port = $port2 } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $primaryRoot 'run\service.json') -Encoding UTF8
    [IO.File]::WriteAllText((Join-Path $primaryRoot 'config.ini'), 'intentionally invalid', $configEncoding)
    $null = Invoke-Control $primaryRoot '关闭服务.cmd'
    Assert-Check (!(Get-Process -Id $firstState.ProcessId -ErrorAction SilentlyContinue)) 'Stop works even with a modified/broken config and stale state'
    $otherHealth = Invoke-RestMethod -Uri "http://127.0.0.1:$port2/health" -TimeoutSec 5
    Assert-Check ($otherHealth.status -eq 'ok') 'Stop does not terminate the other installation'
    $null = Invoke-Control $primaryRoot '关闭服务.cmd'
    $result = Invoke-Control $primaryRoot '查看状态.cmd'
    Assert-Check ($result.Contains('Service is stopped')) 'Repeated stop is harmless and status is stopped'

    $port3 = Get-FreePort
    Write-TestConfig $primaryRoot $port3
    $null = Invoke-Control $primaryRoot '启动服务.cmd'
    $newState = Get-Content -LiteralPath (Join-Path $primaryRoot 'run\service.json') -Raw | ConvertFrom-Json
    $newHealth = Invoke-RestMethod -Uri "http://127.0.0.1:$port3/health" -TimeoutSec 5
    Assert-Check ($newHealth.status -eq 'ok' -and $newState.Port -eq $port3) 'Restart applies the new port'
    $secretMatches = @(Get-ChildItem -LiteralPath (Join-Path $primaryRoot 'logs'), (Join-Path $primaryRoot 'run') -File | Select-String -SimpleMatch 'smoke<&!%=$;word')
    Assert-Check ($secretMatches.Count -eq 0) 'Launcher logs and state do not expose the synthetic password'
}
finally {
    if ($controlLock) { $controlLock.Dispose() }
    if ($portProbe) { $portProbe.Stop() }
    if ($ready) {
        foreach ($root in @($primaryRoot, $otherRoot)) {
            $null = Invoke-Control $root '关闭服务.cmd'
        }
    }
    $evidence | Set-Content -LiteralPath (Join-Path $testRoot 'checks.txt') -Encoding UTF8
}
Write-Host "All $($evidence.Count) checks passed. Evidence: $testRoot"
Write-Host 'No real database requests or patient report verification were performed.'
