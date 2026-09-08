[CmdletBinding()]
param(
    [ValidateSet('Production', 'Development', 'LocalDryRun')]
    [string]$Mode = 'Production',
    [string]$ApiDirectory = (Join-Path (Split-Path -Parent $PSScriptRoot) 'publish/report-api'),
    [string]$AgentDirectory = (Join-Path (Split-Path -Parent $PSScriptRoot) 'publish/print-agent'),
    [ValidateRange(1, 1024)]
    [int]$MinimumFreeDiskGB = 2,
    [string]$AgentIdentityDirectory = (Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::CommonApplicationData)) 'PEIS/PrintAgent'),
    [switch]$Json
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# This script performs configuration and temporary-fixture checks only. It does not alter production configuration,
# database state, network settings, printer drivers, Windows security policy, scheduled tasks, or physical printers.
$checks = [System.Collections.Generic.List[object]]::new()
$isWindowsPlatform = $env:OS -eq 'Windows_NT'

function Add-Check([string]$Name, [string]$Status, [string]$Detail) {
    $checks.Add([pscustomobject][ordered]@{ Name = $Name; Status = $Status; Detail = $Detail })
}

function Get-ConfigProperty([object]$Object, [string[]]$Path) {
    $current = $Object
    foreach ($segment in $Path) {
        if ($null -eq $current) { return $null }
        $property = $current.PSObject.Properties[$segment]
        if ($null -eq $property) { return $null }
        $current = $property.Value
    }
    return $current
}

function Get-ConfiguredValue([object]$Config, [string[]]$Path, [string]$EnvironmentName) {
    $environmentValue = [Environment]::GetEnvironmentVariable($EnvironmentName, 'Process')
    if ([string]::IsNullOrWhiteSpace($environmentValue)) {
        $environmentValue = [Environment]::GetEnvironmentVariable($EnvironmentName, 'User')
    }
    if ([string]::IsNullOrWhiteSpace($environmentValue)) {
        $environmentValue = [Environment]::GetEnvironmentVariable($EnvironmentName, 'Machine')
    }
    if (![string]::IsNullOrWhiteSpace($environmentValue)) { return $environmentValue }
    return Get-ConfigProperty $Config $Path
}

function Test-NonPlaceholder([object]$Value) {
    if ($null -eq $Value) { return $false }
    $text = [string]$Value
    return ![string]::IsNullOrWhiteSpace($text) -and $text -notmatch '^<[^>]+>$'
}

function Test-WritableDirectory([string]$Path) {
    try {
        [IO.Directory]::CreateDirectory($Path) | Out-Null
        $probe = Join-Path $Path ('.preflight-{0}.tmp' -f [Guid]::NewGuid().ToString('N'))
        [IO.File]::WriteAllText($probe, 'preflight')
        Remove-Item -LiteralPath $probe -Force
        return $true
    }
    catch { return $false }
}

function Get-AvailableDiskGB([string]$Path) {
    $resolved = [IO.Path]::GetFullPath($Path)
    $root = [IO.Path]::GetPathRoot($resolved)
    if ([string]::IsNullOrWhiteSpace($root)) { return 0 }
    $drive = [IO.DriveInfo]::new($root)
    return [math]::Floor($drive.AvailableFreeSpace / 1GB)
}

function Test-StationId([object]$Value) {
    $stationId = ([string]$Value).Trim()
    return $stationId -match '^[A-Za-z0-9][A-Za-z0-9_-]{0,63}$'
}

function Get-InstalledPrinterNames {
    if (!$isWindowsPlatform) { return @() }
    try {
        return @(Get-CimInstance -ClassName Win32_Printer -ErrorAction Stop | ForEach-Object { $_.Name })
    }
    catch { return @() }
}

function Test-CommandBackend([object]$Backend) {
    $prohibited = @('cmd.exe', 'command.com', 'powershell.exe', 'pwsh.exe', 'wscript.exe', 'cscript.exe', 'rundll32.exe')
    $executable = ([string](Get-ConfigProperty $Backend @('Executable'))).Trim()
    $template = [string](Get-ConfigProperty $Backend @('ArgumentsTemplate'))
    if ([string]::IsNullOrWhiteSpace($executable)) { return 'FAIL: Executable is empty' }
    if (![IO.Path]::IsPathFullyQualified($executable)) { return 'FAIL: Executable is not an absolute path' }
    if ($prohibited -contains [IO.Path]::GetFileName($executable).ToLowerInvariant()) { return 'FAIL: Shell or script host is forbidden' }
    if (!(Test-Path -LiteralPath $executable -PathType Leaf)) { return 'FAIL: Executable does not exist' }
    if ($template -match '[\x00-\x1F]') { return 'FAIL: Arguments template contains a control character' }
    $opened = @($template.ToCharArray() | Where-Object { $_ -eq '"' }).Count
    if (($opened % 2) -ne 0) { return 'FAIL: Arguments template contains an unmatched quote' }
    $placeholders = [regex]::Matches($template, '\{[^}]*\}') | ForEach-Object { $_.Value }
    foreach ($placeholder in $placeholders) {
        if (@('{file}', '{printer}', '{copies}', '{duplex}') -notcontains $placeholder) {
            return "FAIL: Unsupported placeholder $placeholder"
        }
    }
    return 'VALIDATED_COMMAND'
}

function Invoke-SqliteSmoke([string]$ApiRoot) {
    $tempRoot = Join-Path ([IO.Path]::GetTempPath()) ('peis-preflight-sqlite-' + [Guid]::NewGuid().ToString('N'))
    try {
        [IO.Directory]::CreateDirectory($tempRoot) | Out-Null
        $assembly = Get-ChildItem -LiteralPath $ApiRoot -Filter 'Microsoft.Data.Sqlite.dll' -File -Recurse -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($null -eq $assembly) { return 'FAIL: Microsoft.Data.Sqlite.dll is not present in Report API publish output' }
        [void][Reflection.Assembly]::LoadFrom($assembly.FullName)
        $databasePath = Join-Path $tempRoot 'preflight.db'
        $connection = [Microsoft.Data.Sqlite.SqliteConnection]::new("Data Source=$databasePath")
        $connection.Open()
        $command = $connection.CreateCommand()
        $command.CommandText = 'CREATE TABLE preflight_state (id TEXT PRIMARY KEY, value TEXT NOT NULL); INSERT INTO preflight_state (id, value) VALUES (''smoke'', ''persisted'');'
        [void]$command.ExecuteNonQuery()
        $connection.Dispose()
        $connection = [Microsoft.Data.Sqlite.SqliteConnection]::new("Data Source=$databasePath")
        $connection.Open()
        $command = $connection.CreateCommand()
        $command.CommandText = "SELECT value FROM preflight_state WHERE id = 'smoke';"
        $value = [string]$command.ExecuteScalar()
        $connection.Dispose()
        if ($value -ne 'persisted') { return 'FAIL: SQLite reopen did not recover written state' }
        return 'PASS'
    }
    catch { return "FAIL: SQLite temporary smoke failed ($($_.Exception.Message))" }
    finally {
        if (Test-Path -LiteralPath $tempRoot) { Remove-Item -LiteralPath $tempRoot -Recurse -Force -ErrorAction SilentlyContinue }
    }
}

function Get-ArtifactSignature([Guid]$ArtifactId, [string]$AgentId, [long]$Expires, [string]$Key) {
    $hmac = [Security.Cryptography.HMACSHA256]::new([Text.Encoding]::UTF8.GetBytes($Key))
    try {
        $payload = ("{0}`n{1}`n{2}" -f $ArtifactId.ToString('N'), $AgentId, $Expires)
        return [Convert]::ToBase64String($hmac.ComputeHash([Text.Encoding]::UTF8.GetBytes($payload))).TrimEnd('=').Replace('+', '-').Replace('/', '_')
    }
    finally { $hmac.Dispose() }
}

function Invoke-ArtifactSmoke([string]$SigningKey) {
    $tempRoot = Join-Path ([IO.Path]::GetTempPath()) ('peis-preflight-artifact-' + [Guid]::NewGuid().ToString('N'))
    try {
        [IO.Directory]::CreateDirectory($tempRoot) | Out-Null
        $active = [Guid]::NewGuid()
        $expired = [Guid]::NewGuid()
        $minimalPdf = [Text.Encoding]::ASCII.GetBytes("%PDF-1.4`n1 0 obj<<>>endobj`ntrailer<<>>`n%%EOF`n")
        foreach ($id in @($active, $expired)) {
            [IO.File]::WriteAllBytes((Join-Path $tempRoot ($id.ToString('N') + '.pdf')), $minimalPdf)
            [IO.File]::WriteAllText((Join-Path $tempRoot ($id.ToString('N') + '.name')), 'synthetic.pdf')
        }
        $expiredPath = Join-Path $tempRoot ($expired.ToString('N') + '.pdf')
        $activePath = Join-Path $tempRoot ($active.ToString('N') + '.pdf')
        (Get-Item -LiteralPath $expiredPath).LastWriteTimeUtc = [DateTime]::UtcNow.AddHours(-2)
        (Get-Item -LiteralPath $activePath).LastWriteTimeUtc = [DateTime]::UtcNow.AddHours(-2)
        $activeIds = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
        [void]$activeIds.Add($active.ToString('N'))
        Get-ChildItem -LiteralPath $tempRoot -Filter '*.pdf' | ForEach-Object {
            $id = $_.BaseName
            if ($_.LastWriteTimeUtc -lt [DateTime]::UtcNow.AddHours(-1) -and !$activeIds.Contains($id)) {
                Remove-Item -LiteralPath $_.FullName -Force
                Remove-Item -LiteralPath (Join-Path $tempRoot ($id + '.name')) -Force -ErrorAction SilentlyContinue
            }
        }
        if ((Test-Path -LiteralPath $expiredPath) -or !(Test-Path -LiteralPath $activePath)) {
            return 'FAIL: Synthetic retention cleanup did not preserve active artifact policy'
        }
        $agentId = 'preflight-agent'
        $expires = [DateTimeOffset]::UtcNow.AddMinutes(5).ToUnixTimeSeconds()
        $signature = Get-ArtifactSignature $active $agentId $expires $SigningKey
        $correct = $signature -eq (Get-ArtifactSignature $active $agentId $expires $SigningKey)
        $wrongAgent = $signature -ne (Get-ArtifactSignature $active 'wrong-agent' $expires $SigningKey)
        $expiredAt = [DateTimeOffset]::UtcNow.AddMinutes(-1).ToUnixTimeSeconds()
        $expiredSignature = Get-ArtifactSignature $active $agentId $expiredAt $SigningKey
        $expiredDenied = ([DateTimeOffset]::UtcNow.ToUnixTimeSeconds() -gt $expiredAt) -and ![string]::IsNullOrWhiteSpace($expiredSignature)
        if (!$correct -or !$wrongAgent -or !$expiredDenied) { return 'FAIL: Synthetic signature policy validation failed' }
        return 'PASS'
    }
    catch { return "FAIL: Synthetic artifact smoke failed ($($_.Exception.Message))" }
    finally {
        if (Test-Path -LiteralPath $tempRoot) { Remove-Item -LiteralPath $tempRoot -Recurse -Force -ErrorAction SilentlyContinue }
    }
}

$ApiDirectory = [IO.Path]::GetFullPath($ApiDirectory)
$AgentDirectory = [IO.Path]::GetFullPath($AgentDirectory)
$apiConfigPath = Join-Path $ApiDirectory 'appsettings.Production.json'
if (!(Test-Path -LiteralPath $apiConfigPath)) { $apiConfigPath = Join-Path $ApiDirectory 'appsettings.json' }
$agentConfigPath = Join-Path $AgentDirectory 'appsettings.Production.json'
if (!(Test-Path -LiteralPath $agentConfigPath)) { $agentConfigPath = Join-Path $AgentDirectory 'appsettings.json' }

$apiConfig = $null
$agentConfig = $null
try { if (Test-Path -LiteralPath $apiConfigPath) { $apiConfig = Get-Content -LiteralPath $apiConfigPath -Raw | ConvertFrom-Json } } catch { Add-Check 'API Runtime' 'FAIL' 'API appsettings JSON is invalid' }
try { if (Test-Path -LiteralPath $agentConfigPath) { $agentConfig = Get-Content -LiteralPath $agentConfigPath -Raw | ConvertFrom-Json } } catch { Add-Check 'PrintAgent' 'FAIL' 'PrintAgent appsettings JSON is invalid' }

$apiDll = Join-Path $ApiDirectory 'PEIS.Report.Api.dll'
if ($null -ne $apiConfig -and (Test-Path -LiteralPath $apiDll -PathType Leaf)) {
    Add-Check 'API Runtime' 'PASS' 'Report API publish directory, DLL and appsettings are present'
}
elseif (@($checks | Where-Object Name -eq 'API Runtime').Count -eq 0) { Add-Check 'API Runtime' 'FAIL' 'Report API publish directory, DLL or appsettings is missing' }

$apiRuntime = Join-Path $ApiDirectory '.runtime'
$artifactRuntime = Join-Path $apiRuntime 'pdf-artifacts'
Add-Check 'Runtime Directory' ($(if (Test-WritableDirectory $apiRuntime) { 'PASS' } else { 'FAIL' })) 'Writable runtime probe only'
Add-Check 'SQLite Directory' ($(if (Test-WritableDirectory (Split-Path -Parent (Join-Path $ApiDirectory ([string](Get-ConfigProperty $apiConfig @('PrintPersistence', 'DatabasePath')))))) { 'PASS' } else { 'FAIL' })) 'Writable SQLite parent probe only'
Add-Check 'Artifact Directory' ($(if (Test-WritableDirectory $artifactRuntime) { 'PASS' } else { 'FAIL' })) 'Writable artifact directory probe only'
$freeGB = Get-AvailableDiskGB $ApiDirectory
Add-Check 'Disk Space' ($(if ($freeGB -ge $MinimumFreeDiskGB) { 'PASS' } else { 'FAIL' })) ("Available {0} GB; required {1} GB" -f $freeGB, $MinimumFreeDiskGB)

$securityKeys = @(
    @{ Name = 'RegistrationToken'; Path = @('PrintAgentSecurity', 'RegistrationToken'); Environment = 'PrintAgentSecurity__RegistrationToken' },
    @{ Name = 'AccessToken'; Path = @('InternalApiSecurity', 'AccessToken'); Environment = 'InternalApiSecurity__AccessToken' },
    @{ Name = 'SigningKey'; Path = @('ArtifactAccess', 'SigningKey'); Environment = 'ArtifactAccess__SigningKey' }
)
$securityPass = $true
foreach ($securityKey in $securityKeys) {
    $value = Get-ConfiguredValue $apiConfig $securityKey.Path $securityKey.Environment
    $present = Test-NonPlaceholder $value
    if ($Mode -eq 'Production' -and !$present) { $securityPass = $false }
    Add-Check ("Security " + $securityKey.Name) ($(if ($present) { 'PASS' } elseif ($Mode -eq 'Production') { 'FAIL' } else { 'NOT_CONFIGURED' })) ("{0}: {1}" -f $securityKey.Name, $(if ($present) { 'PRESENT' } else { 'MISSING' }))
}
Add-Check 'Security Config' ($(if ($securityPass -or $Mode -ne 'Production') { 'PASS' } else { 'FAIL' })) 'Secrets are never emitted by preflight'

$apiUrls = [string](Get-ConfiguredValue $apiConfig @('Urls') 'ASPNETCORE_URLS')
$agentServerUrl = [string](Get-ConfiguredValue $agentConfig @('Agent', 'ServerUrl') 'Agent__ServerUrl')
$urlsPass = $Mode -ne 'Production' -or ($apiUrls -match '^https://' -and $agentServerUrl -match '^https://')
Add-Check 'HTTPS' ($(if ($urlsPass) { 'PASS' } else { 'FAIL' })) $(if ($Mode -eq 'Production') { 'Production requires https:// API and Agent ServerUrl' } else { 'Development or LocalDryRun permits HTTP only for controlled preflight' })

$agentExe = Join-Path $AgentDirectory 'PEIS.PrintAgent.exe'
if ($null -ne $agentConfig -and (Test-Path -LiteralPath $agentExe -PathType Leaf)) { Add-Check 'PrintAgent' 'PASS' 'PrintAgent executable and appsettings are present' }
elseif (@($checks | Where-Object Name -eq 'PrintAgent').Count -eq 0) { Add-Check 'PrintAgent' 'FAIL' 'PrintAgent publish directory, executable or appsettings is missing' }

$identityDirectory = [IO.Path]::GetFullPath($AgentIdentityDirectory)
$identityPath = Join-Path $identityDirectory 'agent-id.txt'
$identityWritable = Test-WritableDirectory $identityDirectory
$identityDetail = if (Test-Path -LiteralPath $identityPath) { 'AgentId: PRESENT (not read or changed)' } else { 'AgentId: ABSENT (not created by preflight)' }
Add-Check 'Agent Identity' ($(if ($identityWritable) { 'PASS' } else { 'FAIL' })) $identityDetail

$agent = Get-ConfigProperty $agentConfig @('Agent')
$agentRegistrationToken = Get-ConfiguredValue $agentConfig @('Agent', 'RegistrationToken') 'Agent__RegistrationToken'
$agentRegistrationPresent = Test-NonPlaceholder $agentRegistrationToken
Add-Check 'Agent Registration Token' ($(if ($agentRegistrationPresent) { 'PASS' } elseif ($Mode -eq 'Production') { 'FAIL' } else { 'NOT_CONFIGURED' })) ("RegistrationToken: {0}" -f $(if ($agentRegistrationPresent) { 'PRESENT' } else { 'MISSING' }))
$stationId = Get-ConfiguredValue $agentConfig @('Agent', 'StationId') 'Agent__StationId'
Add-Check 'StationId' ($(if (Test-StationId $stationId) { 'PASS' } else { 'FAIL' })) 'StationId must be 1-64 ASCII letters, digits, underscore or hyphen without whitespace'

$installedPrinters = Get-InstalledPrinterNames
$bindings = Get-ConfigProperty $agent @('PrinterBindings')
$bindingProperties = @(if ($null -eq $bindings) { @() } else { @($bindings.PSObject.Properties) })
if ($bindingProperties.Count -eq 0) {
    Add-Check 'A4 Binding' 'NOT_CONFIGURED' 'A4_GUIDE binding is not configured'
    Add-Check 'Barcode Binding' 'NOT_CONFIGURED' 'BARCODE binding is not configured'
}
else {
    foreach ($role in @('A4_GUIDE', 'BARCODE')) {
        $binding = Get-ConfigProperty $bindings @($role)
        if ([string]::IsNullOrWhiteSpace([string]$binding)) { Add-Check ("{0} Binding" -f $(if ($role -eq 'A4_GUIDE') { 'A4' } else { 'Barcode' })) 'NOT_CONFIGURED' "$role is not configured" }
        elseif (!$isWindowsPlatform) { Add-Check ("{0} Binding" -f $(if ($role -eq 'A4_GUIDE') { 'A4' } else { 'Barcode' })) 'FAIL' 'Installed printer lookup requires Windows' }
        elseif ($installedPrinters -contains [string]$binding) { Add-Check ("{0} Binding" -f $(if ($role -eq 'A4_GUIDE') { 'A4' } else { 'Barcode' })) 'PASS' "$role -> FOUND" }
        else { Add-Check ("{0} Binding" -f $(if ($role -eq 'A4_GUIDE') { 'A4' } else { 'Barcode' })) 'FAIL' "$role -> NOT FOUND" }
    }
    foreach ($property in $bindingProperties | Where-Object { $_.Name -notin @('A4_GUIDE', 'BARCODE') }) {
        $status = if ($isWindowsPlatform -and $installedPrinters -contains [string]$property.Value) { 'PASS' } else { 'FAIL' }
        Add-Check ("Printer Binding {0}" -f $property.Name) $status ("{0} -> {1}" -f $property.Name, $(if ($status -eq 'PASS') { 'FOUND' } else { 'NOT FOUND' }))
    }
}

$backend = Get-ConfigProperty $agent @('PrintBackend')
$backendMode = [string](Get-ConfigProperty $backend @('Mode'))
if ($backendMode -eq 'DryRun') { Add-Check 'Print Backend' 'DRY_RUN' 'SAFE_FOR_PREFLIGHT; no print command is executed' }
elseif ($backendMode -eq 'Command') {
    $commandResult = Test-CommandBackend $backend
    Add-Check 'Print Backend' ($(if ($commandResult -eq 'VALIDATED_COMMAND') { 'VALIDATED_COMMAND' } else { 'FAIL' })) $commandResult
}
else { Add-Check 'Print Backend' 'FAIL' 'PrintBackend:Mode must be DryRun or Command' }

$persistenceResult = Invoke-SqliteSmoke $ApiDirectory
Add-Check 'Persistence' ($(if ($persistenceResult -eq 'PASS') { 'PASS' } else { 'FAIL' })) $persistenceResult
$signingKey = Get-ConfiguredValue $apiConfig @('ArtifactAccess', 'SigningKey') 'ArtifactAccess__SigningKey'
if (Test-NonPlaceholder $signingKey) {
    $artifactResult = Invoke-ArtifactSmoke ([string]$signingKey)
    Add-Check 'Artifact Security' ($(if ($artifactResult -eq 'PASS') { 'PASS' } else { 'FAIL' })) $artifactResult
}
elseif ($Mode -eq 'Production') { Add-Check 'Artifact Security' 'FAIL' 'SigningKey is missing; no synthetic signature validation performed' }
else { Add-Check 'Artifact Security' 'NOT_CONFIGURED' 'SigningKey is absent in non-production mode' }

Add-Check 'Legacy SQL' 'NOT_CHECKED' 'Requires explicitly approved read-only REPORTPLATFORM_TEST_SQLSERVER connection'
Add-Check 'FastReport' 'NOT_CHECKED' 'Requires explicitly approved REPORTPLATFORM_TEST_FASTREPORT evidence'

$blocking = @($checks | Where-Object { $_.Status -eq 'FAIL' })
$final = if (@($blocking).Count -eq 0) { 'READY_FOR_DRY_RUN' } else { 'HOLD' }
$result = [ordered]@{
    Title = 'ReportPlatform Field Preflight'
    Mode = $Mode
    GeneratedAtUtc = [DateTime]::UtcNow.ToString('O')
    ApiRuntime = ($checks | Where-Object Name -eq 'API Runtime' | Select-Object -Last 1).Status
    SecurityConfig = ($checks | Where-Object Name -eq 'Security Config' | Select-Object -Last 1).Status
    HTTPS = ($checks | Where-Object Name -eq 'HTTPS' | Select-Object -Last 1).Status
    Persistence = ($checks | Where-Object Name -eq 'Persistence' | Select-Object -Last 1).Status
    ArtifactSecurity = ($checks | Where-Object Name -eq 'Artifact Security' | Select-Object -Last 1).Status
    PrintAgent = ($checks | Where-Object Name -eq 'PrintAgent' | Select-Object -Last 1).Status
    AgentIdentity = ($checks | Where-Object Name -eq 'Agent Identity' | Select-Object -Last 1).Status
    StationId = ($checks | Where-Object Name -eq 'StationId' | Select-Object -Last 1).Status
    A4Binding = ($checks | Where-Object Name -eq 'A4 Binding' | Select-Object -Last 1).Status
    BarcodeBinding = ($checks | Where-Object Name -eq 'Barcode Binding' | Select-Object -Last 1).Status
    PrintBackend = ($checks | Where-Object Name -eq 'Print Backend' | Select-Object -Last 1).Status
    LegacySql = 'NOT_CHECKED'
    FastReport = 'NOT_CHECKED'
    Final = $final
    Checks = @($checks)
}

if ($Json) {
    $result | ConvertTo-Json -Depth 8
}
else {
    Write-Host 'ReportPlatform Field Preflight'
    foreach ($check in $checks) { Write-Host ('{0}: {1} — {2}' -f $check.Name, $check.Status, $check.Detail) }
    Write-Host ('FINAL: ' + $final)
}

if ($final -eq 'HOLD') { exit 1 }
