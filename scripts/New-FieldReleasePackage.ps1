[CmdletBinding()]
param(
    [string]$Version = (Get-Date -Format 'yyyyMMdd'),
    [string]$OutputRoot = ''
)

$ErrorActionPreference = 'Stop'
$projectRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
if ([string]::IsNullOrWhiteSpace($OutputRoot)) { $OutputRoot = Join-Path $projectRoot 'artifacts' }
Set-Location $projectRoot
Set-Item -Path 'Env:ProgramFiles(x86)' -Value 'C:\Program Files (x86)'

$packageName = "PEIS.ReportPlatform-FieldRelease-$Version"
$packageRoot = Join-Path $OutputRoot $packageName
$apiOutput = Join-Path $packageRoot '01-ReportApi'
$agentOutput = Join-Path $packageRoot '02-PrintAgent'
$docsOutput = Join-Path $packageRoot '03-Docs'

if (Test-Path -LiteralPath $packageRoot) { Remove-Item -LiteralPath $packageRoot -Force -Recurse }
New-Item -ItemType Directory -Path $apiOutput, $agentOutput, $docsOutput -Force | Out-Null

$publishProperties = @(
    '-c', 'Release',
    '-r', 'win-x64',
    '--self-contained', 'true',
    '-p:PublishSingleFile=false',
    '-p:DebugType=None'
)

dotnet publish 'src\PEIS.Report.Api\PEIS.Report.Api.csproj' @publishProperties '-o' $apiOutput
if ($LASTEXITCODE -ne 0) { throw 'API publish failed.' }
dotnet publish 'src\PEIS.PrintAgent\PEIS.PrintAgent.csproj' @publishProperties '-o' $agentOutput
if ($LASTEXITCODE -ne 0) { throw 'PrintAgent publish failed.' }

Copy-Item -Path 'deploy\field-release\api\*' -Destination $apiOutput -Force
Copy-Item -Path 'deploy\field-release\print-agent\*' -Destination $agentOutput -Force
Copy-Item -Path 'deploy\field-release\docs\*' -Destination $docsOutput -Force
Copy-Item -LiteralPath 'docs\THIRD_PARTY_NOTICES.md' -Destination (Join-Path $docsOutput 'ThirdPartyNotices.md') -Force
Copy-Item -LiteralPath 'docs\WATERMARK_CONTRACT.md' -Destination (Join-Path $docsOutput 'WatermarkContract.md') -Force

$zipPath = Join-Path $OutputRoot "$packageName.zip"
if (Test-Path -LiteralPath $zipPath) { Remove-Item -LiteralPath $zipPath -Force }
Compress-Archive -LiteralPath $packageRoot -DestinationPath $zipPath -CompressionLevel Optimal

Write-Output "Field release folder: $packageRoot"
Write-Output "Field release zip: $zipPath"
