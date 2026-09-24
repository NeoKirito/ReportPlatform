[CmdletBinding()]
param(
    [string]$Configuration = 'Release',
    [string]$OutputExe = ''
)

Set-StrictMode -Off
$ErrorActionPreference = 'Stop'
$projectRoot = if (![string]::IsNullOrEmpty($PSScriptRoot)) { (Resolve-Path (Join-Path $PSScriptRoot '..')).Path } else { (Get-Location).Path }
Set-Location $projectRoot

$oldProgramFilesX86 = [Environment]::GetEnvironmentVariable('ProgramFiles(x86)', 'Process')
$tempAgentDir = $null
$tempSetupPublishDir = $null
try {
    if (!$oldProgramFilesX86) {
        [Environment]::SetEnvironmentVariable('ProgramFiles(x86)', [Environment]::GetFolderPath('ProgramFilesX86'), 'Process')
    }

    Write-Host "============================================================" -ForegroundColor Cyan
    Write-Host " Building PEIS.PrintAgent.Setup Single-File Installer       " -ForegroundColor Cyan
    Write-Host "============================================================" -ForegroundColor Cyan

    # 1. 确保 SumatraPDF 存在
    $sumatraTarget = Join-Path $projectRoot 'src\PEIS.PrintAgent\tools\SumatraPDF.exe'
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

    # 2. 发布 PrintAgent 到临时目录
    $guidSuffix = [Guid]::NewGuid().ToString('N')
    $tempAgentDir = [IO.Path]::Combine([IO.Path]::GetTempPath(), "PrintAgent-Payload-$guidSuffix")
    New-Item -ItemType Directory -Path $tempAgentDir -Force | Out-Null

    Write-Host "Publishing PEIS.PrintAgent (self-contained win-x64)..." -ForegroundColor Cyan
    & dotnet publish 'src\PEIS.PrintAgent\PEIS.PrintAgent.csproj' `
        -c $Configuration `
        -r win-x64 `
        --self-contained true `
        -p:PublishSingleFile=false `
        -p:DebugType=None `
        -o $tempAgentDir

    if ($LASTEXITCODE -ne 0) { throw 'PrintAgent publish failed.' }

    Get-ChildItem -LiteralPath $tempAgentDir -File -Filter 'appsettings.*.json' | Remove-Item -Force -ErrorAction SilentlyContinue
    Get-ChildItem -LiteralPath $tempAgentDir -File -Filter '*.pdb' | Remove-Item -Force -ErrorAction SilentlyContinue
    Get-ChildItem -LiteralPath $tempAgentDir -Directory | Where-Object { $_.Name -in @('cs','de','es','fr','it','ja','ko','pl','pt-BR','ru','tr') } | Remove-Item -Recurse -Force -ErrorAction SilentlyContinue

    # 3. 压缩为 PrintAgentPayload.zip 并放入 Setup 项目 Resources 目录
    $setupResourcesDir = Join-Path $projectRoot 'src\PEIS.PrintAgent.Setup\Resources'
    New-Item -ItemType Directory -Path $setupResourcesDir -Force | Out-Null
    $payloadZipPath = Join-Path $setupResourcesDir 'PrintAgentPayload.zip'

    if (Test-Path -LiteralPath $payloadZipPath) {
        Remove-Item -LiteralPath $payloadZipPath -Force
    }

    Write-Host "Creating embedded PrintAgentPayload.zip..." -ForegroundColor Cyan
    Compress-Archive -Path "$tempAgentDir\*" -DestinationPath $payloadZipPath -CompressionLevel Optimal

    # 4. 发布 Setup 为单文件 exe
    $setupGuid = [Guid]::NewGuid().ToString('N')
    $tempSetupPublishDir = [IO.Path]::Combine([IO.Path]::GetTempPath(), "PrintAgent-Setup-Pub-$setupGuid")
    New-Item -ItemType Directory -Path $tempSetupPublishDir -Force | Out-Null

    Write-Host "Publishing PEIS.PrintAgent.Setup single-file executable..." -ForegroundColor Cyan
    & dotnet publish 'src\PEIS.PrintAgent.Setup\PEIS.PrintAgent.Setup.csproj' `
        -c $Configuration `
        -r win-x64 `
        --self-contained true `
        -p:PublishSingleFile=true `
        -p:EnableCompressionInSingleFile=true `
        -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:DebugType=None `
        -o $tempSetupPublishDir

    if ($LASTEXITCODE -ne 0) { throw 'Setup publish failed.' }

    $builtExe = Join-Path $tempSetupPublishDir 'PEIS-PrintAgent-Setup.exe'
    if (!(Test-Path -LiteralPath $builtExe)) {
        throw "Failed to locate generated setup executable: $builtExe"
    }

    # 5. 复制到 API wwwroot/downloads 目录
    $apiDownloadsDir = Join-Path $projectRoot 'src\PEIS.Report.Api\wwwroot\downloads'
    New-Item -ItemType Directory -Path $apiDownloadsDir -Force | Out-Null
    $destExe = Join-Path $apiDownloadsDir 'PEIS-PrintAgent-Setup.exe'
    Copy-Item -LiteralPath $builtExe -Destination $destExe -Force

    if (![string]::IsNullOrWhiteSpace($OutputExe)) {
        $parent = Split-Path $OutputExe -Parent
        if (![string]::IsNullOrWhiteSpace($parent)) {
            New-Item -ItemType Directory -Path $parent -Force | Out-Null
        }
        Copy-Item -LiteralPath $builtExe -Destination $OutputExe -Force
    }

    $fileSizeMb = [math]::Round(((Get-Item $destExe).Length / 1MB), 2)
    Write-Host "============================================================" -ForegroundColor Green
    Write-Host "PEIS.PrintAgent.Setup built successfully!" -ForegroundColor Green
    Write-Host "Target: $destExe ($fileSizeMb MB)" -ForegroundColor Green
    Write-Host "============================================================" -ForegroundColor Green

    return $destExe
}
finally {
    [Environment]::SetEnvironmentVariable('ProgramFiles(x86)', $oldProgramFilesX86, 'Process')
    if (![string]::IsNullOrWhiteSpace($tempAgentDir) -and (Test-Path -LiteralPath $tempAgentDir)) {
        Remove-Item -LiteralPath $tempAgentDir -Recurse -Force -ErrorAction SilentlyContinue
    }
    if (![string]::IsNullOrWhiteSpace($tempSetupPublishDir) -and (Test-Path -LiteralPath $tempSetupPublishDir)) {
        Remove-Item -LiteralPath $tempSetupPublishDir -Recurse -Force -ErrorAction SilentlyContinue
    }
}
