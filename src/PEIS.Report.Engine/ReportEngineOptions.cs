namespace PEIS.Report.Engine;

public sealed class ReportEngineOptions
{
    /// <summary>Supported values: Deterministic and LegacySqlServer.</summary>
    public string DefinitionSource { get; set; } = "Deterministic";

    public bool UsesLegacySqlServer => string.Equals(DefinitionSource, "LegacySqlServer", StringComparison.OrdinalIgnoreCase);
}
