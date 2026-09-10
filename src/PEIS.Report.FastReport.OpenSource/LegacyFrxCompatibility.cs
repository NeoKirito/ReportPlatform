using System.Collections.Concurrent;
using System.Data;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace PEIS.Report.FastReport.OpenSource;

/// <summary>
/// 旧版FRX模板兼容性转换器。
/// 
/// 背景：
/// PEIS系统的报表模板（FRX）存储在SQL Server数据库中，使用旧版FastReport（商业版）创建。
/// 迁移到FastReport Open Source（MIT许可证）后，某些对象类型不兼容，需要转换。
/// 
/// 主要兼容性问题：
/// 1. RichObject → 转换为TextObject（Open Source不支持RichObject）
/// 2. DoublePass="true" → 如果不需要TotalPages则关闭（性能优化）
/// 3. RTF格式文本 → 提取纯文本或FastReport表达式
/// 
/// 缓存机制：
/// - 按模板内容的SHA256哈希缓存标准化结果
/// - 避免重复解析相同模板的XML
/// - LRU策略，最多缓存128个模板
/// </summary>
internal static partial class LegacyFrxCompatibility
{
    /// <summary>页面数据源信息：页面名称及其引用的数据源列表</summary>
    internal sealed record PageDataSourceInfo(string PageName, string[] DataSources);

    /// <summary>模板元数据：标准化后的模板内容和页面信息</summary>
    internal sealed record TemplateMetadata(string NormalizedTemplate, IReadOnlyList<PageDataSourceInfo> Pages);

    /// <summary>标准化结果缓存（按SHA256哈希）</summary>
    private static readonly ConcurrentDictionary<string, TemplateMetadata> _cache = new(StringComparer.Ordinal);
    private const int MaxCacheSize = 128;

    /// <summary>
    /// 标准化FRX模板内容：
    /// 1. 处理RichObject → TextObject
    /// 2. 处理不必要的DoublePass
    /// 3. 缓存结果
    /// </summary>
    public static string Normalize(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
            return content;

        // 检查是否需要处理
        var containsRichObject = content.Contains("<RichObject", StringComparison.Ordinal);
        var mayHaveUnusedDoublePass = content.Contains("DoublePass=\"true\"", StringComparison.OrdinalIgnoreCase)
            && !RequiresTotalPagePass(content);
        if (!containsRichObject && !mayHaveUnusedDoublePass)
            return content;

        // 去除BOM和零宽字符
        var normalizedContent = content.TrimStart('\uFEFF', '\u0000', '\u200B');
        XDocument document;
        try
        {
            document = XDocument.Parse(normalizedContent, LoadOptions.PreserveWhitespace);
        }
        catch (System.Xml.XmlException)
        {
            return content;  // XML解析失败，返回原始内容
        }
        var changed = false;

        // 处理RichObject → TextObject
        if (containsRichObject)
        {
            foreach (var richObject in document.Descendants().Where(element => element.Name.LocalName == "RichObject").ToArray())
            {
                var textAttribute = richObject.Attribute("Text");
                if (textAttribute is not null)
                {
                    var rtf = textAttribute.Value;
                    textAttribute.Value = ConvertRtfText(rtf);
                    ApplyRtfFontFallback(richObject, rtf);
                }
                richObject.Name = richObject.Name.Namespace + "TextObject";
                changed = true;
            }
        }

        // 处理不必要的DoublePass
        var root = document.Root;
        var doublePass = root?.Attribute("DoublePass");
        if (mayHaveUnusedDoublePass && doublePass is not null
            && string.Equals(doublePass.Value, "true", StringComparison.OrdinalIgnoreCase))
        {
            doublePass.Value = "false";
            changed = true;
        }

        if (!changed)
            return content;

        using var writer = new Utf8StringWriter();
        document.Save(writer, SaveOptions.DisableFormatting);
        return writer.ToString();
    }

    /// <summary>
    /// 检查模板是否需要DoublePass（双遍渲染）：
    /// 如果包含TotalPages或PageNofM表达式，则需要DoublePass。
    /// </summary>
    private static bool RequiresTotalPagePass(string content)
        => content.Contains("TotalPages", StringComparison.OrdinalIgnoreCase)
            || content.Contains("PageNofM", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 获取标准化模板和空页名称（带缓存）。
    /// 
    /// 流程：
    /// 1. 计算模板内容的SHA256哈希
    /// 2. 检查缓存：命中则直接返回
    /// 3. 缓存未命中：执行标准化 + 提取页面数据源
    /// 4. 计算空页：数据源全部为空的页面
    /// 5. 缓存结果
    /// 
    /// 空页抑制逻辑：
    /// 如果一个页面引用的所有数据源都为空DataTable（0行），
    /// 则该页面在渲染时会被标记为不可见。
    /// 例如：单位体检报表中的"个人附加信息"页面，如果是个人体检则为空。
    /// </summary>
    public static (string NormalizedTemplate, IReadOnlySet<string> EmptyPageNames) GetNormalizedWithEmptyPages(
        string content,
        IReadOnlyDictionary<string, DataTable> tables)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content)));
        if (!_cache.TryGetValue(hash, out var metadata))
        {
            var normalized = Normalize(content);
            var pages = ExtractPageDataSources(normalized);
            if (_cache.Count >= MaxCacheSize)
                EvictOldest();

            metadata = new TemplateMetadata(normalized, pages);
            _cache.TryAdd(hash, metadata);
        }

        // 计算空页：数据源全部为空的页面
        var emptyPages = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (tables.Count > 0)
        {
            foreach (var page in metadata.Pages)
            {
                if (page.DataSources.Length == 0)
                    continue;

                // 检查该页面引用的所有数据源
                var resolvedTables = page.DataSources
                    .Select(dataSource => tables.FirstOrDefault(pair =>
                        string.Equals(pair.Key, dataSource, StringComparison.OrdinalIgnoreCase)).Value)
                    .ToArray();

                // 如果任何数据源未找到，或任何数据源有数据，则该页面非空
                if (resolvedTables.Any(table => table is null) || resolvedTables.Any(table => table.Rows.Count > 0))
                    continue;

                // 所有数据源都为空，标记为需要抑制
                emptyPages.Add(page.PageName);
            }
        }

        return (metadata.NormalizedTemplate, emptyPages);
    }

    /// <summary>LRU缓存淘汰：移除最早添加的条目</summary>
    private static void EvictOldest()
    {
        if (_cache.IsEmpty) return;
        var oldestKey = _cache.Keys.OrderBy(k => k, StringComparer.Ordinal).FirstOrDefault();
        if (oldestKey is not null)
            _cache.TryRemove(oldestKey, out _);
    }

    /// <summary>
    /// 从FRX XML中提取页面数据源信息。
    /// 遍历所有ReportPage元素，提取Name属性和引用的DataSource。
    /// </summary>
    internal static IReadOnlyList<PageDataSourceInfo> ExtractPageDataSources(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
            return Array.Empty<PageDataSourceInfo>();

        var normalizedContent = content.TrimStart('\uFEFF', '\u0000', '\u200B');
        XDocument document;
        try
        {
            document = XDocument.Parse(normalizedContent, LoadOptions.PreserveWhitespace);
        }
        catch (System.Xml.XmlException)
        {
            return Array.Empty<PageDataSourceInfo>();
        }
        var result = new List<PageDataSourceInfo>();

        foreach (var page in document.Descendants().Where(element => element.Name.LocalName == "ReportPage"))
        {
            var pageName = page.Attribute("Name")?.Value;
            if (string.IsNullOrWhiteSpace(pageName))
                continue;

            // 提取该页面下所有元素引用的DataSource属性
            var dataSources = page.Descendants()
                .Select(element => element.Attribute("DataSource")?.Value)
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            result.Add(new PageDataSourceInfo(pageName, dataSources!));
        }

        return result;
    }

    /// <summary>
    /// 查找数据源全部为空的页面（公开方法）。
    /// 内部调用GetNormalizedWithEmptyPages。
    /// </summary>
    public static IReadOnlySet<string> FindPagesWhoseDataSourcesAreAllEmpty(
        string content,
        IReadOnlyDictionary<string, DataTable> tables)
    {
        var (_, emptyPages) = GetNormalizedWithEmptyPages(content, tables);
        return emptyPages;
    }

    /// <summary>
    /// RTF文本转换为纯文本：
    /// 1. 优先提取FastReport表达式（如 [Master.Name]）
    /// 2. 如果没有表达式，移除RTF控制字，保留可打印文本
    /// </summary>
    private static string ConvertRtfText(string value)
    {
        if (!value.StartsWith(@"{\rtf", StringComparison.OrdinalIgnoreCase))
            return value;

        // 旧版报表的RichObject通常只用RTF来格式化FastReport表达式
        // 例如：{\rtf1\ansi\ansicpg1252\deff0{\fonttbl{\f0 Arial;}}\f0\fs20 [Master.Name]}
        // 需要保留表达式 [Master.Name]，让TextObject正确求值
        var expressions = FastReportExpressionPattern().Matches(value)
            .Select(match => match.Value)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (expressions.Length > 0)
            return string.Join(Environment.NewLine, expressions);

        // 保守回退：移除RTF目的地/控制字，保留纯文本
        var withoutDestinations = RtfDestinationPattern().Replace(value, string.Empty);
        var withHexCharacters = RtfHexPattern().Replace(withoutDestinations, match =>
            ((char)Convert.ToByte(match.Groups["hex"].Value, 16)).ToString());
        var withLineBreaks = RtfParagraphPattern().Replace(withHexCharacters, Environment.NewLine);
        var withoutControls = RtfControlPattern().Replace(withLineBreaks, string.Empty);
        return withoutControls.Replace("{", string.Empty, StringComparison.Ordinal)
            .Replace("}", string.Empty, StringComparison.Ordinal)
            .Trim();
    }

    /// <summary>
    /// RTF字体回退：如果TextObject没有Font属性，从RTF中提取字号。
    /// 设置默认字体为楷体（KaiTi）。
    /// </summary>
    private static void ApplyRtfFontFallback(XElement element, string rtf)
    {
        if (element.Attribute("Font") is not null)
            return;

        var sizeMatch = RtfFontSizePattern().Match(rtf);
        if (!sizeMatch.Success || !int.TryParse(sizeMatch.Groups["halfPoints"].Value, out var halfPoints))
            return;

        var pointSize = halfPoints / 2d;
        element.SetAttributeValue("Font", $"KaiTi, {pointSize.ToString("0.#", CultureInfo.InvariantCulture)}pt");
    }

    // 正则表达式：匹配FastReport表达式 [Master.Name]
    [GeneratedRegex(@"\[[A-Za-z_][A-Za-z0-9_]*(?:\.[A-Za-z_][A-Za-z0-9_]*)+\]", RegexOptions.CultureInvariant)]
    private static partial Regex FastReportExpressionPattern();

    // 正则表达式：匹配RTF目的地（fonttbl、colortbl等）
    [GeneratedRegex(@"\{\\(?:fonttbl|colortbl|stylesheet|info|\*)[^{}]*(?:\{[^{}]*\}[^{}]*)*\}", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex RtfDestinationPattern();

    // 正则表达式：匹配RTF十六进制字符（如 \'80）
    [GeneratedRegex(@"\\'(?<hex>[0-9A-Fa-f]{2})", RegexOptions.CultureInvariant)]
    private static partial Regex RtfHexPattern();

    // 正则表达式：匹配RTF段落标记（\par）
    [GeneratedRegex(@"\\par\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex RtfParagraphPattern();

    // 正则表达式：匹配RTF控制字（如 \fs20、\b 等）
    [GeneratedRegex(@"\\[A-Za-z]+-?\d* ?|\\[^A-Za-z]", RegexOptions.CultureInvariant)]
    private static partial Regex RtfControlPattern();

    // 正则表达式：匹配RTF字号（\fs20 表示20半磅=10磅）
    [GeneratedRegex(@"\\fs(?<halfPoints>\d+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex RtfFontSizePattern();

    /// <summary>UTF-8编码的StringWriter，用于XML序列化</summary>
    private sealed class Utf8StringWriter : StringWriter
    {
        public override Encoding Encoding => Encoding.UTF8;
    }
}
