using System.Collections.Concurrent;
using System.Data;
using System.Text.Json;
using PEIS.Report.Contracts;

namespace PEIS.Report.Engine;

/// <summary>
/// Immutable report metadata. Version is part of the cache key so deployments can invalidate a changed template
/// without sharing mutable FastReport instances across requests.
/// </summary>
public sealed record ReportDefinition(
    string ReportId,
    string Version,
    string TemplateKey,
    string? SqlText,
    IReadOnlyDictionary<string, string> ParameterMetadata,
    DateTimeOffset UpdatedAt,
    string Source,
    string? TemplateContent = null);

public sealed record ReportTemplate(string TemplateKey, string Version, string Content, string ContentHash);

/// <summary>DataSet and table metadata passed to the renderer; table names are part of the FRX compatibility contract.</summary>
public sealed record ReportDataSet(IReadOnlyDictionary<string, DataTable> Tables, int RowCount, DataSet? DataSet = null);

public interface IReportDefinitionProvider
{
    Task<ReportDefinition> GetRequiredAsync(ReportRenderRequest request, CancellationToken cancellationToken);
}

public interface ITemplateProvider
{
    Task<ReportTemplate> GetRequiredAsync(ReportDefinition definition, CancellationToken cancellationToken);
}

public interface IReportDataProvider
{
    /// <summary>Receives the complete request so database binding can use the untouched LegacyPayload.</summary>
    Task<ReportDataSet> QueryAsync(
        ReportDefinition definition,
        ReportRenderRequest request,
        CancellationToken cancellationToken);
}

/// <summary>
/// A cache of immutable metadata only. It deliberately never caches a mutable FastReport Report instance.
/// </summary>
public sealed class ReportDefinitionCache
{
    private readonly ConcurrentDictionary<string, Lazy<Task<ReportDefinition>>> _entries = new(StringComparer.OrdinalIgnoreCase);
    private long _hits;
    private long _misses;

    public async Task<ReportDefinition> GetOrCreateAsync(
        string reportId,
        Func<CancellationToken, Task<ReportDefinition>> factory,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reportId);
        // Metadata creation is shared. It must not inherit cancellation from whichever request first populates the cache.
        var created = new Lazy<Task<ReportDefinition>>(() => factory(CancellationToken.None), LazyThreadSafetyMode.ExecutionAndPublication);
        var entry = _entries.GetOrAdd(reportId, created);
        if (ReferenceEquals(entry, created))
            Interlocked.Increment(ref _misses);
        else
            Interlocked.Increment(ref _hits);

        try
        {
            return await entry.Value.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            if (_entries.TryGetValue(reportId, out var current) && ReferenceEquals(current, entry))
                _entries.TryRemove(reportId, out _);
            throw;
        }
    }

    public bool Invalidate(string cacheKey) => _entries.TryRemove(cacheKey, out _);

    /// <summary>Removes every versioned entry for one logical report without touching other report definitions.</summary>
    public int InvalidateReport(string reportId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reportId);
        var prefix = NormalizeReportId(reportId) + "|";
        var removed = 0;
        foreach (var key in _entries.Keys.Where(key => key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
        {
            if (_entries.TryRemove(key, out _))
                removed++;
        }
        return removed;
    }

    public static string BuildCacheKey(string reportId, ReportDefinitionVersion version)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reportId);
        ArgumentNullException.ThrowIfNull(version);
        return $"{NormalizeReportId(reportId)}|{version.CacheToken}";
    }

    private static string NormalizeReportId(string reportId) => reportId.Trim().ToUpperInvariant();

    public ReportDefinitionCacheSnapshot Snapshot() => new(
        Interlocked.Read(ref _hits),
        Interlocked.Read(ref _misses),
        _entries.Count);
}

public sealed record ReportDefinitionCacheSnapshot(long Hits, long Misses, int EntryCount);

/// <summary>
/// Deterministic provider for local development, CI, and environments without a supplied legacy database.
/// It is intentionally a provider implementation rather than a hidden fallback in business controllers.
/// </summary>
public sealed class DeterministicReportDefinitionProvider : IReportDefinitionProvider
{
    public Task<ReportDefinition> GetRequiredAsync(ReportRenderRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var parameter in request.Parameters)
            parameters[parameter.Key] = parameter.Value.ValueKind.ToString();

        return Task.FromResult(new ReportDefinition(
            request.ReportId,
            "deterministic-v1",
            request.ReportId,
            null,
            parameters,
            DateTimeOffset.UnixEpoch,
            "deterministic-development-provider"));
    }
}

public sealed class DeterministicTemplateProvider : ITemplateProvider
{
    public Task<ReportTemplate> GetRequiredAsync(ReportDefinition definition, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var content = $"deterministic-template:{definition.TemplateKey}:{definition.Version}";
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(content)));
        return Task.FromResult(new ReportTemplate(definition.TemplateKey, definition.Version, content, hash));
    }
}

public sealed class EmptyReportDataProvider : IReportDataProvider
{
    public Task<ReportDataSet> QueryAsync(
        ReportDefinition definition,
        ReportRenderRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new ReportDataSet(new Dictionary<string, DataTable>(), 0, new DataSet("DeterministicReportData")));
    }
}
