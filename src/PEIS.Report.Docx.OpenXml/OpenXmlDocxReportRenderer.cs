using System.Data;
using System.Globalization;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using PEIS.Report.Engine;

namespace PEIS.Report.Docx.OpenXml;

/// <summary>
/// Free Open XML prototype renderer. It deliberately consumes the existing <see cref="ReportDataSet"/> boundary and
/// does not inspect FRX, PDF, controller, or PrintAgent types. Production templates can later replace this generic
/// table layout without changing legacy SQL retrieval.
/// </summary>
public sealed class OpenXmlDocxReportRenderer
{
    public Task<DocxReportOutput> RenderAsync(
        ReportDataSet reportData,
        string reportTitle,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(reportData);
        ArgumentException.ThrowIfNullOrWhiteSpace(reportTitle);
        cancellationToken.ThrowIfCancellationRequested();

        using var stream = new MemoryStream();
        using (var document = WordprocessingDocument.Create(stream, WordprocessingDocumentType.Document, true))
        {
            var mainPart = document.AddMainDocumentPart();
            var body = new Body();
            mainPart.Document = new Document(body);

            body.Append(CreateParagraph(reportTitle, bold: true, fontSize: "32", justification: JustificationValues.Center));
            body.Append(CreateParagraph("DOCX 试验导出：本文件由免费 Open XML 渲染器生成；内容为现有报表查询结果。", bold: false, fontSize: "20"));

            foreach (var item in reportData.Tables.OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase))
            {
                cancellationToken.ThrowIfCancellationRequested();
                body.Append(CreateParagraph($"数据集：{item.Key}（{item.Value.Rows.Count} 行）", bold: true, fontSize: "24"));
                body.Append(CreateTable(item.Value, cancellationToken));
            }

            if (reportData.Tables.Count == 0)
                body.Append(CreateParagraph("该报表查询未返回可导出的数据表。", bold: false, fontSize: "20"));

            mainPart.Document.Save();
        }

        return Task.FromResult(new DocxReportOutput(stream.ToArray(), reportData.Tables.Count, reportData.RowCount));
    }

    private static Table CreateTable(DataTable table, CancellationToken cancellationToken)
    {
        var wordTable = new Table(
            new TableProperties(
                new TableBorders(
                    new TopBorder { Val = BorderValues.Single, Size = 6U },
                    new BottomBorder { Val = BorderValues.Single, Size = 6U },
                    new LeftBorder { Val = BorderValues.Single, Size = 6U },
                    new RightBorder { Val = BorderValues.Single, Size = 6U },
                    new InsideHorizontalBorder { Val = BorderValues.Single, Size = 4U },
                    new InsideVerticalBorder { Val = BorderValues.Single, Size = 4U })));

        var header = new TableRow();
        foreach (DataColumn column in table.Columns)
            header.Append(CreateCell(column.ColumnName, bold: true));
        wordTable.Append(header);

        foreach (DataRow row in table.Rows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var wordRow = new TableRow();
            foreach (DataColumn column in table.Columns)
                wordRow.Append(CreateCell(FormatCellValue(row[column]), bold: false));
            wordTable.Append(wordRow);
        }

        return wordTable;
    }

    private static TableCell CreateCell(string value, bool bold) => new(
        new TableCellProperties(new TableCellVerticalAlignment { Val = TableVerticalAlignmentValues.Center }),
        CreateParagraph(value, bold, "20"));

    private static Paragraph CreateParagraph(string text, bool bold, string fontSize, JustificationValues? justification = null)
    {
        var properties = new ParagraphProperties();
        if (justification is not null)
            properties.Append(new Justification { Val = justification.Value });

        var run = new Run(
            new RunProperties(
                new RunFonts { Ascii = "Microsoft YaHei", HighAnsi = "Microsoft YaHei", EastAsia = "Microsoft YaHei" },
                new FontSize { Val = fontSize },
                new Bold { Val = bold }),
            new Text(text) { Space = SpaceProcessingModeValues.Preserve });
        return new Paragraph(properties, run);
    }

    private static string FormatCellValue(object value) => value switch
    {
        null or DBNull => string.Empty,
        DateTime dateTime => dateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
        DateTimeOffset dateTimeOffset => dateTimeOffset.ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture),
        byte[] bytes => $"[二进制数据 {bytes.Length} 字节]",
        _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty
    };
}

public sealed record DocxReportOutput(byte[] Docx, int TableCount, int RowCount);
