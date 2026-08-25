using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using PEIS.Report.Contracts;

namespace PEIS.Report.Api.Printing;

/// <summary>
/// Durable print-job state. SQLite is deliberately used to keep a single-node Windows deployment
/// operational across API restarts without introducing another infrastructure component.
/// </summary>
public sealed class PrintJobStateStore
{
    private readonly string _connectionString;

    public PrintJobStateStore(IHostEnvironment environment, IOptions<PrintPersistenceOptions> options)
        : this(ResolveDatabasePath(environment.ContentRootPath, options.Value.DatabasePath))
    {
    }

    /// <summary>Test and maintenance constructor for an explicit isolated database path.</summary>
    public PrintJobStateStore(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        var directory = Path.GetDirectoryName(Path.GetFullPath(databasePath));
        if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = Path.GetFullPath(databasePath),
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared
        }.ToString();
        InitializeSchema();
    }

    public async Task InitializeAsync(PrintJobInitialization initialization, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(initialization);
        await using var connection = await OpenAsync(cancellationToken);
        using var transaction = connection.BeginTransaction();

        var job = initialization.Job;
        await ExecuteAsync(connection, transaction, """
            INSERT INTO PrintJobs(JobId, ActionCode, StationId, AgentId, JobName, IdempotencyKey, CreatedAt, UpdatedAt, RetryCount, LastError, CompletedAt)
            VALUES($jobId, $actionCode, $stationId, $agentId, $jobName, $idempotencyKey, $createdAt, $updatedAt, $retryCount, $lastError, $completedAt);
            """, cancellationToken,
            ("$jobId", job.JobId.ToString("N")),
            ("$actionCode", job.ActionCode),
            ("$stationId", job.StationId),
            ("$agentId", job.AgentId),
            ("$jobName", job.JobName),
            ("$idempotencyKey", job.IdempotencyKey),
            ("$createdAt", job.CreatedAt.UtcDateTime.ToString("O")),
            ("$updatedAt", job.UpdatedAt.UtcDateTime.ToString("O")),
            ("$retryCount", job.RetryCount),
            ("$lastError", job.LastError),
            ("$completedAt", job.CompletedAt?.UtcDateTime.ToString("O")));

        foreach (var target in initialization.Targets)
        {
            var result = target.Result;
            await ExecuteAsync(connection, transaction, """
                INSERT INTO PrintJobTargets(JobId, TargetId, AgentId, ArtifactId, DocumentKey, PrinterRole, PrinterName, Status, Message, CompletedAt, UpdatedAt)
                VALUES($jobId, $targetId, $agentId, $artifactId, $documentKey, $printerRole, $printerName, $status, $message, $completedAt, $updatedAt);
                """, cancellationToken,
                ("$jobId", result.JobId.ToString("N")),
                ("$targetId", result.TargetId.ToString("N")),
                ("$agentId", result.AgentId),
                ("$artifactId", target.ArtifactId.ToString("N")),
                ("$documentKey", result.DocumentKey),
                ("$printerRole", result.PrinterRole),
                ("$printerName", result.PrinterName),
                ("$status", result.Status.ToString()),
                ("$message", result.Message),
                ("$completedAt", result.CompletedAt?.UtcDateTime.ToString("O")),
                ("$updatedAt", job.UpdatedAt.UtcDateTime.ToString("O")));
        }

        await transaction.CommitAsync(cancellationToken);
    }

    public async Task MarkDispatchedAsync(Guid jobId, IEnumerable<Guid> targetIds, CancellationToken cancellationToken = default)
    {
        var targets = targetIds.Distinct().ToArray();
        if (targets.Length == 0) return;
        await using var connection = await OpenAsync(cancellationToken);
        using var transaction = connection.BeginTransaction();
        var now = DateTimeOffset.UtcNow.UtcDateTime.ToString("O");
        foreach (var targetId in targets)
        {
            await ExecuteAsync(connection, transaction, """
                UPDATE PrintJobTargets SET Status = 'Dispatched', UpdatedAt = $updatedAt
                WHERE JobId = $jobId AND TargetId = $targetId AND Status = 'Queued';
                """, cancellationToken,
                ("$updatedAt", now),
                ("$jobId", jobId.ToString("N")),
                ("$targetId", targetId.ToString("N")));
        }
        await ExecuteAsync(connection, transaction, "UPDATE PrintJobs SET UpdatedAt = $updatedAt WHERE JobId = $jobId;", cancellationToken,
            ("$updatedAt", now), ("$jobId", jobId.ToString("N")));
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<PrintStateTransitionResult> UpdateAsync(PrintTargetResult result, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        using var transaction = connection.BeginTransaction();

        var current = await ReadTargetAsync(connection, transaction, result.JobId, result.TargetId, cancellationToken);
        if (current is null)
            return new PrintStateTransitionResult(false, "Unknown print target.");
        if (!string.Equals(current.AgentId, result.AgentId, StringComparison.OrdinalIgnoreCase))
            return new PrintStateTransitionResult(false, "Agent does not own this print target.");
        if (!IsTransitionAllowed(current.Status, result.Status))
            return new PrintStateTransitionResult(false, $"Illegal state transition {current.Status} -> {result.Status}.");

        var now = DateTimeOffset.UtcNow;
        await ExecuteAsync(connection, transaction, """
            UPDATE PrintJobTargets
            SET Status = $status, Message = $message, CompletedAt = $completedAt, UpdatedAt = $updatedAt
            WHERE JobId = $jobId AND TargetId = $targetId;
            """, cancellationToken,
            ("$status", result.Status.ToString()),
            ("$message", result.Message),
            ("$completedAt", result.CompletedAt?.UtcDateTime.ToString("O")),
            ("$updatedAt", now.UtcDateTime.ToString("O")),
            ("$jobId", result.JobId.ToString("N")),
            ("$targetId", result.TargetId.ToString("N")));

        var completedAt = await GetJobCompletionAsync(connection, transaction, result.JobId, cancellationToken);
        await ExecuteAsync(connection, transaction, """
            UPDATE PrintJobs
            SET UpdatedAt = $updatedAt, LastError = CASE WHEN $lastError IS NULL THEN LastError ELSE $lastError END,
                CompletedAt = $completedAt
            WHERE JobId = $jobId;
            """, cancellationToken,
            ("$updatedAt", now.UtcDateTime.ToString("O")),
            ("$lastError", result.Status == PrintTargetStatus.Failed ? result.Message : null),
            ("$completedAt", completedAt?.UtcDateTime.ToString("O")),
            ("$jobId", result.JobId.ToString("N")));

        await transaction.CommitAsync(cancellationToken);
        return new PrintStateTransitionResult(true);
    }

    public async Task<IReadOnlyCollection<PrintTargetResult>?> GetAsync(Guid jobId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT TargetId, AgentId, DocumentKey, PrinterRole, PrinterName, Status, Message, CompletedAt
            FROM PrintJobTargets WHERE JobId = $jobId ORDER BY TargetId;
            """;
        command.Parameters.AddWithValue("$jobId", jobId.ToString("N"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var results = new List<PrintTargetResult>();
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(new PrintTargetResult(
                jobId,
                Guid.ParseExact(reader.GetString(0), "N"),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                Enum.Parse<PrintTargetStatus>(reader.GetString(5)),
                reader.IsDBNull(6) ? null : reader.GetString(6),
                reader.IsDBNull(7) ? null : DateTimeOffset.Parse(reader.GetString(7))));
        }
        return results.Count == 0 ? null : results;
    }

    public async Task<bool> IsArtifactAuthorizedForAgentAsync(Guid artifactId, string agentId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM PrintJobTargets WHERE ArtifactId = $artifactId AND AgentId = $agentId LIMIT 1;";
        command.Parameters.AddWithValue("$artifactId", artifactId.ToString("N"));
        command.Parameters.AddWithValue("$agentId", agentId);
        return (await command.ExecuteScalarAsync(cancellationToken)) is not null;
    }

    public async Task<bool> IsArtifactProtectedByActiveJobAsync(Guid artifactId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT 1 FROM PrintJobTargets
            WHERE ArtifactId = $artifactId AND Status NOT IN ('Completed', 'Failed') LIMIT 1;
            """;
        command.Parameters.AddWithValue("$artifactId", artifactId.ToString("N"));
        return (await command.ExecuteScalarAsync(cancellationToken)) is not null;
    }

    public async Task<IdempotencyReservation> ReserveIdempotencyAsync(string actionCode, string stationId, string idempotencyKey, TimeSpan reservationLifetime, CancellationToken cancellationToken = default)
    {
        var requestKey = MakeIdempotencyKey(actionCode, stationId, idempotencyKey);
        await using var connection = await OpenAsync(cancellationToken);
        using var transaction = connection.BeginTransaction();
        var existing = await ReadIdempotencyAsync(connection, transaction, requestKey, cancellationToken);
        var now = DateTimeOffset.UtcNow;

        if (existing is not null && string.Equals(existing.Status, "Completed", StringComparison.Ordinal))
        {
            await transaction.CommitAsync(cancellationToken);
            return new IdempotencyReservation(IdempotencyReservationStatus.Existing, new CreatePrintJobResponse(
                Guid.ParseExact(existing.JobId!, "N"), existing.DocumentCount!.Value, existing.TargetCount!.Value, existing.CreatedAt));
        }

        if (existing is not null && existing.CreatedAt.Add(reservationLifetime) > now)
        {
            await transaction.CommitAsync(cancellationToken);
            return new IdempotencyReservation(IdempotencyReservationStatus.Pending);
        }

        if (existing is not null)
        {
            await ExecuteAsync(connection, transaction, "DELETE FROM PrintIdempotency WHERE RequestKey = $requestKey;", cancellationToken,
                ("$requestKey", (object?)requestKey));
        }

        await ExecuteAsync(connection, transaction, """
            INSERT INTO PrintIdempotency(RequestKey, ActionCode, StationId, IdempotencyKey, JobId, DocumentCount, TargetCount, CreatedAt, Status)
            VALUES($requestKey, $actionCode, $stationId, $idempotencyKey, NULL, NULL, NULL, $createdAt, 'Reserved');
            """, cancellationToken,
            ("$requestKey", requestKey),
            ("$actionCode", actionCode.Trim()),
            ("$stationId", stationId.Trim()),
            ("$idempotencyKey", idempotencyKey.Trim()),
            ("$createdAt", now.UtcDateTime.ToString("O")));
        await transaction.CommitAsync(cancellationToken);
        return new IdempotencyReservation(IdempotencyReservationStatus.Acquired);
    }

    public async Task CompleteIdempotencyAsync(string actionCode, string stationId, string idempotencyKey, CreatePrintJobResponse response, CancellationToken cancellationToken = default)
    {
        var requestKey = MakeIdempotencyKey(actionCode, stationId, idempotencyKey);
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE PrintIdempotency
            SET JobId = $jobId, DocumentCount = $documentCount, TargetCount = $targetCount, CreatedAt = $createdAt, Status = 'Completed'
            WHERE RequestKey = $requestKey;
            """;
        command.Parameters.AddWithValue("$requestKey", requestKey);
        command.Parameters.AddWithValue("$jobId", response.JobId.ToString("N"));
        command.Parameters.AddWithValue("$documentCount", response.DocumentCount);
        command.Parameters.AddWithValue("$targetCount", response.TargetCount);
        command.Parameters.AddWithValue("$createdAt", response.CreatedAt.UtcDateTime.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task ReleaseIdempotencyAsync(string actionCode, string stationId, string idempotencyKey, CancellationToken cancellationToken = default)
    {
        var requestKey = MakeIdempotencyKey(actionCode, stationId, idempotencyKey);
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM PrintIdempotency WHERE RequestKey = $requestKey AND Status = 'Reserved';";
        command.Parameters.AddWithValue("$requestKey", requestKey);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public static bool IsTransitionAllowed(PrintTargetStatus current, PrintTargetStatus next)
        => current == next || (current, next) switch
        {
            (PrintTargetStatus.Queued, PrintTargetStatus.Dispatched or PrintTargetStatus.Failed) => true,
            (PrintTargetStatus.Dispatched, PrintTargetStatus.Downloading or PrintTargetStatus.Failed) => true,
            (PrintTargetStatus.Downloading, PrintTargetStatus.Printing or PrintTargetStatus.Failed) => true,
            (PrintTargetStatus.Printing, PrintTargetStatus.Completed or PrintTargetStatus.Failed) => true,
            _ => false
        };

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        return connection;
    }

    private static async Task<IdempotencyRow?> ReadIdempotencyAsync(SqliteConnection connection, SqliteTransaction transaction, string requestKey, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT JobId, DocumentCount, TargetCount, CreatedAt, Status FROM PrintIdempotency WHERE RequestKey = $requestKey;";
        command.Parameters.AddWithValue("$requestKey", requestKey);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        return new IdempotencyRow(
            reader.IsDBNull(0) ? null : reader.GetString(0),
            reader.IsDBNull(1) ? null : reader.GetInt32(1),
            reader.IsDBNull(2) ? null : reader.GetInt32(2),
            DateTimeOffset.Parse(reader.GetString(3)),
            reader.GetString(4));
    }

    private static async Task<TargetRow?> ReadTargetAsync(SqliteConnection connection, SqliteTransaction transaction, Guid jobId, Guid targetId, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT AgentId, Status FROM PrintJobTargets WHERE JobId = $jobId AND TargetId = $targetId;";
        command.Parameters.AddWithValue("$jobId", jobId.ToString("N"));
        command.Parameters.AddWithValue("$targetId", targetId.ToString("N"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new TargetRow(reader.GetString(0), Enum.Parse<PrintTargetStatus>(reader.GetString(1)))
            : null;
    }

    private static async Task<DateTimeOffset?> GetJobCompletionAsync(SqliteConnection connection, SqliteTransaction transaction, Guid jobId, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT COUNT(*), SUM(CASE WHEN Status IN ('Completed', 'Failed') THEN 1 ELSE 0 END)
            FROM PrintJobTargets WHERE JobId = $jobId;
            """;
        command.Parameters.AddWithValue("$jobId", jobId.ToString("N"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);
        return reader.GetInt64(0) > 0 && reader.GetInt64(0) == reader.GetInt64(1) ? DateTimeOffset.UtcNow : null;
    }

    private static async Task ExecuteAsync(SqliteConnection connection, SqliteTransaction transaction, string sql, CancellationToken cancellationToken, params (string Name, object? Value)[] values)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var (name, value) in values)
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private void InitializeSchema()
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode = WAL;
            PRAGMA foreign_keys = ON;
            CREATE TABLE IF NOT EXISTS PrintJobs(
                JobId TEXT PRIMARY KEY, ActionCode TEXT NULL, StationId TEXT NULL, AgentId TEXT NOT NULL,
                JobName TEXT NOT NULL, IdempotencyKey TEXT NULL, CreatedAt TEXT NOT NULL, UpdatedAt TEXT NOT NULL,
                RetryCount INTEGER NOT NULL DEFAULT 0, LastError TEXT NULL, CompletedAt TEXT NULL);
            CREATE TABLE IF NOT EXISTS PrintJobTargets(
                JobId TEXT NOT NULL, TargetId TEXT NOT NULL, AgentId TEXT NOT NULL, ArtifactId TEXT NOT NULL,
                DocumentKey TEXT NOT NULL, PrinterRole TEXT NOT NULL, PrinterName TEXT NOT NULL, Status TEXT NOT NULL,
                Message TEXT NULL, CompletedAt TEXT NULL, UpdatedAt TEXT NOT NULL,
                PRIMARY KEY(JobId, TargetId), FOREIGN KEY(JobId) REFERENCES PrintJobs(JobId));
            CREATE INDEX IF NOT EXISTS IX_PrintJobTargets_ArtifactAgent ON PrintJobTargets(ArtifactId, AgentId);
            CREATE TABLE IF NOT EXISTS PrintIdempotency(
                RequestKey TEXT PRIMARY KEY, ActionCode TEXT NOT NULL, StationId TEXT NOT NULL, IdempotencyKey TEXT NOT NULL,
                JobId TEXT NULL, DocumentCount INTEGER NULL, TargetCount INTEGER NULL, CreatedAt TEXT NOT NULL, Status TEXT NOT NULL);
            """;
        command.ExecuteNonQuery();
    }

    private static string ResolveDatabasePath(string contentRootPath, string configuredPath)
        => Path.IsPathFullyQualified(configuredPath) ? configuredPath : Path.Combine(contentRootPath, configuredPath);

    private static string MakeIdempotencyKey(string actionCode, string stationId, string idempotencyKey)
        => $"{actionCode.Trim().ToUpperInvariant()}:{stationId.Trim().ToUpperInvariant()}:{idempotencyKey.Trim()}";

    private sealed record TargetRow(string AgentId, PrintTargetStatus Status);
    private sealed record IdempotencyRow(string? JobId, int? DocumentCount, int? TargetCount, DateTimeOffset CreatedAt, string Status);
}
