using Microsoft.AspNetCore.SignalR;
using PEIS.Report.Api.Hubs;
using PEIS.Report.Api.Storage;
using PEIS.Report.Contracts;
using PEIS.Report.Engine;

namespace PEIS.Report.Api.Printing;

/// <summary>
/// Diagnostic/manual print path. It is protected at the endpoint and still persists target state before dispatch.
/// Normal PEIS B/S flows use <see cref="BusinessPrintCoordinator"/> and never submit physical printer names.
/// </summary>
public sealed class PrintJobCoordinator(
    IReportRenderer renderer,
    IPdfArtifactStore artifacts,
    IHubContext<PrintAgentHub> hub,
    AgentRegistry registry,
    PrintJobStateStore states,
    ArtifactDownloadAuthorizer downloads)
{
    public async Task<CreatePrintJobResponse> CreateAsync(CreatePrintJobRequest request, CancellationToken cancellationToken)
    {
        if (request.Targets.Count == 0) throw new ArgumentException("At least one print target is required.");
        if (request.Targets.Any(x => x.Copies < 1)) throw new ArgumentException("Copies must be >= 1.");

        var online = registry.Snapshot().ToDictionary(x => x.AgentId, StringComparer.OrdinalIgnoreCase);
        foreach (var target in request.Targets)
        {
            if (!online.TryGetValue(target.AgentId, out var agent))
                throw new InvalidOperationException($"Print agent '{target.AgentId}' is offline.");
            if (!agent.Printers.Any(x => string.Equals(x.Name, target.PrinterName, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException($"Printer '{target.PrinterName}' is not registered by agent '{target.AgentId}'.");
        }

        var rendered = await renderer.RenderPdfAsync(request.Report, cancellationToken);
        var artifactId = await artifacts.SaveAsync(rendered.Pdf, rendered.FileName, cancellationToken);
        var jobId = Guid.NewGuid();
        var createdAt = DateTimeOffset.UtcNow;
        var jobName = request.JobName ?? request.Report.ReportId;
        var targets = new List<PrintJobTargetState>(request.Targets.Count);
        var batches = new List<(string AgentId, PrintBatchDispatch Batch)>();

        foreach (var group in request.Targets.GroupBy(x => x.AgentId, StringComparer.OrdinalIgnoreCase))
        {
            var documents = group.Select(target =>
            {
                var targetId = Guid.NewGuid();
                var state = new PrintTargetResult(jobId, targetId, target.AgentId, "manual", "MANUAL", target.PrinterName, PrintTargetStatus.Queued);
                targets.Add(new PrintJobTargetState(state, artifactId));
                return new PrintDocumentDispatch(
                    targetId,
                    artifactId,
                    downloads.CreateDownloadPath(artifactId, target.AgentId),
                    "manual",
                    request.Report.ReportId,
                    "MANUAL",
                    target.PrinterName,
                    target.Copies,
                    target.Duplex);
            }).ToArray();
            batches.Add((group.Key, new PrintBatchDispatch(jobId, jobName, documents)));
        }

        await states.InitializeAsync(new PrintJobInitialization(
            new PrintJobRecord(jobId, "MANUAL", null, string.Join(',', batches.Select(x => x.AgentId)), jobName,
                null, createdAt, createdAt, 0, null, null), targets), cancellationToken);
        await Task.WhenAll(batches.Select(x =>
            hub.Clients.Group(PrintAgentHub.GroupName(x.AgentId)).SendAsync("PrintBatch", x.Batch, cancellationToken)));
        await states.MarkDispatchedAsync(jobId, targets.Select(x => x.Result.TargetId), cancellationToken);
        return new CreatePrintJobResponse(jobId, 1, request.Targets.Count, createdAt);
    }
}
