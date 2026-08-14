using System.Diagnostics;
using FastReport;
using FastReport.Export.PdfSimple;
using PEIS.Report.Contracts;
using PEIS.Report.Engine;
using FastReportReport = FastReport.Report;

namespace PEIS.Report.FastReport.OpenSource;

/// <summary>
/// MIT-licensed FastReport Open Source implementation. Mutable <see cref="Report"/> instances are created for one
/// render request only and are retained only by the corresponding prepared-document handle.
/// </summary>
public sealed class OpenSourceFastReportRuntime : IFastReportRuntime
{
    public Task<FastReportRuntimePreparation> PrepareAsync(
        FastReportRenderContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();

        var report = new FastReportReport();
        try
        {
            var frxLoad = Stopwatch.StartNew();
            report.LoadFromString(context.Template.Content);
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
            registerData.Stop();

            cancellationToken.ThrowIfCancellationRequested();
            var prepare = Stopwatch.StartNew();
            report.Prepare();
            prepare.Stop();
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new FastReportRuntimePreparation(
                new OpenSourceFastReportPreparedDocument(report),
                [
                    new ReportStageTiming("FrxLoad", frxLoad.ElapsedMilliseconds),
                    new ReportStageTiming("RegisterData", registerData.ElapsedMilliseconds),
                    new ReportStageTiming("Prepare", prepare.ElapsedMilliseconds)
                ]));
        }
        catch
        {
            report.Dispose();
            throw;
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

        if (prepared is not OpenSourceFastReportPreparedDocument)
            throw new ArgumentException("Prepared document was not created by FastReport Open Source runtime.", nameof(prepared));

        // Database FRX remains the source of truth. This first free-runtime smoke deliberately preserves template
        // watermarks; an application overlay is added only after the actual production watermark source is evidenced.
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
        using var exporter = new PDFSimpleExport();
        document.Report.Export(exporter, stream);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new FastReportPdfOutput(stream.ToArray(), document.Report.PreparedPages.Count));
    }

    private sealed class OpenSourceFastReportPreparedDocument(FastReportReport report) : IFastReportPreparedDocument
    {
        private int _disposed;

        public FastReportReport Report { get; } = report;

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                Report.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
