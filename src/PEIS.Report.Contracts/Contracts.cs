using System.Text.Json;

namespace PEIS.Report.Contracts;

public sealed record WatermarkOptions(
    bool Enabled = true,
    string? Text = null,
    double Opacity = 0.12,
    double Angle = -30);

/// <summary>
/// The report request passed across the API/engine boundary. For legacy callers, <see cref="LegacyPayload"/>
/// is the source of truth; the typed fields are diagnostic conveniences only and must never discard raw data.
/// </summary>
public sealed record ReportRenderRequest(
    string ReportId,
    Dictionary<string, JsonElement> Parameters,
    string Profile = "screen",
    WatermarkOptions? Watermark = null,
    string? FileName = null,
    JsonElement? LegacyPayload = null);

public sealed record PrinterDescriptor(
    string Name,
    bool IsDefault,
    string? DriverName = null,
    string? PortName = null);

/// <summary>
/// A logical printer role is stable business configuration (for example A4_GUIDE or BARCODE).
/// The physical Windows printer name may change without changing PEIS business code.
/// </summary>
public sealed record AgentRegistration(
    string AgentId,
    string StationId,
    string MachineName,
    IReadOnlyList<PrinterDescriptor> Printers,
    IReadOnlyDictionary<string, string> PrinterBindings,
    string Version);

/// <summary>
/// Diagnostic/manual API target. Production B/S should normally call <see cref="BusinessPrintRequest"/> instead.
/// </summary>
public sealed record PrintTarget(
    string AgentId,
    string PrinterName,
    int Copies = 1,
    bool Duplex = false);

public sealed record CreatePrintJobRequest(
    ReportRenderRequest Report,
    IReadOnlyList<PrintTarget> Targets,
    string? JobName = null);

/// <summary>
/// Primary B/S contract: one business action, no physical printer names.
/// <paramref name="IdempotencyKey"/> is optional for backwards compatibility but callers should send a stable
/// business-operation key when browser/network retries are possible.
/// </summary>
public sealed record BusinessPrintRequest(
    string ActionCode,
    string StationId,
    Dictionary<string, JsonElement> Parameters,
    string? JobName = null,
    string? IdempotencyKey = null);

public sealed record CreatePrintJobResponse(
    Guid JobId,
    int DocumentCount,
    int TargetCount,
    DateTimeOffset CreatedAt);

/// <summary>
/// One rendered document routed to one physical printer. Different documents in the same business action may use
/// different artifacts and physical printers.
/// </summary>
public sealed record PrintDocumentDispatch(
    Guid TargetId,
    Guid ArtifactId,
    string DownloadPath,
    string DocumentKey,
    string ReportId,
    string PrinterRole,
    string PrinterName,
    int Copies,
    bool Duplex);

/// <summary>
/// A workstation receives one batch containing all outputs for one B/S click. It downloads each distinct artifact
/// once, then queues each document to its mapped printer.
/// </summary>
public sealed record PrintBatchDispatch(
    Guid JobId,
    string JobName,
    IReadOnlyList<PrintDocumentDispatch> Documents);

public enum PrintTargetStatus
{
    Queued,
    Downloading,
    Printing,
    Completed,
    Failed
}

public sealed record PrintTargetResult(
    Guid JobId,
    Guid TargetId,
    string AgentId,
    string DocumentKey,
    string PrinterRole,
    string PrinterName,
    PrintTargetStatus Status,
    string? Message = null,
    DateTimeOffset? CompletedAt = null);
