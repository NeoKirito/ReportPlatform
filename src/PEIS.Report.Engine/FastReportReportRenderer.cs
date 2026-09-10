using PEIS.Report.Contracts;

namespace PEIS.Report.Engine;

/// <summary>
/// FastReport运行时接口 - 抽象层。
/// 定义报表渲染的核心操作：Prepare、ApplyWatermark、ExportPdf。
/// 控制器和PrintAgent不直接引用FastReport类型，通过此接口解耦。
/// 具体实现由 FastReport.OpenSource 项目提供。
/// </summary>
public interface IFastReportRuntime
{
    /// <summary>
    /// 准备报表：加载FRX模板、注册数据、执行渲染。
    /// 返回包含已准备文档和各阶段耗时的对象。
    /// </summary>
    Task<FastReportRuntimePreparation> PrepareAsync(FastReportRenderContext context, CancellationToken cancellationToken);

    /// <summary>
    /// 应用水印：在已准备的文档上添加半透明文字水印。
    /// </summary>
    Task ApplyWatermarkAsync(IFastReportPreparedDocument prepared, WatermarkOptions watermark, CancellationToken cancellationToken);

    /// <summary>
    /// 导出PDF：将已准备的文档导出为PDF格式。
    /// </summary>
    Task<FastReportPdfOutput> ExportPdfAsync(IFastReportPreparedDocument prepared, PdfExportProfile profile, CancellationToken cancellationToken);
}

/// <summary>
/// 已准备的报表文档接口 - 异步可释放。
/// 封装FastReport的Report对象，确保资源正确释放。
/// </summary>
public interface IFastReportPreparedDocument : IAsyncDisposable
{
}

/// <summary>
/// Prepare操作的结果：包含已准备文档和各阶段耗时信息。
/// </summary>
public sealed record FastReportRuntimePreparation(
    IFastReportPreparedDocument Document,
    IReadOnlyList<ReportStageTiming> Timings)
{
    /// <summary>图片解析批次信息（可选），包含图片加载统计。</summary>
    public ImageResolveBatch? Images { get; init; }
}

/// <summary>
/// 渲染上下文：传递给FastReport运行时的所有必要信息。
/// </summary>
public sealed record FastReportRenderContext(
    ReportRenderRequest Request,      // 原始渲染请求
    ReportDefinition Definition,      // 报表定义（SQL、模板元数据）
    ReportTemplate Template,          // 解码后的FRX模板内容
    ReportDataSet Data,               // SQL查询结果（DataTable集合）
    PdfExportProfile Profile);        // PDF导出配置

/// <summary>
/// PDF导出结果：包含PDF字节数组和页数。
/// </summary>
public sealed record FastReportPdfOutput(byte[] Pdf, int PageCount);

/// <summary>
/// FastReport运行时不可用异常 - 当未配置FastReport时抛出。
/// </summary>
public sealed class FastReportIntegrationUnavailableException(string message) : InvalidOperationException(message);

/// <summary>
/// 生产级报表渲染管道 - 核心编排器。
/// 
/// 职责：
/// 1. 加载报表定义（从数据库或缓存）
/// 2. 解码FRX模板
/// 3. 执行SQL查询获取数据
/// 4. 调用FastReport运行时进行渲染
/// 5. 应用水印
/// 6. 导出PDF
/// 7. 记录性能指标
/// 
/// 设计原则：
/// - FastReport实例通过依赖注入传入，渲染器本身不依赖FastReport
/// - 每个请求创建独立的、可变的Report实例
/// - 通过RenderConcurrencyGate控制并发数，防止内存溢出
/// </summary>
public sealed class FastReportReportRenderer(
    ReportDefinitionCache definitionCache,
    IReportDefinitionProvider definitions,
    ITemplateProvider templates,
    IReportDataProvider data,
    RenderConcurrencyGate renderGate,
    IFastReportRuntime runtime,
    IWatermarkTextProvider watermarkTextProvider,
    IReportRenderTelemetry telemetry) : IReportRenderer
{
    /// <summary>
    /// 渲染PDF报表的主入口。
    /// 
    /// 流程：
    /// 1. 参数校验
    /// 2. 加载报表定义（带缓存）
    /// 3. 解码FRX模板
    /// 4. 执行SQL查询
    /// 5. 获取水印文本
    /// 6. 进入并发控制门
    /// 7. 调用FastReport Prepare
    /// 8. 应用水印
    /// 9. 导出PDF
    /// 10. 校验页数
    /// 11. 记录指标
    /// </summary>
    public async Task<ReportRenderResult> RenderPdfAsync(ReportRenderRequest request, CancellationToken cancellationToken)
    {
        // 参数校验
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ReportId);
        var metrics = new ReportRenderMetrics
        {
            RequestId = Guid.NewGuid().ToString("N"),
            ReportId = request.ReportId,
            Profile = PdfExportProfile.Normalize(request.Profile)
        };

        // 加载报表定义（带缓存）
        // 如果配置了版本提供者，使用版本号作为缓存键；否则使用报表ID
        var cacheKey = request.ReportId;
        if (definitions is IReportDefinitionVersionProvider versionProvider)
        {
            var version = await metrics.MeasureAsync("DefinitionVersionCheck", () => versionProvider.GetVersionAsync(request, cancellationToken));
            cacheKey = ReportDefinitionCache.BuildCacheKey(request.ReportId, version);
        }
        var beforeCache = definitionCache.Snapshot();
        var definition = await metrics.MeasureAsync("DefinitionLoad", () =>
            definitionCache.GetOrCreateAsync(cacheKey, token => definitions.GetRequiredAsync(request, token), cancellationToken));
        metrics.DefinitionCacheHit = definitionCache.Snapshot().Hits > beforeCache.Hits;

        // 解码FRX模板（Base64解码或原始XML）
        var template = await metrics.MeasureAsync("TemplateDecode", () => templates.GetRequiredAsync(definition, cancellationToken));

        // 执行SQL查询，获取DataTable集合
        var reportData = await metrics.MeasureAsync("SqlQuery", () => data.QueryAsync(definition, request, cancellationToken));
        metrics.Rows = reportData.RowCount;
        metrics.SqlResultSets = reportData.Tables.Count;

        // 水印处理：
        // - 水印文本来自数据库（机构名称），忽略调用方提供的文本
        // - 调用方只能启用/禁用水印，不能冒充其他机构
        var watermark = request.Watermark ?? new WatermarkOptions();
        if (watermark.Enabled)
        {
            var text = await metrics.MeasureAsync("WatermarkText", () => watermarkTextProvider.GetWatermarkTextAsync(cancellationToken));
            watermark = watermark with { Text = text };
        }

        FastReportPdfOutput output;
        // 进入并发控制门，限制同时渲染的报表数量
        using (await renderGate.EnterAsync(cancellationToken))
        {
            var profile = PdfExportProfile.Resolve(request.Profile);
            var context = new FastReportRenderContext(request, definition, template, reportData, profile);

            // 调用FastReport运行时进行渲染
            var preparation = await runtime.PrepareAsync(context, cancellationToken);

            // 收集图片解析指标
            if (preparation.Images is { } images)
            {
                metrics.ImageCount = images.Images.Count + images.FailureCount;
                metrics.ImageBytes = images.TotalBytes;
                metrics.ImageCacheHits = images.CacheHits;
                metrics.ImageFailures = images.FailureCount;
            }

            // 记录各阶段耗时
            foreach (var timing in preparation.Timings)
                metrics.Record(timing.Stage, timing.ElapsedMilliseconds);

            // 应用水印并导出PDF
            await using var prepared = preparation.Document;
            await metrics.MeasureAsync("Watermark", () => runtime.ApplyWatermarkAsync(prepared, watermark, cancellationToken));
            output = await metrics.MeasureAsync("PdfExport", () => runtime.ExportPdfAsync(prepared, profile, cancellationToken));
        }

        // 校验：如果生成页数为0，说明没有有效数据
        if (output.PageCount <= 0)
        {
            throw new LegacyReportDatabaseException(
                LegacyReportDatabaseErrorCode.ReportNotFound,
                $"报表 '{request.FileName ?? definition.ReportId}' 未查询到有效体检数据（生成页数为0）。请核对体检人/单位标识参数及对应数据库记录。");
        }

        // 记录最终指标
        metrics.Pages = output.PageCount;
        metrics.PdfBytes = output.Pdf.LongLength;
        await metrics.MeasureAsync("ArtifactWrite", () => Task.CompletedTask);
        var observation = metrics.Complete();
        telemetry.Record(observation);
        return new ReportRenderResult(output.Pdf, PdfExportProfile.FileName(request.FileName, request.ReportId), output.PageCount, observation.Timings)
        {
            UnavailableImageCount = metrics.ImageFailures
        };
    }
}

/// <summary>
/// 缺失FastReport运行时的占位实现。
/// 当未配置FastReport时使用，所有操作都会抛出异常。
/// 用于明确提示需要安装FastReport运行时包。
/// </summary>
public sealed class MissingFastReportRuntime : IFastReportRuntime
{
    private const string Message = "FastReport rendering is blocked until an approved, .NET-compatible FastReport runtime package is configured.";

    public Task<FastReportRuntimePreparation> PrepareAsync(FastReportRenderContext context, CancellationToken cancellationToken)
        => Task.FromException<FastReportRuntimePreparation>(new FastReportIntegrationUnavailableException(Message));

    public Task ApplyWatermarkAsync(IFastReportPreparedDocument prepared, WatermarkOptions watermark, CancellationToken cancellationToken)
        => Task.FromException(new FastReportIntegrationUnavailableException(Message));

    public Task<FastReportPdfOutput> ExportPdfAsync(IFastReportPreparedDocument prepared, PdfExportProfile profile, CancellationToken cancellationToken)
        => Task.FromException<FastReportPdfOutput>(new FastReportIntegrationUnavailableException(Message));
}
