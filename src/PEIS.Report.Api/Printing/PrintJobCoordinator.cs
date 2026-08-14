using Microsoft.AspNetCore.SignalR;
using PEIS.Report.Api.Hubs;
using PEIS.Report.Api.Storage;
using PEIS.Report.Contracts;
using PEIS.Report.Engine;

namespace PEIS.Report.Api.Printing;

/// <summary>
/// Diagnostic/manual API. It keeps explicit physical printer targets for installation/testing.
/// Normal PEIS B/S printing should use BusinessPrintCoordinator and logical printer roles.
/// </summary>
public sealed class PrintJobCoordinator(
    IReportRenderer renderer,
    IPdfArtifactStore artifacts,
    IHubContext<PrintAgentHub> hub,
    AgentRegistry registry,
    PrintJobStateStore states)
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

        // Manual fan-out still renders the same PDF only once.
        var rendered = await renderer.RenderPdfAsync(request.Report, cancellationToken);
        var artifactId = await artifacts.SaveAsync(rendered.Pdf, rendered.FileName, cancellationToken);
        var jobId = Guid.NewGuid();
        var createdAt = DateTimeOffset.UtcNow;
        var jobName = request.JobName ?? request.Report.ReportId;

        var targetStates = new List<PrintTargetResult>(request.Targets.Count);
        var batches = new List<(string AgentId, PrintBatchDispatch Batch)>();

        foreach (var group in request.Targets.GroupBy(x => x.AgentId, StringComparer.OrdinalIgnoreCase))
        {
            var documents = group.Select(target =>
            {
                var targetId = Guid.NewGuid();
                targetStates.Add(new PrintTargetResult(
                    jobId, targetId, target.AgentId, "manual", "MANUAL", target.PrinterName, PrintTargetStatus.Queued));

                return new PrintDocumentDispatch(
                    targetId,
                    artifactId,
                    $"/api/print/artifacts/{artifactId}",
                    "manual",
                    request.Report.ReportId,
                    "MANUAL",
                    target.PrinterName,
                    target.Copies,
                    target.Duplex);
            }).ToArray();

            batches.Add((group.Key, new PrintBatchDispatch(jobId, jobName, documents)));
        }

        states.Initialize(jobId, targetStates);

        await Task.WhenAll(batches.Select(x =>
            hub.Clients.Group(PrintAgentHub.GroupName(x.AgentId)).SendAsync("PrintBatch", x.Batch, cancellationToken)));

        return new CreatePrintJobResponse(jobId, 1, request.Targets.Count, createdAt);
    }
}
