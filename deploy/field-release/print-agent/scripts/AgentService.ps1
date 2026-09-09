[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('Start', 'Stop', 'Status')]
    [string]$Action
)

# Compatible with Windows PowerShell 5.1 and modern PowerShell
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$packageRoot = Split-Path -Parent $PSScriptRoot
$appRoot = Join-Path $packageRoot 'app'
$exePath = Join-Path $appRoot 'PEIS.PrintAgent.exe'
$stateRoot = Join-Path $packageRoot 'run'
$statePath = Join-Path $stateRoot 'service.json'
$lockHandle = $null
$exitCode = 0

function Get-OwnedProcess {
    @(Get-Process -Name 'PEIS.PrintAgent' -ErrorAction SilentlyContinue | Where-Object {
        try { $_.Path -and [string]::Equals($_.Path, $exePath, [StringComparison]::OrdinalIgnoreCase) }
        catch { $false }
    })
}

function Read-AgentConfig {
    $configPath = Join-Path $packageRoot 'config.ini'
    if (!(Test-Path -LiteralPath $configPath)) {
        $configPath = Join-Path $packageRoot 'agent.ini'
    }
    if (!(Test-Path -LiteralPath $configPath)) {
        throw 'Missing config.ini or agent.ini in package directory.'
    }

    $values = @{}
    foreach ($line in [IO.File]::ReadAllLines($configPath, [Text.Encoding]::UTF8)) {
        $trimmed = $line.Trim()
        if (!$trimmed -or $trimmed.StartsWith('#') -or $trimmed.StartsWith(';')) { continue }
        $separator = $trimmed.IndexOf('=')
        if ($separator -lt 0) { continue }
        $key = $trimmed.Substring(0, $separator).Trim()
        $val = $trimmed.Substring($separator + 1).Trim()
        $values[$key] = $val
    }

    if (!$values.ContainsKey('ServerUrl') -or [string]::IsNullOrWhiteSpace($values['ServerUrl'])) {
        throw 'Fill ServerUrl in config.ini before starting PrintAgent (e.g. ServerUrl=http://192.168.0.237:82).'
    }

    return $values
}

try {
    New-Item -ItemType Directory -Path $stateRoot -Force | Out-Null
    try {
        $lockHandle = [IO.File]::Open((Join-Path $stateRoot 'control.lock'), [IO.FileMode]::OpenOrCreate, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
    }
    catch {
        throw 'Another start/stop operation is running. Please try again shortly.'
    }

    $running = @(Get-OwnedProcess)

    if ($Action -eq 'Stop') {
        if ($running.Count -eq 0) {
            Write-Host 'PrintAgent service is stopped.'
        }
        else {
            foreach ($process in $running) {
                $current = Get-Process -Id $process.Id -ErrorAction SilentlyContinue
                if ($current -and $current.Path -eq $exePath -and $current.StartTime -eq $process.StartTime) {
                    Stop-Process -InputObject $current -Force
                    if (!$current.WaitForExit(10000)) {
                        throw 'Process did not stop within 10 seconds.'
                    }
                }
            }
            Remove-Item -LiteralPath $statePath -Force -ErrorAction SilentlyContinue
            Write-Host 'PrintAgent service stopped. Only this package installation was affected.'
        }
    }
    elseif ($Action -eq 'Status') {
        if ($running.Count -eq 0) {
            Write-Host 'PrintAgent service is stopped.'
        }
        else {
            Write-Host ('PrintAgent is running. PID: ' + (($running | ForEach-Object { $_.Id }) -join ', '))
            if (Test-Path -LiteralPath $statePath) {
                try {
                    $state = Get-Content -LiteralPath $statePath -Raw | ConvertFrom-Json
                    Write-Host "Server URL : $($state.ServerUrl)"
                    Write-Host "Station ID : $($state.StationId)"
                    Write-Host "Started At : $($state.StartedAt)"
                }
                catch { }
            }

            $logsRoot = Join-Path $packageRoot 'logs'
            $recentLog = Get-ChildItem -Path $logsRoot -Filter "agent-*.log" -ErrorAction SilentlyContinue | Sort-Object LastWriteTime -Descending | Select-Object -First 1
            if ($recentLog) {
                Write-Host "`nRecent log (${recentLog.Name}) tail:"
                Get-Content -LiteralPath $recentLog.FullName -Tail 5 -ErrorAction SilentlyContinue | ForEach-Object { Write-Host "  $_" }
            }
        }
    }
    elseif ($running.Count -gt 0) {
        Write-Host ('PrintAgent is already running. PID: ' + (($running | ForEach-Object { $_.Id }) -join ', '))
        Write-Host 'Use the stop script before applying configuration changes.'
    }
    else {
        if (!(Test-Path -LiteralPath $exePath)) {
            throw 'app/PEIS.PrintAgent.exe is missing. Extract the entire ZIP first.'
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
                $child = Start-Process -FilePath $exePath -WorkingDirectory $appRoot -WindowStyle Hidden -RedirectStandardOutput $stdout -RedirectStandardError $stderr -PassThru
            }
            finally {
                foreach ($key in $saved.Keys) { [Environment]::SetEnvironmentVariable($key, $saved[$key], 'Process') }
            }

            Start-Sleep -Milliseconds 1500
            $child.Refresh()
            if ($child.HasExited) {
                $errText = ''
                if (Test-Path -LiteralPath $stderr) { $errText = (Get-Content -LiteralPath $stderr -Raw).Trim() }
                if (!$errText -and (Test-Path -LiteralPath $stdout)) { $errText = (Get-Content -LiteralPath $stdout -Raw).Trim() }
                throw "PrintAgent exited unexpectedly (ExitCode: $($child.ExitCode)). $errText"
            }

            $state = @{ ProcessId = $child.Id; ServerUrl = $serverUrl; StationId = $stationId; StartedAt = $child.StartTime.ToUniversalTime().ToString('o') }
            $state | ConvertTo-Json | Set-Content -LiteralPath $statePath -Encoding UTF8

            Write-Host '============================================================'
            Write-Host "PrintAgent service started. PID: $($child.Id)"
            Write-Host "Server URL : $serverUrl"
            Write-Host "Station ID : $stationId"
            Write-Host "Logs Path  : $logsRoot"
            Write-Host '============================================================'
            Write-Host 'You may close this window.'
            Write-Host 'Use Status script to check status, Stop script to stop service.'
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
