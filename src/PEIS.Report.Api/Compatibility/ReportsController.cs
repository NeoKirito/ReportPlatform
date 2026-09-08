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
    LegacyReportRequestAdapter adapter,
    ILogger<ReportsController>? logger = null) : ControllerBase
{
    [HttpGet]
    public IActionResult Test() => Ok("OK");

    [HttpGet("/TJ/exportTemplate/exportPdf")]
    [HttpGet("/BaseInfo/Report/GetReportByJson")]
    [HttpPost]
    [HttpPost("/BaseInfo/Report/GetReportByJson")]
    [HttpPost("/TJ/exportTemplate/exportPdf")]
    public async Task<IActionResult> GetReportByJson(
        [FromBody(EmptyBodyBehavior = Microsoft.AspNetCore.Mvc.ModelBinding.EmptyBodyBehavior.Allow)] JsonElement? data,
        CancellationToken cancellationToken)
    {
        try
        {
            JsonElement payload;
            if (data.HasValue && data.Value.ValueKind == JsonValueKind.Object)
            {
                payload = data.Value;
            }
            else
            {
                var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var query in Request.Query)
                {
                    dict[query.Key] = query.Value.ToString();
                }
                var jsonBytes = JsonSerializer.SerializeToUtf8Bytes(dict);
                using var doc = JsonDocument.Parse(jsonBytes);
                payload = doc.RootElement.Clone();
            }

            var request = adapter.Adapt(payload);
            var result = await renderer.RenderPdfAsync(request, cancellationToken);

            if (result.UnavailableImageCount > 0)
                Response.Headers.Append("X-ReportPlatform-Unavailable-Images", result.UnavailableImageCount.ToString(System.Globalization.CultureInfo.InvariantCulture));

            // Keep the compatibility surface as a direct PDF response/stream. Do not wrap the
            // response in the new API's JSON envelope.
            return File(result.Pdf, "application/pdf", result.FileName, enableRangeProcessing: false);
        }
        catch (LegacyReportDatabaseException ex)
        {
            logger?.LogError(ex, "Legacy report generation failed: [{Code}] {Message}", ex.Code, ex.Message);
            return StatusCode(ex.Code switch
            {
                LegacyReportDatabaseErrorCode.ReportNotFound => StatusCodes.Status404NotFound,
                LegacyReportDatabaseErrorCode.TemplateNotFound => StatusCodes.Status404NotFound,
                LegacyReportDatabaseErrorCode.ParameterBindFailed => StatusCodes.Status400BadRequest,
                _ => StatusCodes.Status500InternalServerError
            }, new { error = ex.Message, code = ex.Code.ToString() });
        }
        catch (Exception ex)
        {
            logger?.LogError(ex, "Unexpected error generating report: {Message}", ex.Message);
            return StatusCode(StatusCodes.Status500InternalServerError, new { error = ex.Message });
        }
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
