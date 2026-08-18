using System.Diagnostics;
using System.Drawing;
using FastReport;
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

        if (prepared is not OpenSourceFastReportPreparedDocument document)
            throw new ArgumentException("Prepared document was not created by FastReport Open Source runtime.", nameof(prepared));
        if (!watermark.Enabled || string.IsNullOrWhiteSpace(watermark.Text))
            return Task.CompletedTask;

        var text = watermark.Text.Trim();
        var alpha = (int)Math.Round(Math.Clamp(watermark.Opacity, 0d, 1d) * byte.MaxValue, MidpointRounding.AwayFromZero);
        var rotation = watermark.Angle < 0 ? WatermarkTextRotation.ForwardDiagonal : WatermarkTextRotation.BackwardDiagonal;
        for (var index = 0; index < document.Report.PreparedPages.Count; index++)
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
