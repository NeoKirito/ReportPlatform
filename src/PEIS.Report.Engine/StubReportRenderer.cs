using System.Diagnostics;
using System.Text;
using PEIS.Report.Contracts;

namespace PEIS.Report.Engine;

/// <summary>
/// Deterministic renderer for CI, local development, and environments without a licensed FastReport reference.
/// It deliberately traverses the same definition/template/data/cache/telemetry/concurrency boundaries as the
/// production adapter while making no claim of FastReport or FRX fidelity.
/// </summary>
public sealed class StubReportRenderer(
    ReportDefinitionCache definitionCache,
    IReportDefinitionProvider definitions,
    ITemplateProvider templates,
    IReportDataProvider data,
    RenderConcurrencyGate renderGate,
    IReportRenderTelemetry telemetry) : IReportRenderer
{
    public async Task<ReportRenderResult> RenderPdfAsync(ReportRenderRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ReportId);
        cancellationToken.ThrowIfCancellationRequested();

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
        var afterCache = definitionCache.Snapshot();
        metrics.DefinitionCacheHit = afterCache.Hits > beforeCache.Hits;

        var template = await metrics.MeasureAsync("TemplateLoad", () => templates.GetRequiredAsync(definition, cancellationToken));
        var reportData = await metrics.MeasureAsync("SqlQuery", () => data.QueryAsync(definition, request, cancellationToken));
        metrics.Rows = reportData.RowCount;
        metrics.SqlResultSets = reportData.Tables.Count;
        await metrics.MeasureAsync("ImageDiscovery", () => Task.CompletedTask);
        await metrics.MeasureAsync("ImageResolve", () => Task.CompletedTask);

        byte[] pdf;
        using (await renderGate.EnterAsync(cancellationToken))
        {
            await metrics.MeasureAsync("FrxLoad", () => Task.CompletedTask);
            await metrics.MeasureAsync("RegisterData", () => Task.CompletedTask);
            await metrics.MeasureAsync("Prepare", () => Task.CompletedTask);
            await metrics.MeasureAsync("Watermark", () => Task.CompletedTask);
            pdf = await metrics.MeasureAsync("PdfExport", () => Task.FromResult(MinimalPdf(BuildStubText(request, definition, template, reportData))));
        }

        metrics.Pages = 1;
        metrics.PdfBytes = pdf.LongLength;
        await metrics.MeasureAsync("ArtifactWrite", () => Task.CompletedTask);
        var observation = metrics.Complete();
        telemetry.Record(observation);

        return new ReportRenderResult(
            pdf,
            PdfExportProfile.FileName(request.FileName, request.ReportId),
            1,
            observation.Timings);
    }

    private static string BuildStubText(
        ReportRenderRequest request,
        ReportDefinition definition,
        ReportTemplate template,
        ReportDataSet reportData)
        => string.Join('\n',
            "PEIS Report Platform",
            $"ReportId: {request.ReportId}",
            $"Profile: {PdfExportProfile.Normalize(request.Profile)}",
            $"Definition: {definition.Source}/{definition.Version}",
            $"Template: {template.TemplateKey}/{template.ContentHash[..12]}",
            $"Rows: {reportData.RowCount}",
            "Deterministic renderer - FastReport integration is not verified.");

    private static byte[] MinimalPdf(string text)
    {
        static string Escape(string s) => s.Replace("\\", "\\\\").Replace("(", "\\(").Replace(")", "\\)").Replace("\r", "").Replace("\n", ") Tj 0 -18 Td (");
        var stream = $"BT /F1 12 Tf 72 760 Td ({Escape(text)}) Tj ET";
        var objects = new[]
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] /Resources << /Font << /F1 5 0 R >> >> /Contents 4 0 R >>",
            $"<< /Length {Encoding.ASCII.GetByteCount(stream)} >>\nstream\n{stream}\nendstream",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>"
        };

        using var ms = new MemoryStream();
        using var writer = new StreamWriter(ms, Encoding.ASCII, 1024, leaveOpen: true) { NewLine = "\n" };
        writer.WriteLine("%PDF-1.4");
        writer.Flush();
        var offsets = new List<long> { 0 };
        for (var i = 0; i < objects.Length; i++)
        {
            offsets.Add(ms.Position);
            writer.WriteLine($"{i + 1} 0 obj");
            writer.WriteLine(objects[i]);
            writer.WriteLine("endobj");
            writer.Flush();
        }

        var xref = ms.Position;
        writer.WriteLine("xref");
        writer.WriteLine($"0 {objects.Length + 1}");
        writer.WriteLine("0000000000 65535 f ");
        foreach (var offset in offsets.Skip(1)) writer.WriteLine($"{offset:0000000000} 00000 n ");
        writer.WriteLine("trailer");
        writer.WriteLine($"<< /Size {objects.Length + 1} /Root 1 0 R >>");
        writer.WriteLine("startxref");
        writer.WriteLine(xref);
        writer.WriteLine("%%EOF");
        writer.Flush();
        return ms.ToArray();
    }
}
