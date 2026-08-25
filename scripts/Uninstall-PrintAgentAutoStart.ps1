[CmdletBinding(SupportsShouldProcess)]
param(
    [string]$TaskName = 'PEIS PrintAgent',
    [string]$AgentDirectory = '',
    [switch]$RemoveLocalState
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# This rollback helper only removes the PEIS PrintAgent scheduled task. It never changes printers, printer drivers,
# PEIS application files, databases, firewall settings, or Windows security policy.
$task = Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue
if ($null -ne $task -and $PSCmdlet.ShouldProcess("Scheduled task $TaskName", 'Stop and unregister PEIS PrintAgent auto-start')) {
    Stop-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue
    Unregister-ScheduledTask -TaskName $TaskName -Confirm:$false
}

$stateDirectory = Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::CommonApplicationData)) 'PEIS/PrintAgent'
if ($RemoveLocalState) {
    if ($PSCmdlet.ShouldProcess($stateDirectory, 'Delete PEIS PrintAgent local AgentId and local state')) {
        if (Test-Path -LiteralPath $stateDirectory) {
            Remove-Item -LiteralPath $stateDirectory -Recurse -Force
        }
    }
    Write-Host 'Local AgentId and PrintAgent state removal was explicitly requested.'
}
else {
    Write-Host 'Local AgentId and PrintAgent state were preserved by default.'
}

if (![string]::IsNullOrWhiteSpace($AgentDirectory)) {
    Write-Host "Program files were not deleted: $AgentDirectory"
}
Write-Host 'No printers, printer drivers, PEIS files, databases, network settings, or security policies were changed.'
