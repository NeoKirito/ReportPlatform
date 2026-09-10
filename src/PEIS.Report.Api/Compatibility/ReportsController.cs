using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using PEIS.Report.Docx.OpenXml;
using PEIS.Report.Engine;

namespace PEIS.Report.Api.Compatibility;

/// <summary>
/// 兼容旧版IIS报表服务的HTTP接口。
/// 保持与原有PEIS Java系统完全相同的URL、HTTP方法和JSON请求体格式，
/// 确保现有调用方无需修改任何代码即可无缝迁移到新平台。
/// </summary>
[ApiController]
[Route("api/[controller]/[action]")]
public sealed class ReportsController(
    IReportRenderer renderer,
    IFrxDocxReportExporter docxExporter,
    LegacyReportRequestAdapter adapter,
    ILogger<ReportsController>? logger = null) : ControllerBase
{
    /// <summary>
    /// 健康检查端点，用于验证API是否正常运行。
    /// </summary>
    [HttpGet]
    public IActionResult Test() => Ok("OK");

    /// <summary>
    /// 核心报表生成端点 - 与旧版IIS报表服务完全兼容。
    /// 支持多种路由格式以适配不同版本的Java调用方：
    /// - /BaseInfo/Report/GetReportByJson（主要路由）
    /// - /TJ/exportTemplate/exportPdf（备用路由）
    /// - /jmreport/exportPdfStream（报表导出路由）
    /// 
    /// 支持GET和POST两种HTTP方法，请求体为JSON格式的报表参数。
    /// 返回直接的PDF文件流（application/pdf），而非JSON包装格式。
    /// </summary>
    [HttpGet("/TJ/exportTemplate/exportPdf")]
    [HttpGet("/BaseInfo/Report/GetReportByJson")]
    [HttpGet("/jmreport/exportPdfStream")]
    [HttpPost]
    [HttpPost("/BaseInfo/Report/GetReportByJson")]
    [HttpPost("/TJ/exportTemplate/exportPdf")]
    [HttpPost("/jmreport/exportPdfStream")]
    public async Task<IActionResult> GetReportByJson(
        [FromBody(EmptyBodyBehavior = Microsoft.AspNetCore.Mvc.ModelBinding.EmptyBodyBehavior.Allow)] JsonElement? data,
        CancellationToken cancellationToken)
    {
        try
        {
            JsonElement payload;
            if (data.HasValue && data.Value.ValueKind == JsonValueKind.Object)
            {
                // POST请求：直接使用JSON请求体
                payload = data.Value;
            }
            else
            {
                // GET请求：将查询字符串参数转换为JSON对象
                // 例如：?djh=123&name=test 转换为 {"djh":"123","name":"test"}
                var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var query in Request.Query)
                {
                    dict[query.Key] = query.Value.ToString();
                }
                var jsonBytes = JsonSerializer.SerializeToUtf8Bytes(dict);
                using var doc = JsonDocument.Parse(jsonBytes);
                payload = doc.RootElement.Clone();
            }

            // 将旧版JSON格式适配为新的报表渲染请求
            var request = adapter.Adapt(payload);
            // 执行报表渲染，返回PDF字节流
            var result = await renderer.RenderPdfAsync(request, cancellationToken);

            // 如果有图片加载失败，在响应头中返回失败数量（用于监控）
            if (result.UnavailableImageCount > 0)
                Response.Headers.Append("X-ReportPlatform-Unavailable-Images", result.UnavailableImageCount.ToString(System.Globalization.CultureInfo.InvariantCulture));

            // 保持兼容性：直接返回PDF流，不包装为JSON格式
            return File(result.Pdf, "application/pdf", result.FileName, enableRangeProcessing: false);
        }
        catch (LegacyReportDatabaseException ex)
        {
            // 数据库相关错误：报表未找到、模板缺失、参数绑定失败等
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
            // 未预期的异常：记录完整错误信息，返回500状态码
            var root = ex.GetBaseException() ?? ex;
            var errorMsg = root.Message;
            if (root != ex && !string.IsNullOrWhiteSpace(ex.Message))
            {
                errorMsg = $"{ex.Message} -> {root.Message}";
            }
            logger?.LogError(ex, "Unexpected error generating report: {Message}", errorMsg);
            return StatusCode(StatusCodes.Status500InternalServerError, new { error = errorMsg, details = ex.ToString() });
        }
    }

    /// <summary>
    /// Word文档导出端点 - 新增功能。
    /// 接收与PDF端点完全相同的旧版JSON格式，但通过Open XML管道渲染Word文档。
    /// 使用数据库中的FRX模板和数据，通过免费的Open XML管道生成DOCX文件。
    /// </summary>
    [HttpPost]
    public async Task<IActionResult> GetReportDocxByJson(
        [FromBody] JsonElement data,
        CancellationToken cancellationToken)
    {
        var request = adapter.Adapt(data);
        var result = await docxExporter.ExportAsync(request, cancellationToken);
        // 在响应头中返回不支持的对象数量（用于监控）
        Response.Headers.Append("X-ReportPlatform-Docx-Unsupported-Objects", result.UnsupportedObjects.Count.ToString(System.Globalization.CultureInfo.InvariantCulture));
        return File(
            result.Docx,
            "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
            result.FileName,
            enableRangeProcessing: false);
    }
}
