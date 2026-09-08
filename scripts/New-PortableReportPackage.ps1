[CmdletBinding()]
param(
    [ValidatePattern('^[A-Za-z0-9._-]+$')]
    [string]$Version = (Get-Date -Format 'yyyyMMdd-HHmmss'),
    [string]$OutputRoot = ''
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
if ([string]::IsNullOrWhiteSpace($OutputRoot)) { $OutputRoot = Join-Path $repositoryRoot 'artifacts' }
$OutputRoot = [IO.Path]::GetFullPath($OutputRoot)
$packageName = "PEIS.ReportApi-Portable-win-x64-$Version"
$packageRoot = Join-Path $OutputRoot $packageName
$zipPath = Join-Path $OutputRoot "$packageName.zip"
if ((Test-Path -LiteralPath $packageRoot) -or (Test-Path -LiteralPath $zipPath)) {
    Remove-Item -LiteralPath $packageRoot -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath $zipPath -Force -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath "$zipPath.sha256" -Force -ErrorAction SilentlyContinue
}

$appRoot = Join-Path $packageRoot 'app'
$scriptsRoot = Join-Path $packageRoot 'scripts'
New-Item -ItemType Directory -Path $appRoot, $scriptsRoot -Force | Out-Null
$project = Join-Path $repositoryRoot 'src\PEIS.Report.Api\PEIS.Report.Api.csproj'
# Some local shells lack this standard variable, which NuGet uses for fallback paths.
$oldProgramFilesX86 = [Environment]::GetEnvironmentVariable('ProgramFiles(x86)', 'Process')
try {
    if (!$oldProgramFilesX86) { [Environment]::SetEnvironmentVariable('ProgramFiles(x86)', [Environment]::GetFolderPath('ProgramFilesX86'), 'Process') }
    & dotnet publish $project -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -p:PublishTrimmed=false -p:DebugType=None -o $appRoot
    if ($LASTEXITCODE -ne 0) { throw 'Report API publish failed.' }
}
finally { [Environment]::SetEnvironmentVariable('ProgramFiles(x86)', $oldProgramFilesX86, 'Process') }

$templateRoot = Join-Path $repositoryRoot 'deploy\portable'
Copy-Item -LiteralPath (Join-Path $templateRoot 'Service.ps1') -Destination $scriptsRoot
Get-ChildItem -LiteralPath $templateRoot -File | Where-Object { $_.Name -ne 'Service.ps1' } | Copy-Item -Destination $packageRoot
Copy-Item -LiteralPath (Join-Path $repositoryRoot 'docs\THIRD_PARTY_NOTICES.md') -Destination $packageRoot

$connStr = 'Server=192.168.0.237;Database=TJXT0616;User ID=sa;Password=Sxyckj#123;TrustServerCertificate=True;'
$config = Get-Content -LiteralPath (Join-Path $repositoryRoot 'src\PEIS.Report.Api\appsettings.json') -Raw | ConvertFrom-Json
$config.Urls = 'http://0.0.0.0:82'
$config.ReportEngine.DefinitionSource = 'LegacySqlServer'
$config.ReportEngine.Renderer = 'FastReportOpenSource'
$config.ReportDatabase.ConnectionString = $connStr
if ($config.PSObject.Properties['WatermarkDatabase']) {
    $config.WatermarkDatabase.ConnectionString = $connStr
} else {
    $config | Add-Member -NotePropertyName 'WatermarkDatabase' -NotePropertyValue ([PSCustomObject]@{ ConnectionString = $connStr })
}
$config.PrintAgentSecurity.RegistrationToken = ''
$config | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath (Join-Path $appRoot 'appsettings.json') -Encoding UTF8

$iniPath = Join-Path $packageRoot 'config.ini'
if (Test-Path -LiteralPath $iniPath) {
    $iniLines = Get-Content -LiteralPath $iniPath
    $newIniLines = @()
    foreach ($line in $iniLines) {
        if ($line.Trim().StartsWith('ConnectionString=')) {
            $newIniLines += "ConnectionString=$connStr"
        } else {
            $newIniLines += $line
        }
    }
    $newIniLines | Set-Content -LiteralPath $iniPath -Encoding UTF8
}
# Environment-specific settings must not override the generated safe defaults.
Get-ChildItem -LiteralPath $appRoot -File -Filter 'appsettings.*.json' | Remove-Item -Force

foreach ($required in @('PEIS.Report.Api.exe', 'coreclr.dll', 'hostfxr.dll', 'System.Private.CoreLib.dll', 'FastReport.dll', 'FastReport.OpenSource.Export.PdfSimple.dll')) {
    if (!(Test-Path -LiteralPath (Join-Path $appRoot $required))) { throw "Missing required runtime file: $required" }
}

# Windows PowerShell and cmd.exe expect these encodings/newlines on older hosts.
$utf8Bom = New-Object Text.UTF8Encoding($true)
foreach ($file in (Get-ChildItem -LiteralPath $packageRoot -Recurse -File | Where-Object { $_.Extension -in '.cmd', '.ps1', '.ini', '.txt' })) {
    $content = [IO.File]::ReadAllText($file.FullName).Replace("`r`n", "`n").Replace("`n", "`r`n")
    $encoding = if ($file.Extension -eq '.cmd') { [Text.Encoding]::ASCII } else { $utf8Bom }
    [IO.File]::WriteAllText($file.FullName, $content, $encoding)
}

Compress-Archive -LiteralPath $packageRoot -DestinationPath $zipPath -CompressionLevel Optimal
$hash = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash
"$hash  $packageName.zip" | Set-Content -LiteralPath "$zipPath.sha256" -Encoding ASCII
Write-Output "Package folder: $packageRoot"
Write-Output "Package ZIP: $zipPath"
Write-Output "SHA256: $hash"
