using System.Globalization;
using System.Xml.Linq;
using PEIS.Report.Engine;

namespace PEIS.Report.Docx.OpenXml;

/// <summary>
/// Converts a FastReport XML template (FRX) into a generic DOCX layout definition. This class intentionally does not
/// depend on FastReport runtime types: PDF and DOCX therefore use the same database-owned FRX source without leaking
/// FastReport types outside the dedicated FastReport runtime project.
/// </summary>
public sealed class FastReportFrxDocxTemplateCompiler
{
    // FastReport's FRX coordinates use 96 dpi pixels; DOCX VML coordinates use points (72 dpi).
    private const double FrxUnitToPoint = 0.75;

    public FrxDocxTemplateCompilation Compile(ReportTemplate frxTemplate)
    {
        ArgumentNullException.ThrowIfNull(frxTemplate);
        var xml = DecodeFrxXml(frxTemplate.Content);
        var document = XDocument.Parse(xml, LoadOptions.None);
        var page = document.Descendants("ReportPage").FirstOrDefault()
            ?? throw new InvalidOperationException($"FRX template '{frxTemplate.TemplateKey}' has no ReportPage.");

        var pageWidth = ParseDouble(page.Attribute("PaperWidth")?.Value, 210);
        var pageHeight = ParseDouble(page.Attribute("PaperHeight")?.Value, 297);
        var elements = new List<DocxTemplateElement>();
        var unsupported = new List<string>();
        var sequence = 1;

        foreach (var node in page.Descendants())
        {
            if (node.Parent is null || !node.Ancestors("DataBand").Any())
                continue;

            var name = node.Name.LocalName;
            if (name == "TextObject")
            {
                elements.Add(CreateTextElement(node, sequence++));
                continue;
            }
            if (name == "BarcodeObject")
            {
                if (!string.Equals(node.Attribute("Barcode")?.Value, "Code128", StringComparison.OrdinalIgnoreCase))
                {
                    unsupported.Add($"{name}:{node.Attribute("Name")?.Value}:barcode={node.Attribute("Barcode")?.Value}");
                    continue;
                }
                elements.Add(CreateBarcodeElement(node, sequence++));
                continue;
            }

            if (name is "DataBand" or "Column" or "TableDataSource" or "Dictionary" or "Parameter")
                continue;
            if (node.Elements().Any())
                continue;
            unsupported.Add($"{name}:{node.Attribute("Name")?.Value}");
        }

        var definition = new DocxTemplateDefinition(
            frxTemplate.TemplateKey,
            frxTemplate.Version,
            new DocxTemplatePage(pageWidth, pageHeight),
            elements.OrderBy(x => x.ZIndex).ToArray());
        return new FrxDocxTemplateCompilation(definition, unsupported.Distinct(StringComparer.OrdinalIgnoreCase).ToArray());
    }

    private static DocxTextElement CreateTextElement(XElement node, int zIndex)
    {
        var text = ConvertFastReportExpression(node.Attribute("Text")?.Value ?? string.Empty);
        var font = ParseFont(node.Attribute("Font")?.Value);
        return new DocxTextElement(
            ElementId: node.Attribute("Name")?.Value ?? $"text-{zIndex}",
            LeftPoints: ToPoints(node.Attribute("Left")?.Value),
            TopPoints: ToPoints(node.Attribute("Top")?.Value),
            WidthPoints: ToPoints(node.Attribute("Width")?.Value),
            HeightPoints: ToPoints(node.Attribute("Height")?.Value),
            ZIndex: zIndex,
            Expression: text,
            FontFamily: font.Family,
            FontPoints: font.Points,
            Bold: font.Bold,
            Alignment: ParseAlignment(node.Attribute("HorzAlign")?.Value));
    }

    private static DocxBarcodeElement CreateBarcodeElement(XElement node, int zIndex) => new(
        ElementId: node.Attribute("Name")?.Value ?? $"barcode-{zIndex}",
        LeftPoints: ToPoints(node.Attribute("Left")?.Value),
        TopPoints: ToPoints(node.Attribute("Top")?.Value),
        WidthPoints: ToPoints(node.Attribute("Width")?.Value),
        HeightPoints: ToPoints(node.Attribute("Height")?.Value),
        ZIndex: zIndex,
        Expression: ConvertFastReportExpression(node.Attribute("Text")?.Value ?? string.Empty));

    private static string DecodeFrxXml(string content)
    {
        var trimmed = NormalizeXmlPrefix(content);
        if (trimmed.StartsWith("<", StringComparison.Ordinal))
            return trimmed;
        try
        {
            var decoded = NormalizeXmlPrefix(System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(trimmed)));
            if (decoded.StartsWith("<", StringComparison.Ordinal))
                return decoded;
        }
        catch (FormatException)
        {
            // The final exception below identifies the input as an invalid FRX payload without emitting the content.
        }
        throw new InvalidOperationException("FRX template content is neither XML nor Base64-encoded UTF-8 XML.");
    }

    private static string NormalizeXmlPrefix(string value) => value.Trim().TrimStart('\uFEFF');

    private static string ConvertFastReportExpression(string text) => System.Text.RegularExpressions.Regex.Replace(
        text,
        "\\[([^\\]]+)\\]",
        match => "{{" + match.Groups[1].Value.Trim() + "}}");

    private static (string Family, double Points, bool Bold) ParseFont(string? font)
    {
        if (string.IsNullOrWhiteSpace(font)) return ("Microsoft YaHei", 9, false);
        var parts = font.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var family = parts.Length > 0 ? parts[0] : "Microsoft YaHei";
        var pointPart = parts.FirstOrDefault(x => x.EndsWith("pt", StringComparison.OrdinalIgnoreCase));
        var points = double.TryParse(pointPart?.Replace("pt", string.Empty, StringComparison.OrdinalIgnoreCase), NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : 9;
        return (family, points, font.Contains("style=Bold", StringComparison.OrdinalIgnoreCase));
    }

    private static DocxHorizontalAlignment ParseAlignment(string? alignment) => alignment?.Trim().ToUpperInvariant() switch
    {
        "CENTER" => DocxHorizontalAlignment.Center,
        "RIGHT" => DocxHorizontalAlignment.Right,
        _ => DocxHorizontalAlignment.Left
    };

    private static double ToPoints(string? frxValue) => ParseDouble(frxValue, 0) * FrxUnitToPoint;

    private static double ParseDouble(string? value, double fallback) => double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
        ? parsed
        : fallback;
}

public sealed record FrxDocxTemplateCompilation(
    DocxTemplateDefinition Template,
    IReadOnlyList<string> UnsupportedObjects);
