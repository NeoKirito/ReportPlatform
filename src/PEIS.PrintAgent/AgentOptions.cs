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
    /// Stable PEIS workstation/station code, e.g. REG-01. PEIS sends this code with a print action.
    /// Configure once per workstation; users do not choose printers for each print.
    /// </summary>
    public string StationId { get; set; } = Environment.MachineName;

    /// <summary>
    /// Logical role -> Windows printer name.
    /// Example A4_GUIDE -> HP LaserJet..., BARCODE -> TSC TE244.
    /// </summary>
    public Dictionary<string, string> PrinterBindings { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public int HeartbeatSeconds { get; set; } = 20;
    public string WorkDirectory { get; set; } = ".runtime/print-agent";
    public PrintBackendOptions PrintBackend { get; set; } = new();
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
