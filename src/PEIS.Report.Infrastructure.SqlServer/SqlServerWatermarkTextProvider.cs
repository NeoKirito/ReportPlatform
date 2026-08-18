using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PEIS.Report.Engine;

namespace PEIS.Report.Infrastructure.SqlServer;

/// <summary>
/// Connection and cache settings for the read-only maintenance-database lookup of the institution watermark text.
/// Configure an independent connection when the maintenance database is separate from the legacy report database.
/// </summary>
public sealed class WatermarkDatabaseOptions
{
    public string ConnectionString { get; set; } = string.Empty;
    public int CommandTimeoutSeconds { get; set; } = 30;
    public int CacheTtlSeconds { get; set; } = 3600;
}

/// <summary>
/// Resolves <c>dbo.qx_hospital.jgmc</c> as the authoritative institution watermark. The evidenced table contains one
/// row in the approved maintenance database; the provider nevertheless uses TOP (1) with hospitalid ordering so a
/// misconfigured multi-row database has deterministic, read-only behavior.
/// </summary>
public sealed class SqlServerWatermarkTextProvider : IWatermarkTextProvider, IDisposable
{
    private const string Query = "SELECT TOP (1) jgmc FROM dbo.qx_hospital ORDER BY hospitalid ASC;";
    private readonly WatermarkDatabaseOptions _database;
    private readonly ReportDatabaseOptions _reportDatabase;
    private readonly ILogger<SqlServerWatermarkTextProvider> _logger;
    private readonly TimeProvider _clock;
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private CacheEntry? _cache;
    private int _disposed;

    public SqlServerWatermarkTextProvider(
        IOptions<WatermarkDatabaseOptions> database,
        IOptions<ReportDatabaseOptions> reportDatabase,
        ILogger<SqlServerWatermarkTextProvider>? logger = null,
        TimeProvider? clock = null)
    {
        _database = database.Value;
        _reportDatabase = reportDatabase.Value;
        _logger = logger ?? NullLogger<SqlServerWatermarkTextProvider>.Instance;
        _clock = clock ?? TimeProvider.System;
    }

    public async Task<string?> GetWatermarkTextAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        var now = _clock.GetUtcNow();
        var cached = Volatile.Read(ref _cache);
        if (cached is not null && cached.ExpiresAt > now)
            return cached.Text;

        await _refreshGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            now = _clock.GetUtcNow();
            cached = Volatile.Read(ref _cache);
            if (cached is not null && cached.ExpiresAt > now)
                return cached.Text;

            var text = await ReadWatermarkTextAsync(cancellationToken).ConfigureAwait(false);
            Volatile.Write(ref _cache, new CacheEntry(text, now.AddSeconds(CacheTtlSeconds())));
            return text;
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    private async Task<string?> ReadWatermarkTextAsync(CancellationToken cancellationToken)
    {
        var connectionString = string.IsNullOrWhiteSpace(_database.ConnectionString)
            ? _reportDatabase.ConnectionString
            : _database.ConnectionString;
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            _logger.LogWarning("Watermark is enabled but neither WatermarkDatabase:ConnectionString nor ReportDatabase:ConnectionString is configured; watermark rendering will be skipped.");
            return null;
        }

        try
        {
            await using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = Query;
            command.CommandTimeout = Math.Clamp(_database.CommandTimeoutSeconds, 1, 300);
            var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            return value is null or DBNull ? null : Normalize(Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture));
        }
        catch (SqlException exception) when (exception.Number == -2)
        {
            _logger.LogWarning(exception, "Timed out reading qx_hospital.jgmc; watermark rendering will be skipped until the next cache refresh.");
            return null;
        }
        catch (SqlException exception)
        {
            _logger.LogWarning(exception, "Unable to read qx_hospital.jgmc; watermark rendering will be skipped until the next cache refresh.");
            return null;
        }
    }

    private int CacheTtlSeconds() => Math.Clamp(_database.CacheTtlSeconds, 1, 86_400);

    private static string? Normalize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;
        return text.Trim();
    }

    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref _disposed) != 0)
            throw new ObjectDisposedException(nameof(SqlServerWatermarkTextProvider));
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
            _refreshGate.Dispose();
    }

    private sealed record CacheEntry(string? Text, DateTimeOffset ExpiresAt);
}
