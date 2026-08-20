using PEIS.Report.Engine;

namespace PEIS.Report.Docx.OpenXml;

/// <summary>Immutable definition of a Word template. Coordinates are in points and use the physical page origin.</summary>
public sealed record DocxTemplateDefinition(
    string TemplateKey,
    string Version,
    DocxTemplatePage Page,
    IReadOnlyList<DocxTemplateElement> Elements);

public sealed record DocxTemplatePage(double WidthMillimeters, double HeightMillimeters);

/// <summary>
/// A template element binds its text from a <see cref="ReportDataSet"/> at render time. Binding paths are
/// Table.Column, for example Master.xm; literal text has no dot. Coordinate elements are independent so a document's
/// layout changes through template metadata, rather than report-specific renderer code.
/// </summary>
public abstract record DocxTemplateElement(
    string ElementId,
    double LeftPoints,
    double TopPoints,
    double WidthPoints,
    double HeightPoints,
    int ZIndex);

public sealed record DocxTextElement(
    string ElementId,
    double LeftPoints,
    double TopPoints,
    double WidthPoints,
    double HeightPoints,
    int ZIndex,
    string Expression,
    string FontFamily,
    double FontPoints,
    bool Bold = false,
    DocxHorizontalAlignment Alignment = DocxHorizontalAlignment.Left)
    : DocxTemplateElement(ElementId, LeftPoints, TopPoints, WidthPoints, HeightPoints, ZIndex);

public sealed record DocxBarcodeElement(
    string ElementId,
    double LeftPoints,
    double TopPoints,
    double WidthPoints,
    double HeightPoints,
    int ZIndex,
    string Expression,
    DocxBarcodeFormat Format = DocxBarcodeFormat.Code128)
    : DocxTemplateElement(ElementId, LeftPoints, TopPoints, WidthPoints, HeightPoints, ZIndex);

public enum DocxHorizontalAlignment
{
    Left,
    Center,
    Right
}

public enum DocxBarcodeFormat
{
    Code128
}

public interface IDocxTemplateProvider
{
    Task<DocxTemplateDefinition> GetRequiredAsync(ReportDefinition definition, CancellationToken cancellationToken);
}

public sealed class InMemoryDocxTemplateProvider : IDocxTemplateProvider
{
    private readonly IReadOnlyDictionary<string, DocxTemplateDefinition> _templates;

    public InMemoryDocxTemplateProvider(IEnumerable<DocxTemplateDefinition> templates)
    {
        ArgumentNullException.ThrowIfNull(templates);
        _templates = templates.ToDictionary(x => x.TemplateKey, StringComparer.OrdinalIgnoreCase);
    }

    public Task<DocxTemplateDefinition> GetRequiredAsync(ReportDefinition definition, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(definition);
        cancellationToken.ThrowIfCancellationRequested();
        if (_templates.TryGetValue(definition.TemplateKey, out var template) ||
            _templates.TryGetValue(definition.ReportId, out template))
            return Task.FromResult(template);
        throw new KeyNotFoundException($"No DOCX template is registered for report '{definition.ReportId}' (template key '{definition.TemplateKey}').");
    }
}
