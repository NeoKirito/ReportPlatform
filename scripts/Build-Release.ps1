[CmdletBinding()]
param(
    [switch]$Clean,
    [switch]$SkipRestore
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$solution = Join-Path $repositoryRoot 'PEIS.ReportPlatform.sln'
$publishRoot = Join-Path $repositoryRoot 'publish'
$apiOutput = Join-Path $publishRoot 'report-api'
$agentOutput = Join-Path $publishRoot 'print-agent'

if (!(Test-Path -LiteralPath $solution)) {
    throw "Solution file was not found: $solution"
}

if ($Clean -and (Test-Path -LiteralPath $publishRoot)) {
    Remove-Item -LiteralPath $publishRoot -Recurse -Force
}
New-Item -ItemType Directory -Path $apiOutput, $agentOutput -Force | Out-Null

Push-Location $repositoryRoot
try {
    if (!$SkipRestore) {
        dotnet restore $solution -r win-x64
        if ($LASTEXITCODE -ne 0) { throw 'dotnet restore for win-x64 failed.' }
    }

    dotnet publish 'src/PEIS.Report.Api/PEIS.Report.Api.csproj' -c Release -r win-x64 --self-contained false --no-restore -o $apiOutput
    if ($LASTEXITCODE -ne 0) { throw 'Report API publish failed.' }

    dotnet publish 'src/PEIS.PrintAgent/PEIS.PrintAgent.csproj' -c Release -r win-x64 --self-contained true --no-restore -o $agentOutput
    if ($LASTEXITCODE -ne 0) { throw 'PrintAgent publish failed.' }

    $manifest = [ordered]@{
        generatedAtUtc = [DateTime]::UtcNow.ToString('O')
        reportApi = [ordered]@{ path = 'report-api'; deployment = 'framework-dependent'; runtime = 'win-x64' }
        printAgent = [ordered]@{ path = 'print-agent'; deployment = 'self-contained'; runtime = 'win-x64' }
    } | ConvertTo-Json -Depth 8
    $utf8NoBom = [System.Text.UTF8Encoding]::new($false)
    [IO.File]::WriteAllText((Join-Path $publishRoot 'release-manifest.json'), $manifest, $utf8NoBom)
}
finally {
    Pop-Location
}

Write-Host "Release assets generated:"
Write-Host "  API:   $apiOutput"
Write-Host "  Agent: $agentOutput"
Write-Host "Before field use, set production secrets through environment configuration and complete docs/FIELD_ACCEPTANCE_CHECKLIST.md."
