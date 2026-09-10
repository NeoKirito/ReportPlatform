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
$packageName = "PEIS.PrintAgent-Portable-win-x64-$Version"
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
$project = Join-Path $repositoryRoot 'src\PEIS.PrintAgent\PEIS.PrintAgent.csproj'

# Ensure SumatraPDF engine is present before publish
$sumatraTarget = Join-Path $repositoryRoot 'src\PEIS.PrintAgent\tools\SumatraPDF.exe'
if (!(Test-Path -LiteralPath $sumatraTarget)) {
    Write-Host "Downloading SumatraPDF portable engine..." -ForegroundColor Cyan
    $toolsDir = Split-Path $sumatraTarget -Parent
    New-Item -ItemType Directory -Path $toolsDir -Force | Out-Null
    $tempZip = Join-Path ([IO.Path]::GetTempPath()) "SumatraPDF-$([Guid]::NewGuid()).zip"
    $tempExtract = Join-Path ([IO.Path]::GetTempPath()) "SumatraPDF-$([Guid]::NewGuid())"
    try {
        Invoke-WebRequest -Uri "https://www.sumatrapdfreader.org/dl/rel/3.5.2/SumatraPDF-3.5.2-64.zip" -OutFile $tempZip
        Expand-Archive -Path $tempZip -DestinationPath $tempExtract -Force
        $extractedExe = (Get-ChildItem -Path $tempExtract -Filter 'SumatraPDF*.exe' | Select-Object -First 1).FullName
        Copy-Item -LiteralPath $extractedExe -Destination $sumatraTarget -Force
    }
    finally {
        Remove-Item -LiteralPath $tempZip, $tempExtract -Recurse -Force -ErrorAction SilentlyContinue
    }
}

# Some local shells lack this standard variable, which NuGet uses for fallback paths.
$oldProgramFilesX86 = [Environment]::GetEnvironmentVariable('ProgramFiles(x86)', 'Process')
try {
    if (!$oldProgramFilesX86) { [Environment]::SetEnvironmentVariable('ProgramFiles(x86)', [Environment]::GetFolderPath('ProgramFilesX86'), 'Process') }
    & dotnet publish $project -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -p:PublishTrimmed=false -p:DebugType=None -o $appRoot
    if ($LASTEXITCODE -ne 0) { throw 'PrintAgent publish failed.' }
}
finally { [Environment]::SetEnvironmentVariable('ProgramFiles(x86)', $oldProgramFilesX86, 'Process') }

$templateRoot = Join-Path $repositoryRoot 'deploy\field-release\print-agent'
Copy-Item -LiteralPath (Join-Path $templateRoot 'scripts\AgentService.ps1') -Destination $scriptsRoot
Get-ChildItem -LiteralPath $templateRoot -File | Copy-Item -Destination $packageRoot
Copy-Item -LiteralPath (Join-Path $repositoryRoot 'docs\THIRD_PARTY_NOTICES.md') -Destination $packageRoot

# Environment-specific settings must not override the generated safe defaults.
Get-ChildItem -LiteralPath $appRoot -File -Filter 'appsettings.*.json' | Remove-Item -Force

foreach ($required in @('PEIS.PrintAgent.exe', 'coreclr.dll', 'hostfxr.dll', 'System.Private.CoreLib.dll')) {
    if (!(Test-Path -LiteralPath (Join-Path $appRoot $required))) { throw "Missing required runtime file: $required" }
}
if (!(Test-Path -LiteralPath (Join-Path $appRoot 'Resources\app.ico'))) {
    throw "Missing required resource file: Resources\app.ico"
}
if (!(Test-Path -LiteralPath (Join-Path $appRoot 'tools\SumatraPDF.exe'))) {
    throw "Missing required silent print engine: tools\SumatraPDF.exe"
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
