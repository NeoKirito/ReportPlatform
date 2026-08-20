using PEIS.Report.Contracts;
using PEIS.Report.Engine;

namespace PEIS.Report.Docx.OpenXml;

/// <summary>
/// Exports a report as an editable DOCX using the same legacy report definition, FRX template, and data-query
/// boundaries as PDF rendering. It intentionally contains no FastReport runtime types and never maintains a second
/// Word template source.
/// </summary>
public interface IFrxDocxReportExporter
{
    Task<FrxDocxExportResult> ExportAsync(ReportRenderRequest request, CancellationToken cancellationToken);
}

public sealed record FrxDocxExportResult(
    byte[] Docx,
    string FileName,
    int TableCount,
    int RowCount,
    IReadOnlyList<string> UnsupportedObjects);

public sealed class FrxDocxReportExporter(
    IReportDefinitionProvider definitions,
    ITemplateProvider templates,
    IReportDataProvider data,
    FastReportFrxDocxTemplateCompiler compiler,
    OpenXmlTemplateDocxRenderer renderer) : IFrxDocxReportExporter
{
    public async Task<FrxDocxExportResult> ExportAsync(ReportRenderRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ReportId);

        var definition = await definitions.GetRequiredAsync(request, cancellationToken).ConfigureAwait(false);
        var template = await templates.GetRequiredAsync(definition, cancellationToken).ConfigureAwait(false);
        var reportData = await data.QueryAsync(definition, request, cancellationToken).ConfigureAwait(false);
        var compilation = compiler.Compile(template);
        var output = await renderer.RenderAsync(compilation.Template, reportData, cancellationToken).ConfigureAwait(false);

        return new FrxDocxExportResult(
            output.Docx,
            FileName(request.FileName, request.ReportId),
            output.TableCount,
            output.RowCount,
            compilation.UnsupportedObjects);
    }

    private static string FileName(string? requestedFileName, string reportId)
    {
        var candidate = string.IsNullOrWhiteSpace(requestedFileName) ? reportId : requestedFileName;
        var safeStem = Path.GetFileNameWithoutExtension(candidate);
        if (string.IsNullOrWhiteSpace(safeStem)) safeStem = reportId;
        return $"{safeStem}.docx";
    }
}
