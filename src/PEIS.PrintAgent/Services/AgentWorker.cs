using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Options;
using PEIS.PrintAgent.Printing;
using PEIS.Report.Contracts;

namespace PEIS.PrintAgent.Services;

public sealed class AgentWorker(
    IOptions<AgentOptions> options,
    PrinterCatalog printers,
    PrinterQueueManager queues,
    IHttpClientFactory httpClientFactory,
    ILogger<AgentWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var cfg = options.Value;
        if (string.IsNullOrWhiteSpace(cfg.AgentId)) cfg.AgentId = Environment.MachineName;
        if (string.IsNullOrWhiteSpace(cfg.StationId)) cfg.StationId = Environment.MachineName;
        Directory.CreateDirectory(cfg.WorkDirectory);
        CleanupOldArtifacts(cfg.WorkDirectory);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunConnectionAsync(cfg, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
            catch (Exception ex)
            {
                logger.LogError(ex, "Print agent connection stopped; reconnecting.");
                await Task.Delay(TimeSpan.FromSeconds(3), stoppingToken);
            }
        }
    }

    private async Task RunConnectionAsync(AgentOptions cfg, CancellationToken token)
    {
        var connection = new HubConnectionBuilder()
            .WithUrl($"{cfg.ServerUrl.TrimEnd('/')}/hubs/print-agent")
            .WithAutomaticReconnect()
            .Build();

        connection.On<PrintBatchDispatch>("PrintBatch", batch => HandleBatchAsync(connection, cfg, batch, token));
        connection.Reconnected += _ => RegisterAsync(connection, cfg, CancellationToken.None);

        await connection.StartAsync(token);
        await RegisterAsync(connection, cfg, token);
        logger.LogInformation("PrintAgent {AgentId} station {StationId} connected to {Server}", cfg.AgentId, cfg.StationId, cfg.ServerUrl);

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
            typeof(AgentWorker).Assembly.GetName().Version?.ToString() ?? "0.1.0"), token);

    private async Task HandleBatchAsync(HubConnection connection, AgentOptions cfg, PrintBatchDispatch batch, CancellationToken token)
    {
        try
        {
            // One B/S click may produce multiple different documents (A4 guide + barcode label).
            // Download each distinct artifact once on this workstation, then route it to the bound printer queue.
            var localPaths = new Dictionary<Guid, string>();
            foreach (var artifactGroup in batch.Documents.GroupBy(x => x.ArtifactId))
            {
                var first = artifactGroup.First();
                var path = Path.Combine(cfg.WorkDirectory, $"{first.ArtifactId:N}.pdf");

                foreach (var doc in artifactGroup)
                    await SendStatusAsync(connection, cfg.AgentId, batch.JobId, doc, PrintTargetStatus.Downloading, null, token);

                if (!File.Exists(path))
                {
                    var http = httpClientFactory.CreateClient("report-api");
                    using var response = await http.GetAsync(new Uri(new Uri(cfg.ServerUrl), first.DownloadPath), HttpCompletionOption.ResponseHeadersRead, token);
                    response.EnsureSuccessStatusCode();
                    await using var input = await response.Content.ReadAsStreamAsync(token);
                    await using var output = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
                    await input.CopyToAsync(output, token);
                }

                localPaths[first.ArtifactId] = path;
            }

            foreach (var doc in batch.Documents)
            {
                var path = localPaths[doc.ArtifactId];
                await queues.EnqueueAsync(new PrintWorkItem(batch.JobId, doc, path, (status, message) =>
                    SendStatusAsync(connection, cfg.AgentId, batch.JobId, doc, status, message, CancellationToken.None)), token);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to receive print batch {JobId}", batch.JobId);
            foreach (var doc in batch.Documents)
                await SendStatusAsync(connection, cfg.AgentId, batch.JobId, doc, PrintTargetStatus.Failed, ex.Message, CancellationToken.None);
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

    private static void CleanupOldArtifacts(string workDirectory)
    {
        var cutoff = DateTime.UtcNow.AddHours(-2);
        foreach (var file in Directory.EnumerateFiles(workDirectory, "*.pdf"))
        {
            try
            {
                if (File.GetLastWriteTimeUtc(file) < cutoff) File.Delete(file);
            }
            catch { }
        }
    }
}
