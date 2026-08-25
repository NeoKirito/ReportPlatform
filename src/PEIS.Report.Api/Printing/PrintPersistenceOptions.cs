using PEIS.Report.Contracts;

namespace PEIS.Report.Api.Printing;

/// <summary>
/// Local durable storage for print workflow state. The default lives below the service content root
/// and can be backed up with the deployed application without adding a server dependency.
/// </summary>
public sealed class PrintPersistenceOptions
{
    public string DatabasePath { get; set; } = ".runtime/print-state.db";

    /// <summary>How long an unfinished idempotency reservation may remain before a retry may claim it.</summary>
    public int IdempotencyReservationSeconds { get; set; } = 120;
}

/// <summary>Retention and capacity bounds for server-side rendered PDF artifacts.</summary>
public sealed class PdfArtifactStoreOptions
{
    public int RetentionHours { get; set; } = 24;
    public int CleanupIntervalMinutes { get; set; } = 30;
    public long MaxArtifactBytes { get; set; } = 1_073_741_824;
    public int MaxArtifactCount { get; set; } = 10_000;
}

/// <summary>
/// Guards diagnostic and artifact endpoints until an application-wide user authentication scheme exists.
/// Supply the token through environment configuration, never source control.
/// </summary>
public sealed class InternalApiSecurityOptions
{
    public const string HeaderName = "X-PEIS-Internal-Token";
    public string? AccessToken { get; set; }
    public bool AllowInsecureDevelopment { get; set; }
}

/// <summary>Signs short-lived, agent-bound PDF download URLs generated only by the API.</summary>
public sealed class ArtifactAccessOptions
{
    public string? SigningKey { get; set; }
    public int DownloadLifetimeMinutes { get; set; } = 10;
    public bool AllowInsecureDevelopment { get; set; }
}

public sealed record PrintJobRecord(
    Guid JobId,
    string? ActionCode,
    string? StationId,
    string AgentId,
    string JobName,
    string? IdempotencyKey,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    int RetryCount,
    string? LastError,
    DateTimeOffset? CompletedAt);

public sealed record PrintJobTargetState(PrintTargetResult Result, Guid ArtifactId);

public sealed record PrintJobInitialization(
    PrintJobRecord Job,
    IReadOnlyCollection<PrintJobTargetState> Targets);

public sealed record PrintJobSnapshot(PrintJobRecord Job, IReadOnlyCollection<PrintJobTargetState> Targets);

public enum IdempotencyReservationStatus
{
    Acquired,
    Existing,
    Pending
}

public sealed record IdempotencyReservation(
    IdempotencyReservationStatus Status,
    CreatePrintJobResponse? ExistingResponse = null);

public sealed record PdfArtifactMetadata(Guid ArtifactId, string FileName, long Length, DateTimeOffset CreatedAt);

public sealed record PdfArtifactCleanupResult(int DeletedCount, long DeletedBytes, string? Error = null);

public sealed record ArtifactAccessAudit(Guid ArtifactId, DateTimeOffset AccessedAt, bool Granted, string? Detail = null);

public sealed record PrintStateTransitionResult(bool Applied, string? Reason = null);

public sealed record PrintArtifactCapacity(long TotalBytes, int Count);

public sealed record PrintProductionWarning(string Code, string Message);

public sealed record PrintProductionReadiness(bool Ready, IReadOnlyCollection<PrintProductionWarning> Warnings);

public sealed record PrintJobStatusSummary(Guid JobId, int TotalTargets, int CompletedTargets, int FailedTargets, bool IsTerminal);

public sealed record PrintStateStoreRestartEvidence(Guid JobId, string IdempotencyKey, bool JobRecovered, bool IdempotencyRecovered);

public sealed record PrintArtifactCleanupEvidence(Guid ArtifactId, bool RetainedForActiveJob, bool DeletedWhenExpired); 
