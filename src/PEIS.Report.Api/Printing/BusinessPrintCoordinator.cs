using Microsoft.AspNetCore.SignalR;
using PEIS.Report.Api.Hubs;
using PEIS.Report.Api.Storage;
using PEIS.Report.Contracts;
using PEIS.Report.Engine;

namespace PEIS.Report.Api.Printing;

/// <summary>
/// Resolves one PEIS business action into logical printer roles. The durable job is created before SignalR dispatch,
/// and a completed idempotency key is recorded before dispatch so a timeout/retry cannot create a second job.
/// </summary>
public sealed class BusinessPrintCoordinator(
    IReportRenderer renderer,
    IPdfArtifactStore artifacts,
    IHubContext<PrintAgentHub> hub,
    AgentRegistry registry,
    PrintScenarioCatalog scenarios,
    PrintJobStateStore states,
    PrintRequestIdempotencyStore idempotency,
    ArtifactDownloadAuthorizer downloads)
{
    public async Task<CreatePrintJobResponse> CreateAsync(BusinessPrintRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.IdempotencyKey))
        {
            var staged = await CreatePersistedAsync(request, null, cancellationToken);
            await DispatchAsync(staged, cancellationToken);
            return staged.Response;
        }

        var key = request.IdempotencyKey.Trim();
        var reservation = await idempotency.ReserveAsync(request.ActionCode, request.StationId, key, cancellationToken);
        if (reservation.Status == IdempotencyReservationStatus.Existing) return reservation.ExistingResponse!;
        if (reservation.Status == IdempotencyReservationStatus.Pending)
            throw new InvalidOperationException("A request with this idempotency key is already being processed. Retry using the same key after it completes.");

        var completed = false;
        try
        {
            var staged = await CreatePersistedAsync(request, key, cancellationToken);
            await idempotency.CompleteAsync(request.ActionCode, request.StationId, key, staged.Response, cancellationToken);
            completed = true;
            await DispatchAsync(staged, cancellationToken);
            return staged.Response;
        }
        catch
        {
            if (!completed)
                await idempotency.ReleaseAsync(request.ActionCode, request.StationId, key, CancellationToken.None);
            throw;
        }
    }

    private async Task<StagedJob> CreatePersistedAsync(BusinessPrintRequest request, string? idempotencyKey, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.ActionCode)) throw new ArgumentException("ActionCode is required.");
        if (string.IsNullOrWhiteSpace(request.StationId)) throw new ArgumentException("StationId is required.");

        var scenario = scenarios.GetRequired(request.ActionCode);
        var agent = registry.FindByStation(request.StationId)
            ?? throw new InvalidOperationException($"Print station '{request.StationId}' is offline.");
        var resolved = scenario.Documents.Select(document => Resolve(agent, document)).ToArray();

        var rendered = await Task.WhenAll(resolved.Select(async item =>
        {
            var reportRequest = new ReportRenderRequest(
                item.Definition.ReportId,
                request.Parameters,
                item.Definition.Profile,
                new WatermarkOptions(item.Definition.WatermarkEnabled, item.Definition.WatermarkText),
                item.Definition.FileName);
            var result = await renderer.RenderPdfAsync(reportRequest, cancellationToken);
            var artifactId = await artifacts.SaveAsync(result.Pdf, result.FileName, cancellationToken);
            return (item.Definition, item.PrinterName, artifactId);
        }));

        var jobId = Guid.NewGuid();
        var createdAt = DateTimeOffset.UtcNow;
        var jobName = string.IsNullOrWhiteSpace(request.JobName) ? scenario.JobName : request.JobName!;
        var targets = new List<PrintJobTargetState>(rendered.Length);
        var documents = new List<PrintDocumentDispatch>(rendered.Length);

        foreach (var item in rendered)
        {
            var targetId = Guid.NewGuid();
            var state = new PrintTargetResult(
                jobId, targetId, agent.AgentId, item.Definition.Key, item.Definition.PrinterRole,
                item.PrinterName, PrintTargetStatus.Queued);
            targets.Add(new PrintJobTargetState(state, item.artifactId));
            documents.Add(new PrintDocumentDispatch(
                targetId,
                item.artifactId,
                downloads.CreateDownloadPath(item.artifactId, agent.AgentId),
                item.Definition.Key,
                item.Definition.ReportId,
                item.Definition.PrinterRole,
                item.PrinterName,
                Math.Max(1, item.Definition.Copies),
                item.Definition.Duplex));
        }

        var response = new CreatePrintJobResponse(jobId, documents.Count, documents.Count, createdAt);
        await states.InitializeAsync(new PrintJobInitialization(
            new PrintJobRecord(jobId, request.ActionCode.Trim(), request.StationId.Trim(), agent.AgentId, jobName,
                idempotencyKey, createdAt, createdAt, 0, null, null), targets), cancellationToken);
        return new StagedJob(response, agent.AgentId, new PrintBatchDispatch(jobId, jobName, documents));
    }

    private async Task DispatchAsync(StagedJob job, CancellationToken cancellationToken)
    {
        await hub.Clients.Group(PrintAgentHub.GroupName(job.AgentId)).SendAsync("PrintBatch", job.Batch, cancellationToken);
        await states.MarkDispatchedAsync(job.Response.JobId, job.Batch.Documents.Select(x => x.TargetId), cancellationToken);
    }

    private static ResolvedDocument Resolve(AgentRegistry.AgentState agent, PrintScenarioDocumentOptions definition)
    {
        if (string.IsNullOrWhiteSpace(definition.ReportId))
            throw new InvalidOperationException($"Document '{definition.Key}' has no ReportId.");
        if (string.IsNullOrWhiteSpace(definition.PrinterRole))
            throw new InvalidOperationException($"Document '{definition.Key}' has no PrinterRole.");
        if (!agent.PrinterBindings.TryGetValue(definition.PrinterRole, out var printerName) || string.IsNullOrWhiteSpace(printerName))
            throw new InvalidOperationException($"Station '{agent.StationId}' has no printer bound to role '{definition.PrinterRole}'.");
        if (!agent.Printers.Any(x => string.Equals(x.Name, printerName, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException($"Station '{agent.StationId}' maps role '{definition.PrinterRole}' to '{printerName}', but that printer is not installed.");
        return new ResolvedDocument(definition, printerName);
    }

    private sealed record ResolvedDocument(PrintScenarioDocumentOptions Definition, string PrinterName);
    private sealed record StagedJob(CreatePrintJobResponse Response, string AgentId, PrintBatchDispatch Batch);
}
