using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PEIS.PrintAgent.Printing;
using PEIS.PrintAgent.Previewing;
using PEIS.Report.Contracts;

namespace PEIS.PrintAgent.Services;

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
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var cfg = options.Value;
        cfg.AgentId = identityStore.GetOrCreate(cfg.AgentId);
        if (string.IsNullOrWhiteSpace(cfg.StationId) || string.Equals(cfg.StationId, "AUTO", StringComparison.OrdinalIgnoreCase))
            cfg.StationId = Environment.MachineName;
        Directory.CreateDirectory(cfg.WorkDirectory);
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
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Print agent connection stopped; reconnecting.");
                await Task.Delay(TimeSpan.FromSeconds(3), stoppingToken);
            }
        }
    }

    private async Task RunConnectionAsync(AgentOptions cfg, CancellationToken token)
    {
        var hubUrl = $"{cfg.ServerUrl.TrimEnd('/')}/hubs/print-agent";
        logger.LogInformation("Connecting to SignalR hub at {Url}...", hubUrl);
        var connection = new HubConnectionBuilder()
            .WithUrl(hubUrl, httpOptions =>
            {
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
            .WithAutomaticReconnect()
            .Build();

        connection.On<PrintBatchDispatch>("PrintBatch", batch => HandleBatchAsync(connection, cfg, batch, token));
        connection.On<ReportDeliveryDispatch>("ReportDelivery", delivery => HandleDeliveryAsync(connection, cfg, delivery, token));
        connection.Reconnected += _ => RegisterAsync(connection, cfg, CancellationToken.None);

        await connection.StartAsync(token);
        await RegisterAsync(connection, cfg, token);
        logger.LogInformation(
            "PrintAgent {AgentId} station {StationId} connected to {Server}",
            cfg.AgentId,
            cfg.StationId,
            cfg.ServerUrl);

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

    private Task RegisterAsync(HubConnection connection, AgentOptions cfg, CancellationToken token)
        => connection.InvokeAsync("Register", new AgentRegistration(
            cfg.AgentId,
            cfg.StationId,
            Environment.MachineName,
            printers.GetInstalledPrinters(),
            cfg.PrinterBindings,
            typeof(AgentWorker).Assembly.GetName().Version?.ToString() ?? "0.1.0",
            cfg.RegistrationToken), token);

    private async Task HandleBatchAsync(HubConnection connection, AgentOptions cfg, PrintBatchDispatch batch, CancellationToken token)
    {
        try
        {
            // One B/S click may produce multiple different documents (A4 guide + barcode label).
            // Distinct artifacts download in bounded parallelism; each physical printer remains serialized in its own queue.
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

    private async Task HandleDeliveryAsync(
        HubConnection connection,
        AgentOptions cfg,
        ReportDeliveryDispatch delivery,
        CancellationToken token)
    {
        try
        {
            if (delivery.ExpiresAt <= DateTimeOffset.UtcNow)
            {
                await SendDeliveryStatusAsync(connection, cfg.AgentId, delivery.JobId, ReportDeliveryStatus.Expired,
                    "The desktop delivery expired before it was received.", CancellationToken.None);
                return;
            }

            await SendDeliveryStatusAsync(connection, cfg.AgentId, delivery.JobId, ReportDeliveryStatus.Downloading, null, token);
            var path = await artifacts.DownloadDeliveryAsync(cfg.ServerUrl, cfg.WorkDirectory, delivery, token);

            var canPrint = delivery.Action is ReportDeliveryAction.Print or ReportDeliveryAction.PreviewAndPrint;
            var installed = printers.GetInstalledPrinters();
            var rememberedPrinter = canPrint
                ? deliveryPrinters.Resolve(
                    delivery.Djid,
                    delivery.PrinterName,
                    installed,
                    cfg.Printing.DefaultPrinter,
                    cfg.Printing.Silent)
                : null;

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

    private static Task SendDeliveryStatusAsync(
        HubConnection connection,
        string agentId,
        Guid jobId,
        ReportDeliveryStatus status,
        string? message,
        CancellationToken token)
        => connection.InvokeAsync("ReportDeliveryResult", new ReportDeliveryResult(
            jobId, agentId, status, message, DateTimeOffset.UtcNow), token);

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
                // A spooler or antivirus scan can temporarily retain a file; it will be retried later.
            }
        }
    }
}
