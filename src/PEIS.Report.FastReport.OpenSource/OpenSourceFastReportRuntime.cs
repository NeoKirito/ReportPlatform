using System.Data;
using System.Diagnostics;
using System.Drawing;
using FastReport;
using FastReport.Data;
using FastReport.Export.PdfSimple;
using FastReport.Utils;
using PEIS.Report.Contracts;
using PEIS.Report.Engine;
using FastReportReport = FastReport.Report;

namespace PEIS.Report.FastReport.OpenSource;

/// <summary>
/// MIT-licensed FastReport Open Source implementation. Mutable <see cref="Report"/> instances are created for one
/// render request only and are retained only by the corresponding prepared-document handle.
/// </summary>
public sealed class OpenSourceFastReportRuntime(IImageResolver? imageResolver = null) : IFastReportRuntime
{
    public static void WarmupCompiler() => Config.CompilerWarmup();

    internal static int ResolveImageDpi(PdfExportProfile profile)
        => profile.IsLabel || profile.IntendedForPrint || string.Equals(profile.Name, "archive", StringComparison.OrdinalIgnoreCase)
            ? 300
            : string.Equals(profile.Name, "screen", StringComparison.OrdinalIgnoreCase) ? 150 : 200;

    public async Task<FastReportRuntimePreparation> PrepareAsync(
        FastReportRenderContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();

        var report = new FastReportReport();
        try
        {
            var (normalizedTemplate, emptyDataPageNames) = LegacyFrxCompatibility.GetNormalizedWithEmptyPages(
                context.Template.Content,
                context.Data.Tables);
            ReportImagePreparation.Result? images = null;
            if (imageResolver is not null)
                images = await ReportImagePreparation.PrepareAsync(normalizedTemplate, context.Data.Tables, imageResolver, cancellationToken).ConfigureAwait(false);
            var frxLoad = Stopwatch.StartNew();
            report.LoadFromString(images?.Template ?? normalizedTemplate);
            frxLoad.Stop();

            var registerData = Stopwatch.StartNew();
            foreach (var table in context.Data.Tables)
            {
                // The real xmtm FRX is bound to Master. Preserve database-owned names instead of adding aliases.
                report.RegisterData(table.Value, table.Key);
                var source = report.GetDataSource(table.Key);
                if (source is not null)
                    source.Enabled = true;
            }
            EnsureDataSourcesAndSchemas(report, context.Data.Tables);
            ApplyParameters(report, context.Request);
            registerData.Stop();

            cancellationToken.ThrowIfCancellationRequested();
            var prepare = Stopwatch.StartNew();
            SuppressTrailingEmptyPages(report, emptyDataPageNames);
            report.Prepare();
            var exportedPageCount = report.PreparedPages.Count;
            prepare.Stop();
            cancellationToken.ThrowIfCancellationRequested();
            return new FastReportRuntimePreparation(
                new OpenSourceFastReportPreparedDocument(report, exportedPageCount),
                [
                    new ReportStageTiming("ImageResolve", images?.Batch.ElapsedMilliseconds ?? 0),
                    new ReportStageTiming("FrxLoad", frxLoad.ElapsedMilliseconds),
                    new ReportStageTiming("RegisterData", registerData.ElapsedMilliseconds),
                    new ReportStageTiming("Prepare", prepare.ElapsedMilliseconds)
                ]) { Images = images?.Batch };
        }
        catch
        {
            report.Dispose();
            throw;
        }
    }

    private static void SuppressTrailingEmptyPages(
        FastReportReport report,
        IReadOnlySet<string> emptyDataPageNames)
    {
        if (emptyDataPageNames.Count == 0 || report.Pages.Count <= 1)
            return;

        // Never suppress the primary page (index 0); only trailing optional pages
        for (var index = report.Pages.Count - 1; index > 0; index--)
        {
            if (report.Pages[index] is not ReportPage page || !emptyDataPageNames.Contains(page.Name))
                break;
            // Exclude the optional page before Prepare so TotalPages matches the
            // exported PDF; trimming prepared pages afterwards leaves stale footers.
            page.Visible = false;
        }
    }

    public Task ApplyWatermarkAsync(
        IFastReportPreparedDocument prepared,
        WatermarkOptions watermark,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(prepared);
        ArgumentNullException.ThrowIfNull(watermark);
        cancellationToken.ThrowIfCancellationRequested();

        if (prepared is not OpenSourceFastReportPreparedDocument document)
            throw new ArgumentException("Prepared document was not created by FastReport Open Source runtime.", nameof(prepared));
        if (!watermark.Enabled || string.IsNullOrWhiteSpace(watermark.Text))
            return Task.CompletedTask;

        var text = watermark.Text.Trim();
        var alpha = (int)Math.Round(Math.Clamp(watermark.Opacity, 0d, 1d) * byte.MaxValue, MidpointRounding.AwayFromZero);
        var rotation = watermark.Angle < 0 ? WatermarkTextRotation.ForwardDiagonal : WatermarkTextRotation.BackwardDiagonal;
        for (var index = 0; index < document.ExportedPageCount; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var page = document.Report.PreparedPages.GetPage(index);
            if (page is null)
                continue;

            // Prepared pages are exported by PDFSimple; modifying and replacing each prepared page ensures the overlay
            // is visible in the produced PDF rather than only in the original FRX page definition.
            page.Watermark.Enabled = true;
            page.Watermark.Text = text;
            page.Watermark.Font = new Font("Arial", 54, FontStyle.Bold);
            page.Watermark.TextFill = new SolidFill(Color.FromArgb(alpha, Color.Gray));
            page.Watermark.TextRotation = rotation;
            page.Watermark.ShowTextOnTop = true;
            document.Report.PreparedPages.ModifyPage(index, page);
        }

        return Task.CompletedTask;
    }

    public Task<FastReportPdfOutput> ExportPdfAsync(
        IFastReportPreparedDocument prepared,
        PdfExportProfile profile,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(prepared);
        ArgumentNullException.ThrowIfNull(profile);
        cancellationToken.ThrowIfCancellationRequested();

        if (prepared is not OpenSourceFastReportPreparedDocument document)
            throw new ArgumentException("Prepared document was not created by FastReport Open Source runtime.", nameof(prepared));

        using var stream = new MemoryStream();
        using var exporter = new PDFSimpleExport
        {
            ImageDpi = ResolveImageDpi(profile),
            JpegQuality = profile.JpegQuality
        };
        if (document.ExportedPageCount < document.Report.PreparedPages.Count)
        {
            exporter.PageRange = PageRange.PageNumbers;
            exporter.PageNumbers = $"1-{document.ExportedPageCount}";
        }
        document.Report.Export(exporter, stream);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new FastReportPdfOutput(stream.ToArray(), document.ExportedPageCount));
    }

    private static void ApplyParameters(FastReportReport report, ReportRenderRequest request)
    {
        var parameters = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

        foreach (var (key, element) in request.Parameters)
            parameters[key] = JsonScalarToObject(element);

        if (request.LegacyPayload is { ValueKind: System.Text.Json.JsonValueKind.Object } payload)
            ExtractPayloadParameters(payload, parameters);

        foreach (var (key, value) in parameters)
        {
            try
            {
                report.SetParameterValue(key, value);
            }
            catch
            {
                // Template may not define this parameter; ignore
            }
        }
    }

    private static void ExtractPayloadParameters(System.Text.Json.JsonElement element, Dictionary<string, object?> parameters)
    {
        if (element.ValueKind != System.Text.Json.JsonValueKind.Object) return;
        foreach (var prop in element.EnumerateObject())
        {
            if (prop.Value.ValueKind == System.Text.Json.JsonValueKind.Object)
            {
                ExtractPayloadParameters(prop.Value, parameters);
            }
            else if (!parameters.ContainsKey(prop.Name))
            {
                parameters[prop.Name] = JsonScalarToObject(prop.Value);
            }
        }
    }

    private static object? JsonScalarToObject(System.Text.Json.JsonElement element) => element.ValueKind switch
    {
        System.Text.Json.JsonValueKind.String => element.GetString(),
        System.Text.Json.JsonValueKind.Number when element.TryGetInt64(out var l) => l,
        System.Text.Json.JsonValueKind.Number when element.TryGetDecimal(out var d) => d,
        System.Text.Json.JsonValueKind.True => true,
        System.Text.Json.JsonValueKind.False => false,
        System.Text.Json.JsonValueKind.Null => null,
        _ => null
    };

    private static void EnsureDataSourcesAndSchemas(
        FastReportReport report,
        IReadOnlyDictionary<string, DataTable> tables)
    {
        foreach (Base b in report.Dictionary.DataSources)
        {
            if (b is not TableDataSource tds)
                continue;

            var refName = tds.ReferenceName ?? tds.Name;
            DataTable? table = null;

            // 1. Direct match by ReferenceName, Name, or Alias
            if (!tables.TryGetValue(refName, out table) &&
                !tables.TryGetValue(tds.Name, out table) &&
                (string.IsNullOrWhiteSpace(tds.Alias) || !tables.TryGetValue(tds.Alias, out table)))
            {
                // 2. Intelligent column overlap match against available tables
                if (tds.Columns.Count > 0)
                {
                    var bestOverlap = 0;
                    DataTable? bestTable = null;
                    foreach (var candidate in tables.Values)
                    {
                        var overlap = 0;
                        foreach (Column col in tds.Columns)
                        {
                            if (candidate.Columns.Contains(col.Name))
                                overlap++;
                        }
                        if (overlap > bestOverlap && overlap >= Math.Min(2, tds.Columns.Count))
                        {
                            bestOverlap = overlap;
                            bestTable = candidate;
                        }
                    }
                    if (bestTable is not null)
                    {
                        table = bestTable;
                        report.RegisterData(table, refName);
                        tds.Enabled = true;
                    }
                }
            }

            if (table is null)
            {
                // 3. No matching table registered: create an empty table with all declared columns so Roslyn compiles without errors
                table = new DataTable(refName);
                foreach (Column col in tds.Columns)
                {
                    table.Columns.Add(col.Name, col.DataType ?? typeof(string));
                }
                report.RegisterData(table, refName);
                tds.Enabled = true;
            }
            else
            {
                // 4. Ensure all declared schema columns exist in the table so Roslyn compiles without errors
                foreach (Column col in tds.Columns)
                {
                    if (!table.Columns.Contains(col.Name))
                    {
                        table.Columns.Add(col.Name, col.DataType ?? typeof(string));
                    }
                }
                tds.Enabled = true;
            }
        }
    }

    private sealed class OpenSourceFastReportPreparedDocument(
        FastReportReport report,
        int exportedPageCount) : IFastReportPreparedDocument
    {
        private int _disposed;

        public FastReportReport Report { get; } = report;
        public int ExportedPageCount { get; } = exportedPageCount;

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                try
                {
                    Report.PreparedPages?.Clear();
                    Report.Dictionary?.Clear();
                    Report.Clear();
                }
                catch
                {
                    // Ignore non-fatal cleanup errors
                }
                finally
                {
                    Report.Dispose();
                }
            }
            return ValueTask.CompletedTask;
        }
    }
}
