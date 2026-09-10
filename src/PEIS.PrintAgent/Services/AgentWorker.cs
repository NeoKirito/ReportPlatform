using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PEIS.PrintAgent.Printing;
using PEIS.PrintAgent.Previewing;
using PEIS.Report.Contracts;

namespace PEIS.PrintAgent.Services;

/// <summary>
/// 打印Agent工作服务 - Windows驻留后台服务。
/// 
/// 职责：
/// 1. 连接到API服务器的SignalR Hub
/// 2. 注册到AgentRegistry（上报打印机列表）
/// 3. 接收打印任务（PrintBatch）并执行
/// 4. 接收桌面交付任务（ReportDelivery）并执行
/// 5. 发送心跳保活
/// 6. 清理过期的PDF文件
/// 
/// 通信流程：
/// 启动 → 连接SignalR → 注册Agent → 接收任务 → 执行打印/预览 → 上报状态
/// 
/// 断线重连：
/// 连接断开后自动重试（3秒间隔），重连后重新注册。
/// </summary>
public sealed class AgentWorker(
    IOptions<AgentOptions> options,
    AgentIdentityStore identityStore,
    PrinterCatalog printers,
    PrintArtifactDownloader artifacts,
    PrinterQueueManager queues,
    DeliveryPrinterResolver deliveryPrinters,
    IPdfPreviewer previewer,
    ILogger<AgentWorker> logger) : BackgroundService
{
    /// <summary>
    /// 主循环：不断尝试连接SignalR Hub。
    /// 连接断开时记录日志并重试。
    /// </summary>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var cfg = options.Value;
        // 获取或创建Agent唯一ID
        cfg.AgentId = identityStore.GetOrCreate(cfg.AgentId);
        // 如果StationId未配置或为AUTO，使用计算机名
        if (string.IsNullOrWhiteSpace(cfg.StationId) || string.Equals(cfg.StationId, "AUTO", StringComparison.OrdinalIgnoreCase))
            cfg.StationId = Environment.MachineName;
        // 创建工作目录
        Directory.CreateDirectory(cfg.WorkDirectory);
        // 清理2小时前的旧PDF文件
        CleanupOldArtifacts(cfg.WorkDirectory);
        logger.LogInformation("Agent initialized. StationId: {StationId}, ServerUrl: {ServerUrl}", cfg.StationId, cfg.ServerUrl);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunConnectionAsync(cfg, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // 正常关闭
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Print agent connection stopped; reconnecting.");
                await Task.Delay(TimeSpan.FromSeconds(3), stoppingToken);
            }
        }
    }

    /// <summary>
    /// SignalR连接管理：建立连接、注册事件处理器、发送心跳。
    /// 
    /// 事件处理器：
    /// - PrintBatch：接收打印任务（可能包含多个文档）
    /// - ReportDelivery：接收桌面交付任务（预览+打印）
    /// - Reconnected：重连后重新注册
    /// </summary>
    private async Task RunConnectionAsync(AgentOptions cfg, CancellationToken token)
    {
        var hubUrl = $"{cfg.ServerUrl.TrimEnd('/')}/hubs/print-agent";
        logger.LogInformation("Connecting to SignalR hub at {Url}...", hubUrl);

        // 构建SignalR连接
        var connection = new HubConnectionBuilder()
            .WithUrl(hubUrl, httpOptions =>
            {
                // 禁用代理（内网环境不需要）
                httpOptions.HttpMessageHandlerFactory = handler =>
                {
                    if (handler is HttpClientHandler clientHandler)
                    {
                        clientHandler.UseProxy = false;
                        clientHandler.Proxy = null;
                    }
                    else if (handler is SocketsHttpHandler socketsHandler)
                    {
                        socketsHandler.UseProxy = false;
                        socketsHandler.Proxy = null;
                    }
                    return handler;
                };
                httpOptions.WebSocketConfiguration = ws =>
                {
                    ws.Proxy = new System.Net.WebProxy();
                };
            })
            .WithAutomaticReconnect()  // 自动重连
            .Build();

        // 注册事件处理器
        connection.On<PrintBatchDispatch>("PrintBatch", batch => HandleBatchAsync(connection, cfg, batch, token));
        connection.On<ReportDeliveryDispatch>("ReportDelivery", delivery => HandleDeliveryAsync(connection, cfg, delivery, token));
        connection.Reconnected += _ => RegisterAsync(connection, cfg, CancellationToken.None);

        // 启动连接并注册Agent
        await connection.StartAsync(token);
        await RegisterAsync(connection, cfg, token);
        logger.LogInformation(
            "PrintAgent {AgentId} station {StationId} connected to {Server}",
            cfg.AgentId,
            cfg.StationId,
            cfg.ServerUrl);

        // 心跳循环：定期发送心跳，每30次心跳清理一次旧文件
        var heartbeatCount = 0;
        while (!token.IsCancellationRequested && connection.State != HubConnectionState.Disconnected)
        {
            if (connection.State == HubConnectionState.Connected)
                await connection.InvokeAsync("Heartbeat", cfg.AgentId, token);

            if (++heartbeatCount % 30 == 0) CleanupOldArtifacts(cfg.WorkDirectory);
            await Task.Delay(TimeSpan.FromSeconds(cfg.HeartbeatSeconds), token);
        }

        await connection.DisposeAsync();
    }

    /// <summary>
    /// 注册Agent到服务器：
    /// 上报AgentId、StationId、计算机名、已安装打印机列表、打印机绑定、版本号。
    /// </summary>
    private Task RegisterAsync(HubConnection connection, AgentOptions cfg, CancellationToken token)
        => connection.InvokeAsync("Register", new AgentRegistration(
            cfg.AgentId,
            cfg.StationId,
            Environment.MachineName,
            printers.GetInstalledPrinters(),
            cfg.PrinterBindings,
            typeof(AgentWorker).Assembly.GetName().Version?.ToString() ?? "0.1.0",
            cfg.RegistrationToken), token);

    /// <summary>
    /// 处理打印任务（PrintBatch）：
    /// 一个B/S点击可能产生多个不同文档（如A4指导单+条码标签）。
    /// 
    /// 流程：
    /// 1. 并行下载所有文档PDF（受MaxConcurrentDownloads限制）
    /// 2. 每个文档单独入队到对应物理打印机的队列
    /// 3. 逐个打印（每个打印机队列串行执行）
    /// 4. 实时上报状态（Downloading → Printing → Completed/Failed）
    /// </summary>
    private async Task HandleBatchAsync(HubConnection connection, AgentOptions cfg, PrintBatchDispatch batch, CancellationToken token)
    {
        try
        {
            // 并行下载所有文档PDF
            var localPaths = await artifacts.DownloadAsync(
                cfg.ServerUrl,
                cfg.WorkDirectory,
                cfg.MaxConcurrentDownloads,
                batch.Documents,
                doc => SendStatusAsync(
                    connection,
                    cfg.AgentId,
                    batch.JobId,
                    doc,
                    PrintTargetStatus.Downloading,
                    null,
                    token),
                token);

            // 每个文档入队到对应打印机队列
            foreach (var doc in batch.Documents)
            {
                var path = localPaths[doc.ArtifactId];
                await queues.EnqueueAsync(new PrintWorkItem(
                    batch.JobId,
                    doc,
                    path,
                    (status, message) => SendStatusAsync(
                        connection,
                        cfg.AgentId,
                        batch.JobId,
                        doc,
                        status,
                        message,
                        CancellationToken.None)), token);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to receive print batch {JobId}", batch.JobId);
            // 上报所有文档失败状态
            foreach (var doc in batch.Documents)
            {
                await SendStatusAsync(
                    connection,
                    cfg.AgentId,
                    batch.JobId,
                    doc,
                    PrintTargetStatus.Failed,
                    ex.Message,
                    CancellationToken.None);
            }
        }
    }

    /// <summary>
    /// 发送打印任务状态到服务器：
    /// 通过SignalR调用ReportResult方法。
    /// </summary>
    private static Task SendStatusAsync(
        HubConnection connection,
        string agentId,
        Guid jobId,
        PrintDocumentDispatch doc,
        PrintTargetStatus status,
        string? message,
        CancellationToken token)
        => connection.InvokeAsync("ReportResult", new PrintTargetResult(
            jobId,
            doc.TargetId,
            agentId,
            doc.DocumentKey,
            doc.PrinterRole,
            doc.PrinterName,
            status,
            message,
            status is PrintTargetStatus.Completed or PrintTargetStatus.Failed ? DateTimeOffset.UtcNow : null), token);

    /// <summary>
    /// 处理桌面交付任务（ReportDelivery）：
    /// 用户在B/S界面点击"预览/打印"按钮，服务器推送PDF到Agent。
    /// 
    /// 流程：
    /// 1. 检查是否过期
    /// 2. 下载PDF文件
    /// 3. 确定打印方式：
    ///    - 静默打印：直接打印到默认/记住的打印机
    ///    - 预览打印：打开PDF预览窗口，用户选择打印机
    /// 4. 执行打印
    /// 5. 上报状态
    /// </summary>
    private async Task HandleDeliveryAsync(
        HubConnection connection,
        AgentOptions cfg,
        ReportDeliveryDispatch delivery,
        CancellationToken token)
    {
        try
        {
            // 检查是否过期
            if (delivery.ExpiresAt <= DateTimeOffset.UtcNow)
            {
                await SendDeliveryStatusAsync(connection, cfg.AgentId, delivery.JobId, ReportDeliveryStatus.Expired,
                    "The desktop delivery expired before it was received.", CancellationToken.None);
                return;
            }

            // 下载PDF文件
            await SendDeliveryStatusAsync(connection, cfg.AgentId, delivery.JobId, ReportDeliveryStatus.Downloading, null, token);
            var path = await artifacts.DownloadDeliveryAsync(cfg.ServerUrl, cfg.WorkDirectory, delivery, token);

            var canPrint = delivery.Action is ReportDeliveryAction.Print or ReportDeliveryAction.PreviewAndPrint;
            var installed = printers.GetInstalledPrinters();
            // 查找之前记住的打印机（按报表ID）
            var rememberedPrinter = canPrint
                ? deliveryPrinters.Resolve(
                    delivery.Djid,
                    delivery.PrinterName,
                    installed,
                    cfg.Printing.DefaultPrinter,
                    cfg.Printing.Silent)
                : null;

            // 静默打印：直接打印到记住的/默认打印机
            async Task PrintAsync(string selectedPrinter)
            {
                deliveryPrinters.Remember(delivery.Djid, selectedPrinter, installed);
                await QueueDeliveryPrintAsync(connection, cfg.AgentId, delivery, path, selectedPrinter);
            }

            if (canPrint && cfg.Printing.Silent)
            {
                if (string.IsNullOrWhiteSpace(rememberedPrinter))
                    throw new InvalidOperationException(
                        "Silent printing needs a remembered Djid printer, Agent:Printing:DefaultPrinter, or Windows default printer.");
                await PrintAsync(rememberedPrinter);
                return;
            }

            // 预览打印：打开PDF预览窗口
            var suggestedPrinter = rememberedPrinter ??
                DeliveryPrinterResolver.SuggestedDefault(installed, cfg.Printing.DefaultPrinter);
            await previewer.OpenAsync(new PdfPreviewRequest(
                path,
                delivery.FileName,
                canPrint ? PrintAsync : null,
                canPrint ? installed.Select(x => x.Name).ToArray() : null,
                suggestedPrinter), token);
            await SendDeliveryStatusAsync(connection, cfg.AgentId, delivery.JobId, ReportDeliveryStatus.Opened, null, token);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Desktop report delivery {JobId} failed", delivery.JobId);
            await SendDeliveryStatusAsync(connection, cfg.AgentId, delivery.JobId, ReportDeliveryStatus.Failed,
                ex.Message, CancellationToken.None);
        }
    }

    /// <summary>
    /// 将桌面交付任务入队到打印队列：
    /// 构建PrintDocumentDispatch对象，入队到对应打印机队列。
    /// </summary>
    private ValueTask QueueDeliveryPrintAsync(
        HubConnection connection,
        string agentId,
        ReportDeliveryDispatch delivery,
        string path,
        string printerName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(printerName);

        var document = new PrintDocumentDispatch(
            delivery.JobId,
            delivery.ArtifactId,
            delivery.DownloadPath,
            string.IsNullOrWhiteSpace(delivery.Djid) ? "desktop-report" : delivery.Djid,
            "uploaded-pdf",
            delivery.PrinterRole ?? delivery.Djid ?? string.Empty,
            printerName,
            Math.Max(1, delivery.Copies),
            delivery.Duplex);

        return queues.EnqueueAsync(new PrintWorkItem(
            delivery.JobId,
            document,
            path,
            (status, message) => SendDeliveryStatusAsync(
                connection,
                agentId,
                delivery.JobId,
                status switch
                {
                    PrintTargetStatus.Printing => ReportDeliveryStatus.Printing,
                    PrintTargetStatus.Completed => ReportDeliveryStatus.Completed,
                    PrintTargetStatus.Failed => ReportDeliveryStatus.Failed,
                    _ => ReportDeliveryStatus.Queued
                },
                message,
                CancellationToken.None)), CancellationToken.None);
    }

    /// <summary>
    /// 发送桌面交付状态到服务器：
    /// 通过SignalR调用ReportDeliveryResult方法。
    /// </summary>
    private static Task SendDeliveryStatusAsync(
        HubConnection connection,
        string agentId,
        Guid jobId,
        ReportDeliveryStatus status,
        string? message,
        CancellationToken token)
        => connection.InvokeAsync("ReportDeliveryResult", new ReportDeliveryResult(
            jobId, agentId, status, message, DateTimeOffset.UtcNow), token);

    /// <summary>
    /// 清理2小时前的旧PDF文件：
    /// 防止磁盘空间被大量打印任务占满。
    /// </summary>
    private static void CleanupOldArtifacts(string workDirectory)
    {
        var cutoff = DateTime.UtcNow.AddHours(-2);
        foreach (var file in Directory.EnumerateFiles(workDirectory, "*.pdf"))
        {
            try
            {
                if (File.GetLastWriteTimeUtc(file) < cutoff) File.Delete(file);
            }
            catch
            {
                // 文件可能被打印后台处理程序或杀毒软件锁定，稍后重试
            }
        }
    }
}
