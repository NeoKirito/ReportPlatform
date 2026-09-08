using System.Collections.Concurrent;
using System.Data;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace PEIS.Report.FastReport.OpenSource;

/// <summary>
/// Applies narrowly scoped compatibility conversions for legacy FRX objects that are not present in the
/// MIT-licensed FastReport Open Source runtime. The database-owned template remains unchanged.
/// Results are cached by content hash to avoid redundant XML parsing on repeated renders.
/// </summary>
internal static partial class LegacyFrxCompatibility
{
    internal sealed record PageDataSourceInfo(string PageName, string[] DataSources);
    internal sealed record TemplateMetadata(string NormalizedTemplate, IReadOnlyList<PageDataSourceInfo> Pages);

    private static readonly ConcurrentDictionary<string, TemplateMetadata> _cache = new(StringComparer.Ordinal);
    private const int MaxCacheSize = 128;

    public static string Normalize(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
            return content;

        var containsRichObject = content.Contains("<RichObject", StringComparison.Ordinal);
        var mayHaveUnusedDoublePass = content.Contains("DoublePass=\"true\"", StringComparison.OrdinalIgnoreCase)
            && !RequiresTotalPagePass(content);
        if (!containsRichObject && !mayHaveUnusedDoublePass)
            return content;

        var normalizedContent = content.TrimStart('\uFEFF', '\u0000', '\u200B');
        XDocument document;
        try
        {
            document = XDocument.Parse(normalizedContent, LoadOptions.PreserveWhitespace);
        }
        catch (System.Xml.XmlException)
        {
            return content;
        }
        var changed = false;
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

    private static bool RequiresTotalPagePass(string content)
        => content.Contains("TotalPages", StringComparison.OrdinalIgnoreCase)
            || content.Contains("PageNofM", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Returns normalized FRX and dynamically computed empty-page names for the current request.
    /// Template parsing is performed once and cached by template SHA256; page suppression is evaluated in O(1).
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

        var emptyPages = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (tables.Count > 0)
        {
            foreach (var page in metadata.Pages)
            {
                if (page.DataSources.Length == 0)
                    continue;

                var resolvedTables = page.DataSources
                    .Select(dataSource => tables.FirstOrDefault(pair =>
                        string.Equals(pair.Key, dataSource, StringComparison.OrdinalIgnoreCase)).Value)
                    .ToArray();

                if (resolvedTables.Any(table => table is null) || resolvedTables.Any(table => table.Rows.Count > 0))
                    continue;

                emptyPages.Add(page.PageName);
            }
        }

        return (metadata.NormalizedTemplate, emptyPages);
    }

    private static void EvictOldest()
    {
        if (_cache.IsEmpty) return;
        var oldestKey = _cache.Keys.OrderBy(k => k, StringComparer.Ordinal).FirstOrDefault();
        if (oldestKey is not null)
            _cache.TryRemove(oldestKey, out _);
    }

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

            var dataSources = page.Descendants()
                .Select(element => element.Attribute("DataSource")?.Value)
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            result.Add(new PageDataSourceInfo(pageName, dataSources!));
        }

        return result;
    }

    public static IReadOnlySet<string> FindPagesWhoseDataSourcesAreAllEmpty(
        string content,
        IReadOnlyDictionary<string, DataTable> tables)
    {
        var (_, emptyPages) = GetNormalizedWithEmptyPages(content, tables);
        return emptyPages;
    }

    private static string ConvertRtfText(string value)
    {
        if (!value.StartsWith(@"{\rtf", StringComparison.OrdinalIgnoreCase))
            return value;

        // Legacy report RichObjects in the observed database use RTF only to format FastReport expressions.
        // Preserve those expressions exactly so TextObject evaluates the same registered data columns.
        var expressions = FastReportExpressionPattern().Matches(value)
            .Select(match => match.Value)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (expressions.Length > 0)
            return string.Join(Environment.NewLine, expressions);

        // Conservative fallback for fixed RTF text: remove destinations/control words and retain printable text.
        var withoutDestinations = RtfDestinationPattern().Replace(value, string.Empty);
        var withHexCharacters = RtfHexPattern().Replace(withoutDestinations, match =>
            ((char)Convert.ToByte(match.Groups["hex"].Value, 16)).ToString());
        var withLineBreaks = RtfParagraphPattern().Replace(withHexCharacters, Environment.NewLine);
        var withoutControls = RtfControlPattern().Replace(withLineBreaks, string.Empty);
        return withoutControls.Replace("{", string.Empty, StringComparison.Ordinal)
            .Replace("}", string.Empty, StringComparison.Ordinal)
            .Trim();
    }

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

    [GeneratedRegex(@"\[[A-Za-z_][A-Za-z0-9_]*(?:\.[A-Za-z_][A-Za-z0-9_]*)+\]", RegexOptions.CultureInvariant)]
    private static partial Regex FastReportExpressionPattern();

    [GeneratedRegex(@"\{\\(?:fonttbl|colortbl|stylesheet|info|\*)[^{}]*(?:\{[^{}]*\}[^{}]*)*\}", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex RtfDestinationPattern();

    [GeneratedRegex(@"\\'(?<hex>[0-9A-Fa-f]{2})", RegexOptions.CultureInvariant)]
    private static partial Regex RtfHexPattern();

    [GeneratedRegex(@"\\par\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex RtfParagraphPattern();

    [GeneratedRegex(@"\\[A-Za-z]+-?\d* ?|\\[^A-Za-z]", RegexOptions.CultureInvariant)]
    private static partial Regex RtfControlPattern();

    [GeneratedRegex(@"\\fs(?<halfPoints>\d+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex RtfFontSizePattern();

    private sealed class Utf8StringWriter : StringWriter
    {
        public override Encoding Encoding => Encoding.UTF8;
    }
}
