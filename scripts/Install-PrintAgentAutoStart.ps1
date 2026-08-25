[CmdletBinding(SupportsShouldProcess)]
param(
    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string]$AgentDirectory,

    [Parameter(Mandatory = $true)]
    [ValidatePattern('^[A-Za-z0-9][A-Za-z0-9_-]{0,63}$')]
    [string]$StationId,

    [Parameter(Mandatory = $true)]
    [ValidatePattern('^https?://')]
    [string]$ServerUrl,

    [Parameter(Mandatory = $true)]
    [hashtable]$PrinterBindings,

    [string]$RegistrationToken = '',
    [switch]$AllowInsecureHttp,
    [switch]$AllowInsecureRegistration,
    [string]$TaskName = 'PEIS PrintAgent'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$AgentDirectory = [IO.Path]::GetFullPath($AgentDirectory)
if (!$AllowInsecureHttp -and !$ServerUrl.StartsWith('https://', [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Production installation requires an https:// ServerUrl. Use -AllowInsecureHttp only for a controlled development test.'
}
if (!$AllowInsecureRegistration -and [string]::IsNullOrWhiteSpace($RegistrationToken)) {
    throw 'Production installation requires a non-empty RegistrationToken. Use -AllowInsecureRegistration only for a controlled development test.'
}
if ($PrinterBindings.Count -eq 0) {
    throw 'At least one logical PrinterBindings entry is required.'
}
$agentExecutable = Join-Path $AgentDirectory 'PEIS.PrintAgent.exe'
$configPath = Join-Path $AgentDirectory 'appsettings.Production.json'
if (!(Test-Path -LiteralPath $agentExecutable)) {
    throw "PrintAgent executable was not found: $agentExecutable"
}

$config = if (Test-Path -LiteralPath $configPath) {
    Get-Content -LiteralPath $configPath -Raw | ConvertFrom-Json
}
else {
    [pscustomobject]@{ Agent = [pscustomobject]@{} }
}
if ($null -eq $config.Agent) {
    $config | Add-Member -NotePropertyName Agent -NotePropertyValue ([pscustomobject]@{})
}
function Set-JsonProperty([object]$Object, [string]$Name, [object]$Value) {
    $property = $Object.PSObject.Properties[$Name]
    if ($null -eq $property) {
        $Object | Add-Member -NotePropertyName $Name -NotePropertyValue $Value
    }
    else {
        $property.Value = $Value
    }
}
Set-JsonProperty $config.Agent 'ServerUrl' $ServerUrl.TrimEnd('/')
Set-JsonProperty $config.Agent 'StationId' $StationId
# Blank causes the agent to create and retain its ProgramData installation GUID.
Set-JsonProperty $config.Agent 'AgentId' ''
Set-JsonProperty $config.Agent 'RegistrationToken' $RegistrationToken
Set-JsonProperty $config.Agent 'PrinterBindings' ([pscustomobject]$PrinterBindings)

$utf8NoBom = [System.Text.UTF8Encoding]::new($false)
[IO.File]::WriteAllText($configPath, ($config | ConvertTo-Json -Depth 16), $utf8NoBom)

$action = New-ScheduledTaskAction -Execute $agentExecutable -Argument '--environment Production' -WorkingDirectory $AgentDirectory
$taskUser = [Environment]::UserDomainName + '\' + [Environment]::UserName
$trigger = New-ScheduledTaskTrigger -AtLogOn -User $taskUser
$settings = New-ScheduledTaskSettingsSet -RestartCount 3 -RestartInterval (New-TimeSpan -Minutes 1) -ExecutionTimeLimit (New-TimeSpan -Days 3650) -StartWhenAvailable

if ($PSCmdlet.ShouldProcess("Scheduled task $TaskName", "Install PEIS PrintAgent auto-start")) {
    Register-ScheduledTask -TaskName $TaskName -Action $action -Trigger $trigger -Settings $settings -Description "PEIS workstation print agent for $StationId" -Force | Out-Null
    Start-ScheduledTask -TaskName $TaskName
}

Write-Host "PrintAgent configured. Station: $StationId; Task: $TaskName"
Write-Host 'A stable agent identifier is created under ProgramData on first startup.'
Write-Host 'The registration token is saved only in local Production configuration. Do not commit it.'
Write-Host 'For controlled development only, -AllowInsecureHttp and -AllowInsecureRegistration explicitly opt into insecure settings.'
