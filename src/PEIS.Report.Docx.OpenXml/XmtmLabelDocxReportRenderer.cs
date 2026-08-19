using System.Data;
using System.Globalization;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Drawing;
using DocumentFormat.OpenXml.Drawing.Wordprocessing;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using PEIS.Report.Engine;
using ZXing;
using ZXing.Common;
using ZXing.Rendering;
using A = DocumentFormat.OpenXml.Drawing;
using DW = DocumentFormat.OpenXml.Drawing.Wordprocessing;
using PIC = DocumentFormat.OpenXml.Drawing.Pictures;
using W = DocumentFormat.OpenXml.Wordprocessing;

namespace PEIS.Report.Docx.OpenXml;

/// <summary>
/// Editable 50 x 30 mm Word label template for the evidenced xmtm result shape. It intentionally recreates the
/// page geometry and report fields from the PDF instead of embedding a PDF/image, so field text remains editable.
/// This renderer is report-specific; other FRX layouts need their own Word template implementation.
/// </summary>
public sealed class XmtmLabelDocxReportRenderer
{
    private const int PageWidthTwips = 2835; // 50 mm
    private const int PageHeightTwips = 1701; // 30 mm
    private const long BarcodeWidthEmu = 1_188_000L; // 33 mm
    private const long BarcodeHeightEmu = 324_000L; // 9 mm

    public Task<DocxReportOutput> RenderAsync(
        ReportDataSet reportData,
        string? watermarkText,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(reportData);
        cancellationToken.ThrowIfCancellationRequested();

        var table = ResolveMasterTable(reportData);
        if (table.Rows.Count == 0)
            throw new InvalidOperationException("xmtm Word label requires one Master data row.");
        var row = table.Rows[0];

        using var stream = new MemoryStream();
        using (var document = WordprocessingDocument.Create(stream, WordprocessingDocumentType.Document, true))
        {
            var mainPart = document.AddMainDocumentPart();
            var body = new W.Body();
            mainPart.Document = new W.Document(body);

            body.Append(CreateInfoTable(row));
            body.Append(CreateBarcodeParagraph(mainPart, Cell(row, "tmh")));
            body.Append(CreateSmallCenteredParagraph(Cell(row, "tmh"), bold: false, fontSize: "14", before: "0", after: "0"));
            body.Append(CreateSmallCenteredParagraph(CombineItemName(row), bold: false, fontSize: "16", before: "0", after: "0"));
            body.Append(CreateSectionProperties());

            if (!string.IsNullOrWhiteSpace(watermarkText))
                ConfigureHeaderWatermark(mainPart, watermarkText.Trim());

            mainPart.Document.Save();
        }

        return Task.FromResult(new DocxReportOutput(stream.ToArray(), 1, 1));
    }

    private static DataTable ResolveMasterTable(ReportDataSet reportData)
    {
        if (reportData.Tables.TryGetValue("Master", out var master))
            return master;
        return reportData.Tables.Values.FirstOrDefault()
            ?? throw new InvalidOperationException("xmtm Word label requires one data table.");
    }

    private static W.Table CreateInfoTable(DataRow row)
    {
        var table = new W.Table(
            new W.TableProperties(
                new W.TableWidth { Type = W.TableWidthUnitValues.Pct, Width = "5000" },
                new W.TableBorders(
                    new W.TopBorder { Val = W.BorderValues.Nil },
                    new W.BottomBorder { Val = W.BorderValues.Nil },
                    new W.LeftBorder { Val = W.BorderValues.Nil },
                    new W.RightBorder { Val = W.BorderValues.Nil },
                    new W.InsideHorizontalBorder { Val = W.BorderValues.Nil },
                    new W.InsideVerticalBorder { Val = W.BorderValues.Nil })));

        table.Append(CreateInfoRow($"姓名：{Cell(row, "xm")}", $"性别：{Cell(row, "xb")}"));
        table.Append(CreateInfoRow($"年龄：{Cell(row, "nl")}", $"科室：{Cell(row, "zxksmc")}"));
        return table;
    }

    private static W.TableRow CreateInfoRow(string left, string right) => new(
        CreateInfoCell(left),
        CreateInfoCell(right));

    private static W.TableCell CreateInfoCell(string value) => new(
        new W.TableCellProperties(
            new W.TableCellWidth { Type = W.TableWidthUnitValues.Pct, Width = "2500" },
            new W.TableCellVerticalAlignment { Val = W.TableVerticalAlignmentValues.Center },
            new W.TableCellMargin(
                new W.TopMargin { Width = "0", Type = W.TableWidthUnitValues.Dxa },
                new W.BottomMargin { Width = "0", Type = W.TableWidthUnitValues.Dxa },
                new W.StartMargin { Width = "0", Type = W.TableWidthUnitValues.Dxa },
                new W.EndMargin { Width = "0", Type = W.TableWidthUnitValues.Dxa })),
        CreateSmallParagraph(value, bold: true, fontSize: "16", before: "0", after: "0"));

    private static W.Paragraph CreateBarcodeParagraph(MainDocumentPart mainPart, string barcodeValue)
    {
        var bytes = CreateCode128Png(barcodeValue);
        var imagePart = mainPart.AddImagePart(ImagePartType.Bmp);
        using (var imageStream = new MemoryStream(bytes))
            imagePart.FeedData(imageStream);

        var relationshipId = mainPart.GetIdOfPart(imagePart);
        var picture = new PIC.Picture(
            new PIC.NonVisualPictureProperties(
                new PIC.NonVisualDrawingProperties { Id = 0U, Name = "xmtm-code128.png" },
                new PIC.NonVisualPictureDrawingProperties()),
            new PIC.BlipFill(
                new A.Blip { Embed = relationshipId },
                new A.Stretch(new A.FillRectangle())),
            new PIC.ShapeProperties(
                new A.Transform2D(
                    new A.Offset { X = 0L, Y = 0L },
                    new A.Extents { Cx = BarcodeWidthEmu, Cy = BarcodeHeightEmu }),
                new A.PresetGeometry(new A.AdjustValueList()) { Preset = A.ShapeTypeValues.Rectangle }));
        var graphicData = new A.GraphicData(picture)
        {
            Uri = "http://schemas.openxmlformats.org/drawingml/2006/picture"
        };
        var inline = new DW.Inline(
            new DW.Extent { Cx = BarcodeWidthEmu, Cy = BarcodeHeightEmu },
            new DW.EffectExtent { LeftEdge = 0L, TopEdge = 0L, RightEdge = 0L, BottomEdge = 0L },
            new DW.DocProperties { Id = 1U, Name = "xmtm-code128" },
            new DW.NonVisualGraphicFrameDrawingProperties(
                new A.GraphicFrameLocks { NoChangeAspect = true }),
            new A.Graphic(graphicData));
        var drawing = new W.Drawing(inline);

        return new W.Paragraph(
            new W.ParagraphProperties(
                new W.Justification { Val = W.JustificationValues.Center },
                new W.SpacingBetweenLines { Before = "0", After = "0", Line = "72", LineRule = W.LineSpacingRuleValues.Exact }),
            new W.Run(drawing));
    }

    private static byte[] CreateCode128Png(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
            content = " ";
        var writer = new BarcodeWriterPixelData
        {
            Format = BarcodeFormat.CODE_128,
            Options = new EncodingOptions { Height = 64, Width = 320, Margin = 0 }
        };
        return EncodeBitmap(writer.Write(content));
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

    private static W.Paragraph CreateSmallCenteredParagraph(string text, bool bold, string fontSize, string before, string after) =>
        CreateSmallParagraph(text, bold, fontSize, before, after, W.JustificationValues.Center);

    private static W.Paragraph CreateSmallParagraph(string text, bool bold, string fontSize, string before, string after, W.JustificationValues? justification = null)
    {
        var properties = new W.ParagraphProperties(
            new W.SpacingBetweenLines { Before = before, After = after, Line = "144", LineRule = W.LineSpacingRuleValues.Exact });
        if (justification is not null)
            properties.Append(new W.Justification { Val = justification.Value });

        return new W.Paragraph(
            properties,
            new W.Run(
                new W.RunProperties(
                    new W.RunFonts { Ascii = "Microsoft YaHei", HighAnsi = "Microsoft YaHei", EastAsia = "Microsoft YaHei" },
                    new W.FontSize { Val = fontSize },
                    new W.Bold { Val = bold }),
                new W.Text(text) { Space = SpaceProcessingModeValues.Preserve }));
    }

    private static W.SectionProperties CreateSectionProperties() => new(
        new W.PageSize { Width = PageWidthTwips, Height = PageHeightTwips, Orient = W.PageOrientationValues.Landscape },
        new W.PageMargin { Top = 50, Right = 50U, Bottom = 50, Left = 50U, Header = 0U, Footer = 0U, Gutter = 0U });

    private static void ConfigureHeaderWatermark(MainDocumentPart mainPart, string text)
    {
        var headerPart = mainPart.AddNewPart<HeaderPart>();
        var header = new W.Header(
            new W.Paragraph(
                new W.ParagraphProperties(new W.Justification { Val = W.JustificationValues.Center }),
                new W.Run(
                    new W.RunProperties(
                        new W.RunFonts { Ascii = "Microsoft YaHei", HighAnsi = "Microsoft YaHei", EastAsia = "Microsoft YaHei" },
                        new W.FontSize { Val = "16" },
                        new W.Color { Val = "D9D9D9" }),
                    new W.Text(text))));
        headerPart.Header = header;
        headerPart.Header.Save();

        var wordDocument = mainPart.Document ?? throw new InvalidOperationException("DOCX document has not been initialized.");
        var body = wordDocument.Body ?? throw new InvalidOperationException("DOCX body has not been initialized.");
        var section = body.Elements<W.SectionProperties>().Single();
        section.PrependChild(new W.HeaderReference { Type = W.HeaderFooterValues.Default, Id = mainPart.GetIdOfPart(headerPart) });
    }

    private static string CombineItemName(DataRow row)
    {
        var item = Cell(row, "XMMC");
        var category = Cell(row, "flmc");
        return string.IsNullOrWhiteSpace(category) ? item : $"{item}（{category}）";
    }

    private static string Cell(DataRow row, string column)
    {
        if (!row.Table.Columns.Contains(column) || row[column] is null or DBNull)
            return string.Empty;
        return Convert.ToString(row[column], CultureInfo.InvariantCulture)?.Trim() ?? string.Empty;
    }
}
