[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^[0-9a-fA-F]{7,40}$')]
    [string]$ShortSha,
    [string]$PublishRoot = (Join-Path (Split-Path -Parent $PSScriptRoot) 'publish'),
    [string]$OutputRoot = (Join-Path (Split-Path -Parent $PSScriptRoot) 'release')
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$packageName = "ReportPlatform_RC1_$ShortSha"
$packageDirectory = Join-Path $OutputRoot $packageName
$zipPath = Join-Path $OutputRoot ($packageName + '_win-x64.zip')
$apiSource = Join-Path $PublishRoot 'report-api'
$agentSource = Join-Path $PublishRoot 'print-agent'

if (!(Test-Path -LiteralPath (Join-Path $apiSource 'PEIS.Report.Api.dll'))) { throw 'Report API publish output is missing.' }
if (!(Test-Path -LiteralPath (Join-Path $agentSource 'PEIS.PrintAgent.exe'))) { throw 'PrintAgent publish output is missing.' }

Remove-Item -LiteralPath $packageDirectory -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item -LiteralPath $zipPath -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Path $packageDirectory -Force | Out-Null

Copy-Item -LiteralPath $apiSource -Destination (Join-Path $packageDirectory 'report-api') -Recurse -Force
Copy-Item -LiteralPath $agentSource -Destination (Join-Path $packageDirectory 'print-agent') -Recurse -Force
Copy-Item -LiteralPath (Join-Path $repositoryRoot 'scripts') -Destination (Join-Path $packageDirectory 'scripts') -Recurse -Force
Copy-Item -LiteralPath (Join-Path $repositoryRoot 'docs') -Destination (Join-Path $packageDirectory 'docs') -Recurse -Force
Copy-Item -LiteralPath (Join-Path $repositoryRoot 'config-templates') -Destination (Join-Path $packageDirectory 'config-templates') -Recurse -Force

# Production configuration, local identity, runtime state, test evidence and compiled build intermediates must not ship.
$forbiddenNames = @('appsettings.Production.json', 'agent-id.txt', 'print-state.db')
Get-ChildItem -LiteralPath $packageDirectory -Recurse -Force | Where-Object {
    $_.Name -in $forbiddenNames -or $_.FullName -match '[\\/]\.runtime[\\/]' -or $_.FullName -match '[\\/](bin|obj)[\\/]'
} | Remove-Item -Recurse -Force

$forbiddenExtensions = @('.pdf', '.frx', '.db', '.sqlite', '.har', '.trace')
$forbiddenFiles = Get-ChildItem -LiteralPath $packageDirectory -Recurse -File | Where-Object { $forbiddenExtensions -contains $_.Extension.ToLowerInvariant() }
if ($forbiddenFiles) {
    throw ('RC1 package contains prohibited artifact types: ' + (($forbiddenFiles | Select-Object -ExpandProperty Name) -join ', '))
}

Compress-Archive -LiteralPath $packageDirectory -DestinationPath $zipPath -CompressionLevel Optimal
Write-Host "RC1 directory: $packageDirectory"
Write-Host "RC1 ZIP:       $zipPath"
