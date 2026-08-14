using PEIS.Report.Contracts;

namespace PEIS.Report.Engine;

/// <summary>
/// Adapter contract implemented in an isolated FastReport integration project once an approved runtime package and
/// its FRX/data-registration rules are configured. Controllers and PrintAgent never reference FastReport types directly.
/// </summary>
public interface IFastReportRuntime
{
    Task<FastReportRuntimePreparation> PrepareAsync(FastReportRenderContext context, CancellationToken cancellationToken);
    Task ApplyWatermarkAsync(IFastReportPreparedDocument prepared, WatermarkOptions watermark, CancellationToken cancellationToken);
    Task<FastReportPdfOutput> ExportPdfAsync(IFastReportPreparedDocument prepared, PdfExportProfile profile, CancellationToken cancellationToken);
}

public interface IFastReportPreparedDocument : IAsyncDisposable
{
}

/// <summary>Prepared per-request document plus timings produced inside the renderer-specific integration boundary.</summary>
public sealed record FastReportRuntimePreparation(
    IFastReportPreparedDocument Document,
    IReadOnlyList<ReportStageTiming> Timings);

public sealed record FastReportRenderContext(
    ReportRenderRequest Request,
    ReportDefinition Definition,
    ReportTemplate Template,
    ReportDataSet Data,
    PdfExportProfile Profile);

public sealed record FastReportPdfOutput(byte[] Pdf, int PageCount);

public sealed class FastReportIntegrationUnavailableException(string message) : InvalidOperationException(message);

/// <summary>
/// Production rendering pipeline. The concrete FastReport runtime is intentionally injected so the renderer package
/// is confined to an integration boundary and each request creates an independent, mutable report instance.
/// </summary>
public sealed class FastReportReportRenderer(
    ReportDefinitionCache definitionCache,
    IReportDefinitionProvider definitions,
    ITemplateProvider templates,
    IReportDataProvider data,
    RenderConcurrencyGate renderGate,
    IFastReportRuntime runtime,
    IReportRenderTelemetry telemetry) : IReportRenderer
{
    public async Task<ReportRenderResult> RenderPdfAsync(ReportRenderRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ReportId);
        var metrics = new ReportRenderMetrics
        {
            RequestId = Guid.NewGuid().ToString("N"),
            ReportId = request.ReportId,
            Profile = PdfExportProfile.Normalize(request.Profile)
        };

        var cacheKey = request.ReportId;
        if (definitions is IReportDefinitionVersionProvider versionProvider)
        {
            var version = await metrics.MeasureAsync("DefinitionVersionCheck", () => versionProvider.GetVersionAsync(request, cancellationToken));
            cacheKey = ReportDefinitionCache.BuildCacheKey(request.ReportId, version);
        }
        var beforeCache = definitionCache.Snapshot();
        var definition = await metrics.MeasureAsync("DefinitionLoad", () =>
            definitionCache.GetOrCreateAsync(cacheKey, token => definitions.GetRequiredAsync(request, token), cancellationToken));
        metrics.DefinitionCacheHit = definitionCache.Snapshot().Hits > beforeCache.Hits;
        var template = await metrics.MeasureAsync("TemplateDecode", () => templates.GetRequiredAsync(definition, cancellationToken));
        var reportData = await metrics.MeasureAsync("SqlQuery", () => data.QueryAsync(definition, request, cancellationToken));
        metrics.Rows = reportData.RowCount;
        metrics.SqlResultSets = reportData.Tables.Count;
        await metrics.MeasureAsync("ImageDiscovery", () => Task.CompletedTask);
        await metrics.MeasureAsync("ImageResolve", () => Task.CompletedTask);

        FastReportPdfOutput output;
        using (await renderGate.EnterAsync(cancellationToken))
        {
            var profile = PdfExportProfile.Resolve(request.Profile);
            var context = new FastReportRenderContext(request, definition, template, reportData, profile);
            var preparation = await runtime.PrepareAsync(context, cancellationToken);
            foreach (var timing in preparation.Timings)
                metrics.Record(timing.Stage, timing.ElapsedMilliseconds);
            await using var prepared = preparation.Document;
            await metrics.MeasureAsync("Watermark", () => runtime.ApplyWatermarkAsync(prepared, request.Watermark ?? new WatermarkOptions(), cancellationToken));
            output = await metrics.MeasureAsync("PdfExport", () => runtime.ExportPdfAsync(prepared, profile, cancellationToken));
        }

        metrics.Pages = output.PageCount;
        metrics.PdfBytes = output.Pdf.LongLength;
        await metrics.MeasureAsync("ArtifactWrite", () => Task.CompletedTask);
        var observation = metrics.Complete();
        telemetry.Record(observation);
        return new ReportRenderResult(output.Pdf, PdfExportProfile.FileName(request.FileName, request.ReportId), output.PageCount, observation.Timings);
    }
}

/// <summary>
/// Explicit Integration Gate used when the commercial runtime is not configured. It is never substituted for a
/// different report engine and therefore does not compromise FRX compatibility.
/// </summary>
public sealed class MissingFastReportRuntime : IFastReportRuntime
{
    private const string Message = "FastReport rendering is blocked until an approved, .NET-compatible FastReport runtime package is configured.";

    public Task<FastReportRuntimePreparation> PrepareAsync(FastReportRenderContext context, CancellationToken cancellationToken)
        => Task.FromException<FastReportRuntimePreparation>(new FastReportIntegrationUnavailableException(Message));

    public Task ApplyWatermarkAsync(IFastReportPreparedDocument prepared, WatermarkOptions watermark, CancellationToken cancellationToken)
        => Task.FromException(new FastReportIntegrationUnavailableException(Message));

    public Task<FastReportPdfOutput> ExportPdfAsync(IFastReportPreparedDocument prepared, PdfExportProfile profile, CancellationToken cancellationToken)
        => Task.FromException<FastReportPdfOutput>(new FastReportIntegrationUnavailableException(Message));
}
