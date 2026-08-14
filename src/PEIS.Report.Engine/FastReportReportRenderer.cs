using PEIS.Report.Contracts;

namespace PEIS.Report.Engine;

/// <summary>
/// Production renderer seam.
/// Wire the licensed FastReport .NET reference here instead of coupling FastReport
/// directly to controllers or the print agent.
///
/// Planned pipeline:
/// 1. Load immutable report definition from cache (FRX + SQL + parameter metadata).
/// 2. Query data with thin ADO.NET/Dapper provider.
/// 3. Resolve report images concurrently with bounded parallelism and deduplication.
/// 4. Create a fresh FastReport Report instance per request.
/// 5. Register data, apply watermark before export, Prepare once.
/// 6. Export PDF once with profile-based image/JPEG settings.
/// 7. Emit stage timings and return bytes/file stream.
/// </summary>
public sealed class FastReportReportRenderer : IReportRenderer
{
    public Task<ReportRenderResult> RenderPdfAsync(ReportRenderRequest request, CancellationToken cancellationToken)
        => throw new NotImplementedException(
            "FastReport adapter intentionally left behind an interface until the licensed FastReport reference, newest watermark build, FRX schema and data-source rules are supplied.");
}
