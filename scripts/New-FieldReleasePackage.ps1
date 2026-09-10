[CmdletBinding()]
param(
    [string]$Version = (Get-Date -Format 'yyyyMMdd'),
    [string]$OutputRoot = ''
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$projectRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
if ([string]::IsNullOrWhiteSpace($OutputRoot)) { $OutputRoot = Join-Path $projectRoot 'artifacts' }
$OutputRoot = [IO.Path]::GetFullPath($OutputRoot)
Set-Location $projectRoot

$oldProgramFilesX86 = [Environment]::GetEnvironmentVariable('ProgramFiles(x86)', 'Process')
try {
    if (!$oldProgramFilesX86) { [Environment]::SetEnvironmentVariable('ProgramFiles(x86)', [Environment]::GetFolderPath('ProgramFilesX86'), 'Process') }

    $packageName = "PEIS.ReportPlatform-FieldRelease-$Version"
    $packageRoot = Join-Path $OutputRoot $packageName
    $zipPath = Join-Path $OutputRoot "$packageName.zip"
    $shaPath = Join-Path $OutputRoot "$packageName.zip.sha256"

    if (Test-Path -LiteralPath $packageRoot) { Remove-Item -LiteralPath $packageRoot -Force -Recurse }
    if (Test-Path -LiteralPath $zipPath) { Remove-Item -LiteralPath $zipPath -Force }
    if (Test-Path -LiteralPath $shaPath) { Remove-Item -LiteralPath $shaPath -Force }

    $apiOutput = Join-Path $packageRoot '01-ReportApi'
    $apiApp = Join-Path $apiOutput 'app'
    $apiScripts = Join-Path $apiOutput 'scripts'

    $agentOutput = Join-Path $packageRoot '02-PrintAgent'
    $agentApp = Join-Path $agentOutput 'app'
    $agentScripts = Join-Path $agentOutput 'scripts'

    $docsOutput = Join-Path $packageRoot '03-Docs'

    New-Item -ItemType Directory -Path $apiApp, $apiScripts, $agentApp, $agentScripts, $docsOutput -Force | Out-Null

    $publishProperties = @(
        '-c', 'Release',
        '-r', 'win-x64',
        '--self-contained', 'true',
        '-p:PublishSingleFile=false',
        '-p:DebugType=None'
    )

    # 1. 编译并发布 ReportApi 到 01-ReportApi/app
    Write-Host "Publishing ReportApi..." -ForegroundColor Cyan
    & dotnet publish 'src\PEIS.Report.Api\PEIS.Report.Api.csproj' @publishProperties '-o' $apiApp
    if ($LASTEXITCODE -ne 0) { throw 'API publish failed.' }

    # 2. 编译并发布 PrintAgent 到 02-PrintAgent/app
    Write-Host "Publishing PrintAgent..." -ForegroundColor Cyan
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

    & dotnet publish 'src\PEIS.PrintAgent\PEIS.PrintAgent.csproj' @publishProperties '-o' $agentApp
    if ($LASTEXITCODE -ne 0) { throw 'PrintAgent publish failed.' }

    # 3. 组织 01-ReportApi 外部脚本与配置
    Copy-Item -LiteralPath 'deploy\field-release\api\scripts\Service.ps1' -Destination $apiScripts -Force
    Get-ChildItem -LiteralPath 'deploy\field-release\api' -File | Copy-Item -Destination $apiOutput -Force
    Get-ChildItem -LiteralPath $apiApp -File -Filter 'appsettings.*.json' | Remove-Item -Force

    # 4. 组织 02-PrintAgent 外部脚本与配置
    Copy-Item -LiteralPath 'deploy\field-release\print-agent\scripts\AgentService.ps1' -Destination $agentScripts -Force
    Get-ChildItem -LiteralPath 'deploy\field-release\print-agent' -File | Copy-Item -Destination $agentOutput -Force
    Get-ChildItem -LiteralPath $agentApp -File -Filter 'appsettings.*.json' | Remove-Item -Force

    # 5. 复制文档
    Copy-Item -Path 'deploy\field-release\docs\*' -Destination $docsOutput -Force
    Copy-Item -LiteralPath 'docs\THIRD_PARTY_NOTICES.md' -Destination (Join-Path $docsOutput 'ThirdPartyNotices.md') -Force
    Copy-Item -LiteralPath 'docs\WATERMARK_CONTRACT.md' -Destination (Join-Path $docsOutput 'WatermarkContract.md') -Force
    Copy-Item -LiteralPath 'docs\THIRD_PARTY_NOTICES.md' -Destination $apiOutput -Force
    Copy-Item -LiteralPath 'docs\THIRD_PARTY_NOTICES.md' -Destination $agentOutput -Force

    # 6. 验证核心文件存在性
    foreach ($required in @('PEIS.Report.Api.exe', 'coreclr.dll', 'hostfxr.dll', 'FastReport.dll')) {
        if (!(Test-Path -LiteralPath (Join-Path $apiApp $required))) { throw "Missing required API file: $required" }
    }
    foreach ($required in @('PEIS.PrintAgent.exe', 'coreclr.dll', 'hostfxr.dll')) {
        if (!(Test-Path -LiteralPath (Join-Path $agentApp $required))) { throw "Missing required PrintAgent file: $required" }
    }
    if (!(Test-Path -LiteralPath (Join-Path $agentApp 'Resources\app.ico'))) {
        throw "Missing required resource: Resources\app.ico"
    }
    if (!(Test-Path -LiteralPath (Join-Path $agentApp 'tools\SumatraPDF.exe'))) {
        throw "Missing required silent print engine: tools\SumatraPDF.exe"
    }

    # 7. 规范化批处理与脚本换行符和编码
    $utf8Bom = New-Object Text.UTF8Encoding($true)
    foreach ($file in (Get-ChildItem -LiteralPath $packageRoot -Recurse -File | Where-Object { $_.Extension -in '.cmd', '.ps1', '.ini', '.txt' })) {
        $content = [IO.File]::ReadAllText($file.FullName).Replace("`r`n", "`n").Replace("`n", "`r`n")
        $encoding = if ($file.Extension -eq '.cmd') { [Text.Encoding]::ASCII } else { $utf8Bom }
        [IO.File]::WriteAllText($file.FullName, $content, $encoding)
    }

    # 8. 打包为 ZIP 并生成哈希
    Write-Host "Compressing archive..." -ForegroundColor Cyan
    Compress-Archive -LiteralPath $packageRoot -DestinationPath $zipPath -CompressionLevel Optimal
    $hash = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash
    "$hash  $packageName.zip" | Set-Content -LiteralPath $shaPath -Encoding ASCII

    Write-Host "============================================================" -ForegroundColor Green
    Write-Host "Field release folder: $packageRoot"
    Write-Host "Field release zip   : $zipPath"
    Write-Host "SHA256              : $hash"
    Write-Host "============================================================" -ForegroundColor Green
}
finally {
    [Environment]::SetEnvironmentVariable('ProgramFiles(x86)', $oldProgramFilesX86, 'Process')
}
