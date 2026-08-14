namespace PEIS.Report.Engine;

public sealed class ReportEngineOptions
{
    /// <summary>Supported values: Deterministic and LegacySqlServer.</summary>
    public string DefinitionSource { get; set; } = "Deterministic";

    /// <summary>Supported values: Stub and FastReportOpenSource.</summary>
    public string Renderer { get; set; } = "Stub";

    public bool UsesLegacySqlServer => string.Equals(DefinitionSource, "LegacySqlServer", StringComparison.OrdinalIgnoreCase);
    public bool UsesFastReportOpenSource => string.Equals(Renderer, "FastReportOpenSource", StringComparison.OrdinalIgnoreCase);
}
