using Microsoft.AspNetCore.SignalR;
using PEIS.Report.Api.Hubs;
using PEIS.Report.Api.Storage;
using PEIS.Report.Contracts;
using PEIS.Report.Engine;

namespace PEIS.Report.Api.Printing;

/// <summary>
/// Resolves one PEIS business action into multiple rendered documents and routes each one
/// to a logical printer role on the target workstation. Physical printer names never appear
/// in the normal B/S request.
/// </summary>
public sealed class BusinessPrintCoordinator(
    IReportRenderer renderer,
    IPdfArtifactStore artifacts,
    IHubContext<PrintAgentHub> hub,
    AgentRegistry registry,
    PrintScenarioCatalog scenarios,
    PrintJobStateStore states,
    PrintRequestIdempotencyStore idempotency)
{
    public Task<CreatePrintJobResponse> CreateAsync(BusinessPrintRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.IdempotencyKey))
            return CreateCoreAsync(request, cancellationToken);

        return idempotency.GetOrCreateAsync(
            request.ActionCode,
            request.StationId,
            request.IdempotencyKey,
            () => CreateCoreAsync(request, cancellationToken));
    }

    private async Task<CreatePrintJobResponse> CreateCoreAsync(BusinessPrintRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.ActionCode)) throw new ArgumentException("ActionCode is required.");
        if (string.IsNullOrWhiteSpace(request.StationId)) throw new ArgumentException("StationId is required.");

        var scenario = scenarios.GetRequired(request.ActionCode);
        var agent = registry.FindByStation(request.StationId)
            ?? throw new InvalidOperationException($"Print station '{request.StationId}' is offline.");

        var resolved = scenario.Documents.Select(document => Resolve(agent, document)).ToArray();

        // Different documents from one B/S click are independent. Render them concurrently.
        // The production renderer should still have a global bounded render scheduler so many users
        // cannot create unbounded FastReport concurrency.
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
        var jobName = string.IsNullOrWhiteSpace(request.JobName) ? scenario.JobName : request.JobName;

        var targetStates = new List<PrintTargetResult>(rendered.Length);
        var documents = new List<PrintDocumentDispatch>(rendered.Length);

        foreach (var item in rendered)
        {
            var targetId = Guid.NewGuid();
            targetStates.Add(new PrintTargetResult(
                jobId,
                targetId,
                agent.AgentId,
                item.Definition.Key,
                item.Definition.PrinterRole,
                item.PrinterName,
                PrintTargetStatus.Queued));

            documents.Add(new PrintDocumentDispatch(
                targetId,
                item.artifactId,
                $"/api/print/artifacts/{item.artifactId}",
                item.Definition.Key,
                item.Definition.ReportId,
                item.Definition.PrinterRole,
                item.PrinterName,
                Math.Max(1, item.Definition.Copies),
                item.Definition.Duplex));
        }

        states.Initialize(jobId, targetStates);

        await hub.Clients.Group(PrintAgentHub.GroupName(agent.AgentId))
            .SendAsync("PrintBatch", new PrintBatchDispatch(jobId, jobName!, documents), cancellationToken);

        return new CreatePrintJobResponse(jobId, documents.Count, documents.Count, createdAt);
    }

    private static ResolvedDocument Resolve(AgentRegistry.AgentState agent, PrintScenarioDocumentOptions definition)
    {
        if (string.IsNullOrWhiteSpace(definition.ReportId))
            throw new InvalidOperationException($"Document '{definition.Key}' has no ReportId.");
        if (string.IsNullOrWhiteSpace(definition.PrinterRole))
            throw new InvalidOperationException($"Document '{definition.Key}' has no PrinterRole.");

        if (!agent.PrinterBindings.TryGetValue(definition.PrinterRole, out var printerName) || string.IsNullOrWhiteSpace(printerName))
            throw new InvalidOperationException(
                $"Station '{agent.StationId}' has no printer bound to role '{definition.PrinterRole}'.");

        if (!agent.Printers.Any(x => string.Equals(x.Name, printerName, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException(
                $"Station '{agent.StationId}' maps role '{definition.PrinterRole}' to '{printerName}', but that printer is not installed.");

        return new ResolvedDocument(definition, printerName);
    }

    private sealed record ResolvedDocument(PrintScenarioDocumentOptions Definition, string PrinterName);
}
