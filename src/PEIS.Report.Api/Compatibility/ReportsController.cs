using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using PEIS.Report.Engine;

namespace PEIS.Report.Api.Compatibility;

/// <summary>
/// Drop-in HTTP compatibility surface for the legacy IIS report service.
/// Existing PEIS callers can keep the old URL, HTTP method and JSON body.
/// </summary>
[ApiController]
[Route("api/[controller]/[action]")]
public sealed class ReportsController(
    IReportRenderer renderer,
    LegacyReportRequestAdapter adapter) : ControllerBase
{
    [HttpGet]
    public IActionResult Test() => Ok("OK");

    [HttpPost]
    public async Task<IActionResult> GetReportByJson(
        [FromBody] JsonElement data,
        CancellationToken cancellationToken)
    {
        var request = adapter.Adapt(data);
        var result = await renderer.RenderPdfAsync(request, cancellationToken);

        // Keep the compatibility surface as a direct PDF response/stream. Do not wrap the
        // response in the new API's JSON envelope.
        return File(result.Pdf, "application/pdf", result.FileName, enableRangeProcessing: false);
    }
}
