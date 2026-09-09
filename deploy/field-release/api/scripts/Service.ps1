[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('Start', 'Stop', 'Status')]
    [string]$Action
)

# Compatible with the Windows PowerShell 5.1 shipped with Windows.
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$packageRoot = Split-Path -Parent $PSScriptRoot
$appRoot = Join-Path $packageRoot 'app'
$exePath = Join-Path $appRoot 'PEIS.Report.Api.exe'
$stateRoot = Join-Path $packageRoot 'run'
$statePath = Join-Path $stateRoot 'service.json'
$lockHandle = $null
$exitCode = 0

function Get-OwnedProcess {
    # Always check the full executable path: never stop another installation or
    # an unrelated process whose PID happens to have been reused.
    @(Get-Process -Name 'PEIS.Report.Api' -ErrorAction SilentlyContinue | Where-Object {
        try { $_.Path -and [string]::Equals($_.Path, $exePath, [StringComparison]::OrdinalIgnoreCase) }
        catch { $false }
    })
}

function Read-ServiceConfig {
    $configPath = Join-Path $packageRoot 'config.ini'
    $values = @{}
    foreach ($line in [IO.File]::ReadAllLines($configPath, [Text.Encoding]::UTF8)) {
        $trimmed = $line.Trim()
        if (!$trimmed -or $trimmed.StartsWith('#') -or $trimmed.StartsWith(';')) { continue }
        $separator = $trimmed.IndexOf('=')
        if ($separator -lt 0) { throw 'Invalid config.ini line. Use Name=Value.' }
        $key = $trimmed.Substring(0, $separator).Trim()
        if ($key -notin @('Port', 'ConnectionString')) { throw 'Unknown setting in config.ini. Only Port and ConnectionString are supported.' }
        if ($values.ContainsKey($key)) { throw "Duplicate setting: $key" }
        $values[$key] = $trimmed.Substring($separator + 1).Trim()
    }
    $portNumber = 0
    if (!$values.ContainsKey('Port') -or ![int]::TryParse($values['Port'], [ref]$portNumber) -or $portNumber -lt 1 -or $portNumber -gt 65535) {
        throw 'Port must be a number from 1 to 65535 in config.ini.'
    }
    if (!$values.ContainsKey('ConnectionString') -or [string]::IsNullOrWhiteSpace($values['ConnectionString'])) {
        throw 'Fill ConnectionString in config.ini before starting the service.'
    }
    # Parse without opening the database or printing any part of the secret.
    try {
        $connection = New-Object System.Data.Common.DbConnectionStringBuilder
        # DbConnectionStringBuilder implements IDictionary: PowerShell's property
        # adapter would otherwise add a key literally named ConnectionString.
        $connection.set_ConnectionString($values['ConnectionString'])
        $hasServer = @('Server', 'Data Source', 'Address', 'Addr', 'Network Address') | Where-Object { $connection.ContainsKey($_) -and ![string]::IsNullOrWhiteSpace($connection[$_]) }
        $hasDatabase = @('Database', 'Initial Catalog') | Where-Object { $connection.ContainsKey($_) -and ![string]::IsNullOrWhiteSpace($connection[$_]) }
        if (!$hasServer -or !$hasDatabase) { throw 'Missing server/database.' }
    }
    catch { throw 'Invalid ConnectionString. Include Server and Database; check the SQL Server connection-string syntax.' }
    return @{ Port = $portNumber; ConnectionString = $values['ConnectionString'] }
}

try {
    New-Item -ItemType Directory -Path $stateRoot -Force | Out-Null
    try {
        $lockHandle = [IO.File]::Open((Join-Path $stateRoot 'control.lock'), [IO.FileMode]::OpenOrCreate, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
    }
    catch { throw 'Another start/stop operation is running. Please try again shortly.' }

    $running = @(Get-OwnedProcess)
    if ($Action -eq 'Stop') {
        foreach ($process in $running) {
            # Recheck identity just before termination, including start time.
            $current = Get-Process -Id $process.Id -ErrorAction SilentlyContinue
            if ($current -and $current.Path -eq $exePath -and $current.StartTime -eq $process.StartTime) {
                Stop-Process -InputObject $current -Force
                if (!$current.WaitForExit(15000)) { throw 'The process did not stop within 15 seconds.' }
            }
        }
        Remove-Item -LiteralPath $statePath -Force -ErrorAction SilentlyContinue
        Write-Host 'Service stopped. Only this package installation was affected.'
    }
    elseif ($Action -eq 'Status') {
        if ($running.Count -eq 0) { Write-Host 'Service is stopped.' }
        else {
            Write-Host ('Service is running. PID: ' + (($running | ForEach-Object { $_.Id }) -join ', '))
            if (Test-Path -LiteralPath $statePath) {
                $state = Get-Content -LiteralPath $statePath -Raw | ConvertFrom-Json
                Write-Host "URL at last start: http://127.0.0.1:$($state.Port)/BaseInfo/Report/GetReportByJson"
            }
        }
    }
    elseif ($running.Count -gt 0) {
        Write-Host 'Service is already running. Stop it before applying configuration changes.'
    }
    else {
        if (!(Test-Path -LiteralPath $exePath)) { throw 'app/PEIS.Report.Api.exe is missing. Extract the entire ZIP first.' }
        $config = Read-ServiceConfig
        $port = $config.Port
        $probe = New-Object Net.Sockets.TcpListener([Net.IPAddress]::Any, $port)
        try {
            $probe.Server.ExclusiveAddressUse = $true
            $probe.Start()
        }
        catch { throw "Port $port is unavailable. Change Port in config.ini or stop its existing owner yourself." }
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
            $state = @{ ProcessId = $child.Id; Port = $port; StartedAt = $child.StartTime.ToUniversalTime().ToString('o') }
            $state | ConvertTo-Json | Set-Content -LiteralPath $statePath -Encoding UTF8
            $deadline = [DateTime]::UtcNow.AddSeconds(45)
            $healthy = $false
            while ([DateTime]::UtcNow -lt $deadline) {
                $child.Refresh()
                if ($child.HasExited) { throw 'Service exited during startup. Check the latest files in logs.' }
                try {
                    $response = Invoke-WebRequest -UseBasicParsing -Uri "http://127.0.0.1:$port/health" -TimeoutSec 2
                    $health = $response.Content | ConvertFrom-Json
                    if ($response.StatusCode -eq 200 -and $health.service -eq 'PEIS.Report.Api') { $healthy = $true; break }
                }
                catch { }
                Start-Sleep -Milliseconds 300
            }
            $child.Refresh()
            if (!$healthy -or $child.HasExited) { throw 'Service did not become healthy. Check the latest files in logs.' }
            Write-Host "Service started. PID: $($child.Id)"
            Write-Host "POST http://127.0.0.1:$port/BaseInfo/Report/GetReportByJson"
            Write-Host 'For LAN access, replace 127.0.0.1 with this server IP.'
            Write-Host 'Health check passed. Database connectivity and real reports still require a business request.'
            Write-Host 'You may close this window. Use the stop script to stop the background process.'
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
    Write-Host ('ERROR: ' + $_.Exception.Message) -ForegroundColor Red
    $exitCode = 1
}
finally {
    if ($lockHandle) { $lockHandle.Dispose() }
}
exit $exitCode
