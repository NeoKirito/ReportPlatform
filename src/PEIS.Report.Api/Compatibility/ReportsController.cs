using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using PEIS.Report.Docx.OpenXml;
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
    IFrxDocxReportExporter docxExporter,
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

    /// <summary>
    /// Additive Word-export endpoint. It accepts exactly the same legacy JSON as the PDF endpoint but renders the
    /// current database FRX plus data through the free Open XML pipeline.
    /// </summary>
    [HttpPost]
    public async Task<IActionResult> GetReportDocxByJson(
        [FromBody] JsonElement data,
        CancellationToken cancellationToken)
    {
        var request = adapter.Adapt(data);
        var result = await docxExporter.ExportAsync(request, cancellationToken);
        Response.Headers.Append("X-ReportPlatform-Docx-Unsupported-Objects", result.UnsupportedObjects.Count.ToString(System.Globalization.CultureInfo.InvariantCulture));
        return File(
            result.Docx,
            "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
            result.FileName,
            enableRangeProcessing: false);
    }
}
