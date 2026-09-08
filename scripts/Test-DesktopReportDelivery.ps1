[CmdletBinding()]
param(
    [string]$ServerUrl = "",
    [string]$ReportServerUrl = "",
    [string]$StationId = "",
    [string]$Djid = "jktjbbd",
    [ValidateSet("Preview", "Print", "PreviewAndPrint")]
    [string]$Action = "Preview",
    [string]$PrinterRole = "",
    [string]$PdfPath = "",
    [string]$UploadToken = "",
    [ValidateRange(10, 600)]
    [int]$TimeoutSeconds = 120
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

Add-Type -AssemblyName System.Net.Http

function Read-ResponseText {
    param([System.Net.Http.HttpResponseMessage]$Response)

    $text = $Response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
    if (-not $Response.IsSuccessStatusCode) {
        throw "HTTP $([int]$Response.StatusCode) $($Response.ReasonPhrase): $text"
    }
    return $text
}

function Assert-PdfFile {
    param([string]$Path)

    $file = Get-Item -LiteralPath $Path -ErrorAction Stop
    if ($file.Length -lt 5) {
        throw "PDF 文件为空或不完整：$Path"
    }

    $stream = [System.IO.File]::OpenRead($file.FullName)
    try {
        $header = New-Object byte[] 5
        if ($stream.Read($header, 0, 5) -ne 5 -or
            [System.Text.Encoding]::ASCII.GetString($header) -ne "%PDF-") {
            throw "返回内容不是有效 PDF（缺少 %PDF- 文件头）：$Path"
        }
    }
    finally {
        $stream.Dispose()
    }
    return $file
}

$agentIniPath = Join-Path $PSScriptRoot "agent.ini"
if ([string]::IsNullOrWhiteSpace($ServerUrl) -and (Test-Path -LiteralPath $agentIniPath)) {
    foreach ($line in (Get-Content -LiteralPath $agentIniPath)) {
        if ($line -match '^\s*ServerUrl\s*=\s*(.+?)\s*$') {
            $ServerUrl = $Matches[1]
            break
        }
    }
}
$agentSettingsPath = Join-Path $PSScriptRoot "appsettings.json"
if ([string]::IsNullOrWhiteSpace($ServerUrl) -and (Test-Path -LiteralPath $agentSettingsPath)) {
    $agentSettings = Get-Content -LiteralPath $agentSettingsPath -Raw | ConvertFrom-Json
    $ServerUrl = [string]$agentSettings.Agent.ServerUrl
}
if ([string]::IsNullOrWhiteSpace($ServerUrl)) {
    $ServerUrl = "http://192.168.0.88:82"
}

$deliveryBaseUrl = $ServerUrl.TrimEnd("/")
$reportBaseUrl = if ([string]::IsNullOrWhiteSpace($ReportServerUrl)) {
    $deliveryBaseUrl
}
else {
    $ReportServerUrl.TrimEnd("/")
}
$http = [System.Net.Http.HttpClient]::new()
$reportHttp = [System.Net.Http.HttpClient]::new()
$timeout = [TimeSpan]::FromSeconds([Math]::Max(30, $TimeoutSeconds))
$http.Timeout = $timeout
$reportHttp.Timeout = $timeout
$response = $null
if (-not [string]::IsNullOrWhiteSpace($UploadToken)) {
    $http.DefaultRequestHeaders.Add("X-Report-Delivery-Token", $UploadToken)
}

try {
    $agentExe = Join-Path $PSScriptRoot "PEIS.PrintAgent.exe"
    if (Test-Path -LiteralPath $agentExe) {
        $agentFullPath = [System.IO.Path]::GetFullPath($agentExe)
        $running = @(Get-Process -Name "PEIS.PrintAgent" -ErrorAction SilentlyContinue | Where-Object {
            try { $_.Path -eq $agentFullPath } catch { $false }
        }).Count -gt 0
        if (-not $running) {
            Write-Host "[0/4] 正在自动启动桌面 Agent..."
            Start-Process -FilePath $agentFullPath -WorkingDirectory $PSScriptRoot -WindowStyle Hidden
        }
    }

    $stationReady = $false
    $stationResponse = $null
    for ($attempt = 0; $attempt -lt 20; $attempt++) {
        try {
            $stationResponse = $http.GetAsync("$deliveryBaseUrl/api/report-deliveries/stations").GetAwaiter().GetResult()
            if ($stationResponse.IsSuccessStatusCode) {
                $stationInfo = ($stationResponse.Content.ReadAsStringAsync().GetAwaiter().GetResult() | ConvertFrom-Json)
                $available = @($stationInfo.stations)
                $stationReady = if ([string]::IsNullOrWhiteSpace($StationId)) {
                    -not [string]::IsNullOrWhiteSpace([string]$stationInfo.autoMatchedStationId) -or $available.Count -eq 1
                }
                else {
                    @($available | Where-Object { $_.stationId -eq $StationId }).Count -eq 1
                }
                if ($stationReady) { break }
            }
        }
        catch {
        }
        finally {
            if ($null -ne $stationResponse) { $stationResponse.Dispose(); $stationResponse = $null }
        }
        Start-Sleep -Milliseconds 500
    }
    if (-not $stationReady) {
        throw "桌面 Agent 未在线或无法自动匹配。请检查 $deliveryBaseUrl、启动程序和网络。"
    }

    if ([string]::IsNullOrWhiteSpace($PdfPath)) {
        $PdfPath = [System.IO.Path]::GetFullPath(
            (Join-Path $PSScriptRoot "desktop-delivery-test.pdf"))
        $legacyRequest = [ordered]@{
            pageNo = 1
            pageSize = 100
            djh = [ordered]@{
                grtjgcjjgid = "C48E053041174B41B32C9CE9EB5ECE7F"
            }
            yhmc = "亚创科技有限公司"
            bbid = "jktjbbd"
            fileName = "123456"
            querytype = "djwh"
        }
        $json = $legacyRequest | ConvertTo-Json -Depth 5 -Compress
        $body = [System.Net.Http.StringContent]::new(
            $json,
            [System.Text.Encoding]::UTF8,
            "application/json")
        Write-Host "[1/4] 调用旧接口生成测试 PDF：$reportBaseUrl"
        $response = $reportHttp.PostAsync(
            "$reportBaseUrl/BaseInfo/Report/GetReportByJson",
            $body).GetAwaiter().GetResult()
        if (-not $response.IsSuccessStatusCode) {
            [void](Read-ResponseText -Response $response)
        }
        $bytes = $response.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult()
        [System.IO.File]::WriteAllBytes($PdfPath, $bytes)
        $response.Dispose()
        $body.Dispose()
    }
    else {
        $PdfPath = [System.IO.Path]::GetFullPath($PdfPath)
        Write-Host "[1/4] 使用指定 PDF，跳过旧接口生成。"
    }

    $pdf = Assert-PdfFile -Path $PdfPath
    Write-Host "[2/4] PDF 校验通过：$($pdf.Length) 字节，$($pdf.FullName)"

    if ($Action -ne "Preview" -and [string]::IsNullOrWhiteSpace($PrinterRole)) {
        throw "Print/PreviewAndPrint 必须指定 PrinterRole。"
    }

    $multipart = [System.Net.Http.MultipartFormDataContent]::new()
    $fileStream = [System.IO.File]::OpenRead($pdf.FullName)
    $fileContent = [System.Net.Http.StreamContent]::new($fileStream)
    $fileContent.Headers.ContentType = [System.Net.Http.Headers.MediaTypeHeaderValue]::new("application/pdf")
    if (-not [string]::IsNullOrWhiteSpace($StationId)) {
        $multipart.Add([System.Net.Http.StringContent]::new($StationId), "stationId")
    }
    $multipart.Add([System.Net.Http.StringContent]::new($Action), "action")
    if (-not [string]::IsNullOrWhiteSpace($Djid)) {
        $multipart.Add([System.Net.Http.StringContent]::new($Djid), "djid")
    }
    $multipart.Add([System.Net.Http.StringContent]::new("1"), "copies")
    $multipart.Add([System.Net.Http.StringContent]::new("false"), "duplex")
    if ($Action -ne "Preview") {
        $multipart.Add([System.Net.Http.StringContent]::new($PrinterRole), "printerRole")
    }
    $multipart.Add($fileContent, "file", $pdf.Name)

    try {
        $targetText = if ([string]::IsNullOrWhiteSpace($StationId)) { "自动匹配本机 Agent" } else { "工作站 $StationId" }
        Write-Host "[3/4] 投递到 $deliveryBaseUrl，$targetText（$Action）..."
        $response = $null
        $response = $http.PostAsync("$deliveryBaseUrl/api/report-deliveries", $multipart).GetAwaiter().GetResult()
        $createdText = Read-ResponseText -Response $response
        $created = $createdText | ConvertFrom-Json
        $jobId = [string]$created.jobId
        if ([string]::IsNullOrWhiteSpace($jobId)) {
            throw "投递响应中没有 jobId：$createdText"
        }
        Write-Host "任务号：$jobId"
    }
    finally {
        if ($null -ne $response) { $response.Dispose() }
        $multipart.Dispose()
    }

    Write-Host "[4/4] 等待工作站处理状态..."
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    $lastStatus = ""
    while ([DateTime]::UtcNow -lt $deadline) {
        Start-Sleep -Milliseconds 500
        $statusResponse = $http.GetAsync("$deliveryBaseUrl/api/report-deliveries/$jobId").GetAwaiter().GetResult()
        try {
            $statusText = Read-ResponseText -Response $statusResponse
            $state = $statusText | ConvertFrom-Json
        }
        finally {
            $statusResponse.Dispose()
        }

        $status = [string]$state.status
        if ($status -ne $lastStatus) {
            Write-Host "状态：$status $($state.message)"
            $lastStatus = $status
        }
        if ($status -eq "Failed") {
            throw "工作站处理失败：$($state.message)"
        }
        if ($Action -eq "Preview" -and $status -eq "Opened") {
            Write-Host "测试通过：工作站已打开 PDF 预览窗口。" -ForegroundColor Green
            return
        }
        if ($Action -eq "PreviewAndPrint" -and $status -eq "Opened") {
            Write-Host "预览已打开。请在窗口中点击“打印”；随后状态会变为 Completed。" -ForegroundColor Green
            return
        }
        if ($status -eq "Completed") {
            Write-Host "测试通过：工作站任务已完成。" -ForegroundColor Green
            return
        }
    }
    throw "等待工作站处理超时（$TimeoutSeconds 秒），最后状态：$lastStatus"
}
finally {
    $reportHttp.Dispose()
    $http.Dispose()
}
