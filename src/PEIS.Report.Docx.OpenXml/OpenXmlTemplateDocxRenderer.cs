using System.Data;
using System.Globalization;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using PEIS.Report.Engine;
using ZXing;
using ZXing.Common;
using ZXing.Rendering;
using V = DocumentFormat.OpenXml.Vml;
using W = DocumentFormat.OpenXml.Wordprocessing;

namespace PEIS.Report.Docx.OpenXml;

/// <summary>
/// Generic fixed-layout Open XML renderer. It knows how to render template elements, but knows nothing about specific
/// reports, column names, or report layouts. Each template binds expressions to existing ReportDataSet tables.
/// </summary>
public sealed class OpenXmlTemplateDocxRenderer
{
    public Task<DocxReportOutput> RenderAsync(
        DocxTemplateDefinition template,
        ReportDataSet reportData,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(reportData);
        cancellationToken.ThrowIfCancellationRequested();

        using var stream = new MemoryStream();
        using (var document = WordprocessingDocument.Create(stream, WordprocessingDocumentType.Document, true))
        {
            var mainPart = document.AddMainDocumentPart();
            var body = new W.Body();
            mainPart.Document = new W.Document(body);
            body.Append(CreateCanvas(mainPart, template, reportData, cancellationToken));
            body.Append(CreateSectionProperties(template.Page));
            mainPart.Document.Save();
        }

        return Task.FromResult(new DocxReportOutput(stream.ToArray(), reportData.Tables.Count, reportData.RowCount));
    }

    private static W.Paragraph CreateCanvas(
        MainDocumentPart mainPart,
        DocxTemplateDefinition template,
        ReportDataSet reportData,
        CancellationToken cancellationToken)
    {
        var paragraph = new W.Paragraph(
            new W.ParagraphProperties(
                new W.SpacingBetweenLines { Before = "0", After = "0", Line = "1", LineRule = W.LineSpacingRuleValues.Exact }));
        paragraph.Append(CreateTextBoxShapeTypeRun());

        foreach (var element in template.Elements.OrderBy(x => x.ZIndex).ThenBy(x => x.ElementId, StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            switch (element)
            {
                case DocxTextElement text:
                    paragraph.Append(CreateTextBoxRun(text, ResolveExpression(reportData, text.Expression)));
                    break;
                case DocxBarcodeElement barcode:
                    paragraph.Append(CreateBarcodeRun(mainPart, barcode, ResolveExpression(reportData, barcode.Expression)));
                    break;
                default:
                    throw new NotSupportedException($"DOCX template element '{element.ElementId}' has unsupported type '{element.GetType().Name}'.");
            }
        }

        return paragraph;
    }

    private static W.Run CreateTextBoxShapeTypeRun()
    {
        var shapeType = new V.Shapetype
        {
            Id = "_x0000_t202",
            CoordinateSize = "21600,21600",
            OptionalNumber = 202,
            Filled = false,
            Stroked = false
        };
        shapeType.SetAttribute(new OpenXmlAttribute(string.Empty, "path", string.Empty, "m,l,21600,21600e"));
        return new W.Run(new W.Picture(shapeType));
    }

    private static W.Run CreateTextBoxRun(DocxTextElement element, string text)
    {
        var properties = new W.ParagraphProperties(
            new W.SpacingBetweenLines { Before = "0", After = "0", Line = "1", LineRule = W.LineSpacingRuleValues.Exact });
        if (element.Alignment == DocxHorizontalAlignment.Center)
            properties.Append(new W.Justification { Val = W.JustificationValues.Center });
        else if (element.Alignment == DocxHorizontalAlignment.Right)
            properties.Append(new W.Justification { Val = W.JustificationValues.Right });

        var content = new W.TextBoxContent(
            new W.Paragraph(
                properties,
                new W.Run(
                    new W.RunProperties(
                        new W.RunFonts { Ascii = element.FontFamily, HighAnsi = element.FontFamily, EastAsia = element.FontFamily },
                        new W.FontSize { Val = (element.FontPoints * 2).ToString("0", CultureInfo.InvariantCulture) },
                        new W.Bold { Val = element.Bold }),
                    new W.Text(text) { Space = SpaceProcessingModeValues.Preserve })));
        var shape = CreateShape(element, $"docx-text-{element.ElementId}");
        shape.Type = "#_x0000_t202";
        shape.Append(new V.TextBox(content) { Inset = "0,0,0,0", Style = "mso-fit-shape-to-text:t" });
        return new W.Run(new W.Picture(shape));
    }

    private static W.Run CreateBarcodeRun(MainDocumentPart mainPart, DocxBarcodeElement element, string value)
    {
        if (element.Format != DocxBarcodeFormat.Code128)
            throw new NotSupportedException($"DOCX barcode format '{element.Format}' is not supported.");

        var imagePart = mainPart.AddImagePart(ImagePartType.Bmp);
        using (var imageStream = new MemoryStream(CreateCode128Bmp(value)))
            imagePart.FeedData(imageStream);

        var shape = CreateShape(element, $"docx-barcode-{element.ElementId}");
        shape.Append(new V.ImageData { RelationshipId = mainPart.GetIdOfPart(imagePart) });
        return new W.Run(new W.Picture(shape));
    }

    private static V.Shape CreateShape(DocxTemplateElement element, string id) => new()
    {
        Id = id,
        Style = string.Format(
            CultureInfo.InvariantCulture,
            "position:absolute;left:{0:0.##}pt;top:{1:0.##}pt;width:{2:0.##}pt;height:{3:0.##}pt;z-index:{4};mso-position-horizontal-relative:page;mso-position-vertical-relative:page;mso-wrap-style:none;v-text-anchor:top",
            element.LeftPoints,
            element.TopPoints,
            element.WidthPoints,
            element.HeightPoints,
            element.ZIndex),
        Filled = false,
        Stroked = false
    };

    private static string ResolveExpression(ReportDataSet reportData, string expression)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(expression);
        var cursor = 0;
        var builder = new System.Text.StringBuilder(expression.Length);
        while (true)
        {
            var open = expression.IndexOf("{{", cursor, StringComparison.Ordinal);
            if (open < 0)
            {
                builder.Append(expression, cursor, expression.Length - cursor);
                break;
            }
            var close = expression.IndexOf("}}", open + 2, StringComparison.Ordinal);
            if (close < 0)
                return expression;
            builder.Append(expression, cursor, open - cursor);
            builder.Append(ResolveScalar(reportData, expression[(open + 2)..close].Trim()));
            cursor = close + 2;
        }

        var resolved = builder.ToString();
        return string.Equals(resolved, expression, StringComparison.Ordinal)
            ? ResolveScalar(reportData, expression)
            : resolved;
    }

    private static string ResolveScalar(ReportDataSet reportData, string expression)
    {
        var separator = expression.IndexOf('.', StringComparison.Ordinal);
        if (separator < 1 || separator == expression.Length - 1)
            return expression;

        var tableName = expression[..separator];
        var columnName = expression[(separator + 1)..];
        if (!reportData.Tables.TryGetValue(tableName, out var table) || table.Rows.Count == 0 || !table.Columns.Contains(columnName))
            return string.Empty;
        var value = table.Rows[0][columnName];
        return value is null or DBNull ? string.Empty : Convert.ToString(value, CultureInfo.InvariantCulture)?.Trim() ?? string.Empty;
    }

    private static byte[] CreateCode128Bmp(string content)
    {
        var writer = new BarcodeWriterPixelData
        {
            Format = BarcodeFormat.CODE_128,
            Options = new EncodingOptions { Height = 108, Width = 342, Margin = 0 }
        };
        return EncodeBitmap(writer.Write(string.IsNullOrWhiteSpace(content) ? " " : content));
    }

    private static byte[] EncodeBitmap(PixelData pixelData)
    {
        var stride = (pixelData.Width * 3 + 3) & ~3;
        var imageSize = stride * pixelData.Height;
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write((ushort)0x4D42);
        writer.Write(14 + 40 + imageSize);
        writer.Write(0);
        writer.Write(14 + 40);
        writer.Write(40);
        writer.Write(pixelData.Width);
        writer.Write(pixelData.Height);
        writer.Write((ushort)1);
        writer.Write((ushort)24);
        writer.Write(0);
        writer.Write(imageSize);
        writer.Write(3780);
        writer.Write(3780);
        writer.Write(0);
        writer.Write(0);
        var padding = new byte[stride - pixelData.Width * 3];
        for (var y = pixelData.Height - 1; y >= 0; y--)
        {
            for (var x = 0; x < pixelData.Width; x++)
            {
                var source = (y * pixelData.Width + x) * 4;
                writer.Write(pixelData.Pixels[source + 2]);
                writer.Write(pixelData.Pixels[source + 1]);
                writer.Write(pixelData.Pixels[source]);
            }
            writer.Write(padding);
        }
        writer.Flush();
        return stream.ToArray();
    }

    private static W.SectionProperties CreateSectionProperties(DocxTemplatePage page)
    {
        var widthTwips = Convert.ToInt32(Math.Round(page.WidthMillimeters * 56.6929133858, MidpointRounding.AwayFromZero));
        var heightTwips = Convert.ToInt32(Math.Round(page.HeightMillimeters * 56.6929133858, MidpointRounding.AwayFromZero));
        return new W.SectionProperties(
            new W.PageSize { Width = checked((uint)widthTwips), Height = checked((uint)heightTwips), Orient = W.PageOrientationValues.Landscape },
            new W.PageMargin { Top = 0, Right = 0U, Bottom = 0, Left = 0U, Header = 0U, Footer = 0U, Gutter = 0U });
    }
}
