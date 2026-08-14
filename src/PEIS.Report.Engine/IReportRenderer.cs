using PEIS.Report.Contracts;

namespace PEIS.Report.Engine;

public interface IReportRenderer
{
    Task<ReportRenderResult> RenderPdfAsync(ReportRenderRequest request, CancellationToken cancellationToken);
}

public sealed record ReportStageTiming(string Stage, long ElapsedMilliseconds);

public sealed record ReportRenderResult(
    byte[] Pdf,
    string FileName,
    int PageCount,
    IReadOnlyList<ReportStageTiming> Timings);
