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
/// FastReport Open Source 运行时实现（MIT许可证）。
/// 
/// 核心职责：
/// 1. 加载FRX模板字符串到FastReport.Report对象
/// 2. 注册DataTable数据源
/// 3. 执行report.Prepare()生成预览页面
/// 4. 应用水印（修改PreparedPages）
/// 5. 导出PDF
/// 
/// 设计原则：
/// - 每个请求创建独立的Report实例，用完即释放
/// - Report实例只通过IFastReportPreparedDocument句柄保留
/// - 通过LegacyFrxCompatibility处理旧版FRX兼容性
/// - 通过ReportImagePreparation预取HTTP图片
/// </summary>
public sealed class OpenSourceFastReportRuntime(IImageResolver? imageResolver = null) : IFastReportRuntime
{
    /// <summary>默认图片解析器：150ms超时，失败缓存1小时</summary>
    private static readonly IImageResolver _defaultResolver = new ImageResolver(
        new HttpClient(),
        new ImageResolutionOptions { TimeoutMilliseconds = 150, FailureCacheSeconds = 3600 });

    /// <summary>预热FastReport编译器，减少首次渲染延迟</summary>
    public static void WarmupCompiler() => Config.CompilerWarmup();

    /// <summary>
    /// 根据PDF配置文件确定图片DPI：
    /// - 标签/打印/归档模式：300 DPI
    /// - 屏幕模式：150 DPI
    /// - 默认：200 DPI
    /// </summary>
    internal static int ResolveImageDpi(PdfExportProfile profile)
        => profile.IsLabel || profile.IntendedForPrint || string.Equals(profile.Name, "archive", StringComparison.OrdinalIgnoreCase)
            ? 300
            : string.Equals(profile.Name, "screen", StringComparison.OrdinalIgnoreCase) ? 150 : 200;

    /// <summary>
    /// 准备报表 - 核心流程：
    /// 1. FRX模板标准化（处理RichObject、DoublePass等兼容性问题）
    /// 2. 预取HTTP图片并替换为本地路径
    /// 3. 加载FRX字符串到Report对象
    /// 4. 注册DataTable数据源
    /// 5. 应用报表参数
    /// 6. 抑制尾部空页
    /// 7. 执行report.Prepare()
    /// </summary>
    public async Task<FastReportRuntimePreparation> PrepareAsync(
        FastReportRenderContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();

        var report = new FastReportReport();
        try
        {
            // 步骤1：FRX模板标准化
            // - 处理RichObject（旧版RTF格式，转换为TextObject）
            // - 处理不必要的DoublePass（如果不需要TotalPages则关闭）
            // - 缓存标准化结果（按SHA256哈希）
            // - 计算哪些页面的数据源为空（需要抑制）
            var (normalizedTemplate, emptyDataPageNames) = LegacyFrxCompatibility.GetNormalizedWithEmptyPages(
                context.Template.Content,
                context.Data.Tables);

            // 步骤2：预取HTTP图片
            // - 扫描FRX中的<img>标签，提取URL
            // - 并行下载图片（150ms超时，LRU缓存）
            // - 将URL替换为本地文件路径
            var effectiveResolver = imageResolver ?? _defaultResolver;
            var images = await ReportImagePreparation.PrepareAsync(normalizedTemplate, context.Data.Tables, effectiveResolver, cancellationToken).ConfigureAwait(false);

            // 步骤3：加载FRX字符串到Report对象
            var frxLoad = Stopwatch.StartNew();
            report.LoadFromString(images.Template);
            frxLoad.Stop();

            // 步骤4：注册DataTable数据源
            // - 匹配FRX中声明的数据源名称与实际DataTable
            // - 处理列缺失、名称不匹配等问题
            var registerData = Stopwatch.StartNew();
            EnsureDataSourcesAndSchemas(report, context.Data.Tables);
            ApplyParameters(report, context.Request);
            registerData.Stop();

            cancellationToken.ThrowIfCancellationRequested();

            // 步骤5：抑制尾部空页 + 执行Prepare
            var prepare = Stopwatch.StartNew();
            SuppressTrailingEmptyPages(report, emptyDataPageNames);
            report.Prepare();  // 最耗时的操作：渲染所有页面
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
            // 出错时释放Report对象
            report.Dispose();
            throw;
        }
    }

    /// <summary>
    /// 抑制尾部空页：
    /// 如果报表有多个页面，且尾部页面的数据源全部为空，
    /// 则将这些页面标记为不可见（在Prepare之前设置，确保TotalPages正确）。
    /// 
    /// 例如：体检报告可能有"单位信息"页面，如果个人体检则该页面为空，需要隐藏。
    /// </summary>
    private static void SuppressTrailingEmptyPages(
        FastReportReport report,
        IReadOnlySet<string> emptyDataPageNames)
    {
        if (emptyDataPageNames.Count == 0 || report.Pages.Count <= 1)
            return;

        // 从尾部向前遍历，遇到非空页面就停止
        for (var index = report.Pages.Count - 1; index > 0; index--)
        {
            if (report.Pages[index] is not ReportPage page || !emptyDataPageNames.Contains(page.Name))
                break;
            // 在Prepare之前设置Visible=false，确保TotalPages与导出PDF一致
            page.Visible = false;
        }
    }

    /// <summary>
    /// 应用水印：遍历所有已准备页面，添加半透明文字水印。
    /// 
    /// 水印参数：
    /// - 文字：来自数据库的机构名称
    /// - 字体：Arial 54pt Bold
    /// - 颜色：半透明灰色
    /// - 旋转：正对角线或反对角线
    /// - 位置：顶部显示
    /// </summary>
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

        // 遍历所有已导出的页面
        for (var index = 0; index < document.ExportedPageCount; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var page = document.Report.PreparedPages.GetPage(index);
            if (page is null)
                continue;

            // 修改页面水印属性
            // 注意：必须修改PreparedPages中的页面，而不是原始FRX页面
            // 否则水印不会出现在导出的PDF中
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

    /// <summary>
    /// 导出PDF：使用PDFSimple导出器将已准备的文档导出为PDF。
    /// 
    /// 处理逻辑：
    /// - 根据配置文件设置图片DPI和JPEG质量
    /// - 如果导出页数小于总页数，设置页码范围（抑制尾部空页后）
    /// - 将Report导出到MemoryStream，然后转为byte[]
    /// </summary>
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

        // 如果有抑制的空页，设置页码范围
        if (document.ExportedPageCount < document.Report.PreparedPages.Count)
        {
            exporter.PageRange = PageRange.PageNumbers;
            exporter.PageNumbers = $"1-{document.ExportedPageCount}";
        }

        document.Report.Export(exporter, stream);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new FastReportPdfOutput(stream.ToArray(), document.ExportedPageCount));
    }

    /// <summary>
    /// 应用报表参数：将请求中的参数设置到FastReport.Report对象。
    /// 
    /// 参数来源：
    /// 1. request.Parameters - 显式参数字典
    /// 2. request.LegacyPayload - 旧版JSON对象（递归展开）
    /// 
    /// 参数传递给FastReport后，FRX模板中的表达式可以引用这些值。
    /// </summary>
    private static void ApplyParameters(FastReportReport report, ReportRenderRequest request)
    {
        var parameters = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

        // 添加显式参数
        foreach (var (key, element) in request.Parameters)
            parameters[key] = JsonScalarToObject(element);

        // 递归展开旧版JSON对象中的参数
        if (request.LegacyPayload is { ValueKind: System.Text.Json.JsonValueKind.Object } payload)
            ExtractPayloadParameters(payload, parameters);

        // 设置到FastReport（忽略模板中未定义的参数）
        foreach (var (key, value) in parameters)
        {
            try
            {
                report.SetParameterValue(key, value);
            }
            catch
            {
                // 模板可能未定义此参数，忽略
            }
        }
    }

    /// <summary>
    /// 从JSON对象中递归提取参数：
    /// - 对象类型：递归展开
    /// - 标量类型（字符串、数字、布尔、null）：添加到参数字典
    /// - 数组类型：取第一个元素
    /// </summary>
    private static void ExtractPayloadParameters(System.Text.Json.JsonElement element, Dictionary<string, object?> parameters)
    {
        if (element.ValueKind != System.Text.Json.JsonValueKind.Object) return;
        foreach (var prop in element.EnumerateObject())
        {
            if (prop.Value.ValueKind == System.Text.Json.JsonValueKind.Object)
            {
                // 递归展开嵌套对象
                ExtractPayloadParameters(prop.Value, parameters);
            }
            else if (!parameters.ContainsKey(prop.Name))
            {
                // 只添加尚未存在的参数（避免覆盖显式参数）
                parameters[prop.Name] = JsonScalarToObject(prop.Value);
            }
        }
    }

    /// <summary>
    /// JSON元素转C#对象：
    /// - String → string
    /// - Number(Int64) → long
    /// - Number(Decimal) → decimal
    /// - True/False → bool
    /// - Null → null
    /// - 其他 → null
    /// </summary>
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

    /// <summary>
    /// 确保数据源和Schema匹配：
    /// 
    /// FastReport FRX模板中声明了数据源（TableDataSource），需要与实际的DataTable匹配。
    /// 匹配策略：
    /// 1. 按ReferenceName/Name/Alias直接匹配
    /// 2. 按列名重叠度智能匹配（选择重叠最多的表）
    /// 3. 如果没有匹配，创建空表（避免编译错误）
    /// 4. 确保所有声明的列都存在（缺失的列添加为空字符串列）
    /// 
    /// 这个方法处理了旧版报表模板Schema不匹配的问题。
    /// </summary>
    private static void EnsureDataSourcesAndSchemas(
        FastReportReport report,
        IReadOnlyDictionary<string, DataTable> tables)
    {
        var registeredNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (Base b in report.Dictionary.DataSources)
        {
            if (b is not TableDataSource tds)
                continue;

            var refName = tds.ReferenceName ?? tds.Name;
            var declaredCols = tds.Columns.Cast<Column>().Select(c => c.Name).ToList();
            DataTable? table = null;

            // 策略1：按ReferenceName/Name/Alias直接匹配
            if (tables.TryGetValue(refName, out var candidate) ||
                tables.TryGetValue(tds.Name, out candidate) ||
                (!string.IsNullOrWhiteSpace(tds.Alias) && tables.TryGetValue(tds.Alias, out candidate)))
            {
                // 验证列兼容性：如果模板声明了列，至少要有一列匹配
                if (declaredCols.Count == 0 || declaredCols.Any(c => candidate.Columns.Contains(c)))
                {
                    table = candidate;
                }
            }

            // 策略2：智能列重叠匹配
            // 遍历所有可用表，选择列重叠最多的表
            if (table is null && declaredCols.Count > 0)
            {
                var bestOverlap = 0;
                DataTable? bestTable = null;
                foreach (var cand in tables.Values.Distinct())
                {
                    var overlap = declaredCols.Count(c => cand.Columns.Contains(c));
                    if (overlap > bestOverlap)
                    {
                        bestOverlap = overlap;
                        bestTable = cand;
                    }
                }
                if (bestTable is not null && bestOverlap > 0)
                {
                    table = bestTable;
                }
            }

            if (table is null)
            {
                // 策略3：没有匹配的表，创建空表（避免FastReport编译错误）
                table = new DataTable(refName);
                foreach (Column col in tds.Columns)
                {
                    table.Columns.Add(col.Name, col.DataType ?? typeof(string));
                }
            }
            else
            {
                // 策略4：确保所有声明的列都存在（缺失的列添加为空字符串列）
                foreach (var colName in declaredCols)
                {
                    if (!table.Columns.Contains(colName))
                    {
                        table.Columns.Add(colName, typeof(string));
                    }
                }
            }

            // 注册数据源到FastReport
            report.RegisterData(table, tds.Name);
            registeredNames.Add(tds.Name);
            if (!string.IsNullOrEmpty(tds.ReferenceName) && !tds.ReferenceName.Equals(tds.Name, StringComparison.OrdinalIgnoreCase))
            {
                report.RegisterData(table, tds.ReferenceName);
                registeredNames.Add(tds.ReferenceName);
            }
            tds.Table = table;
            tds.Enabled = true;
        }

        // 注册未在模板中声明的额外数据源
        // 例如：SQL查询返回了多个结果集，但FRX只声明了部分
        foreach (var (key, tbl) in tables)
        {
            if (!registeredNames.Contains(key))
            {
                report.RegisterData(tbl, key);
                var source = report.GetDataSource(key);
                if (source is not null)
                    source.Enabled = true;
            }
        }
    }

    /// <summary>
    /// 已准备的FastReport文档封装。
    /// 持有Report对象引用和导出的页数。
    /// 实现异步释放，确保资源正确清理。
    /// </summary>
    private sealed class OpenSourceFastReportPreparedDocument(
        FastReportReport report,
        int exportedPageCount) : IFastReportPreparedDocument
    {
        private int _disposed;

        public FastReportReport Report { get; } = report;
        public int ExportedPageCount { get; } = exportedPageCount;

        /// <summary>
        /// 异步释放：清理PreparedPages、Dictionary，然后释放Report。
        /// 使用Interlocked确保只释放一次。
        /// </summary>
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
                    // 忽略非致命清理错误
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
