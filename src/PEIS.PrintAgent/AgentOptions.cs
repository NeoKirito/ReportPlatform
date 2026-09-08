namespace PEIS.PrintAgent;

public sealed class AgentOptions
{
    public string ServerUrl { get; set; } = "http://127.0.0.1:5080";
    /// <summary>
    /// Optional migration override. When blank, the agent generates and persists an installation GUID under ProgramData.
    /// </summary>
    public string AgentId { get; set; } = string.Empty;

    /// <summary>
    /// Per-installation token provisioned by the server administrator. Leave blank only while server-side registration
    /// authentication is disabled for backwards-compatible pilot deployments.
    /// </summary>
    public string? RegistrationToken { get; set; }

    /// <summary>
    /// Optional installation label. Blank or AUTO uses the Windows machine name. Normal preview requests are routed
    /// by the browser/Agent source address and therefore do not need to know this value.
    /// </summary>
    public string StationId { get; set; } = string.Empty;

    /// <summary>
    /// Logical role -> Windows printer name.
    /// Example A4_GUIDE -> HP LaserJet..., BARCODE -> TSC TE244.
    /// </summary>
    public Dictionary<string, string> PrinterBindings { get; set; } = new(StringComparer.OrdinalIgnoreCase);

        public int HeartbeatSeconds { get; set; } = 20;

    /// <summary>
    /// Maximum number of different PDF artifacts downloaded concurrently for one B/S print action.
    /// Two is sufficient for the usual A4 report plus barcode-label case while preserving workstation bandwidth.
    /// </summary>
    public int MaxConcurrentDownloads { get; set; } = 2;

    public string WorkDirectory { get; set; } = ".runtime/print-agent";

    public PrintBackendOptions PrintBackend { get; set; } = new();

    public PreviewOptions Preview { get; set; } = new();

    public DeliveryPrintingOptions Printing { get; set; } = new();
}

public sealed class DeliveryPrintingOptions
{
    /// <summary>
    /// When true, printable desktop deliveries bypass the preview window and are sent directly to the remembered
    /// Djid printer. The configured/default Windows printer is remembered on the first silent print.
    /// </summary>
    public bool Silent { get; set; }

    /// <summary>Optional first-use fallback. Blank uses the Windows default printer.</summary>
    public string? DefaultPrinter { get; set; }
}

public sealed class PreviewOptions
{
    /// <summary>Only the new ReportDelivery message uses this setting; legacy printing is unaffected.</summary>
    public bool Enabled { get; set; } = true;
    public int WindowWidth { get; set; } = 1100;
    public int WindowHeight { get; set; } = 820;
}

public sealed class PrintBackendOptions
{
    public string Mode { get; set; } = "DryRun";
    public string? Executable { get; set; }
    public string ArgumentsTemplate { get; set; } = "{file} {printer} {copies}";
    /// <summary>Additional attempts after the initial print command.</summary>
    public int RetryCount { get; set; } = 1;
    /// <summary>Delay between transient backend/spooler retries.</summary>
    public int RetryDelaySeconds { get; set; } = 2;
}
