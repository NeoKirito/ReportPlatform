namespace PEIS.Report.Api.Printing;

public sealed class PrintRoutingOptions
{
    public Dictionary<string, PrintScenarioOptions> Scenarios { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class PrintScenarioOptions
{
    public string JobName { get; set; } = "PEIS打印";
    public List<PrintScenarioDocumentOptions> Documents { get; set; } = [];
}

public sealed class PrintScenarioDocumentOptions
{
    public string Key { get; set; } = "document";
    public string ReportId { get; set; } = "";
    public string PrinterRole { get; set; } = "";
    public string Profile { get; set; } = "print";
    public int Copies { get; set; } = 1;
    public bool Duplex { get; set; }
    public bool WatermarkEnabled { get; set; }
    public string? WatermarkText { get; set; }
    public string? FileName { get; set; }
}
