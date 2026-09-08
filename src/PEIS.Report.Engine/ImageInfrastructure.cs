using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;

namespace PEIS.Report.Engine;

public sealed class ImageResolutionOptions
{
    public int MaxConcurrentFetches { get; set; } = 12;
    public int TimeoutSeconds { get; set; } = 3;
    public int TimeoutMilliseconds { get; set; } = 150;
    public int MaxCachedItems { get; set; } = 1024;
    public int FailureCacheSeconds { get; set; } = 3600;

    public TimeSpan EffectiveTimeout => TimeoutMilliseconds > 0
        ? TimeSpan.FromMilliseconds(TimeoutMilliseconds)
        : TimeSpan.FromSeconds(Math.Max(0.1, TimeoutSeconds));
}

public sealed record ResolvedImage(
    string Source,
    byte[] Bytes,
    string ContentHash,
    bool FromCache,
    long FetchMilliseconds);

public sealed record ImageResolveBatch(
    IReadOnlyDictionary<string, ResolvedImage> Images,
    int CacheHits,
    int FailureCount,
    long TotalBytes,
    long ElapsedMilliseconds);

public interface IImageResolver
{
    Task<ImageResolveBatch> ResolveAsync(IEnumerable<Uri> sources, CancellationToken cancellationToken);
}

/// <summary>
/// LRU byte[] cache with thread-safe access. Evicts least-recently-used entries when capacity is reached.
/// </summary>
internal sealed class LruImageCache
{
    private readonly int _capacity;
    private readonly Dictionary<string, LinkedListNode<(string Key, byte[] Value)>> _map = new(StringComparer.Ordinal);
    private readonly LinkedList<(string Key, byte[] Value)> _order = new();
    private readonly object _lock = new();

    public LruImageCache(int capacity) => _capacity = Math.Max(1, capacity);

    public byte[]? Get(string key)
    {
        lock (_lock)
        {
            if (_map.TryGetValue(key, out var node))
            {
                _order.Remove(node);
                _order.AddLast(node);
                return node.Value.Value;
            }
            return null;
        }
    }

    public void Add(string key, byte[] value)
    {
        lock (_lock)
        {
            if (_map.TryGetValue(key, out var existing))
            {
                _order.Remove(existing);
                existing.Value = (key, value);
                _order.AddLast(existing);
                return;
            }
            if (_map.Count >= _capacity)
            {
                var oldest = _order.First;
                if (oldest is not null)
                {
                    _order.RemoveFirst();
                    _map.Remove(oldest.Value.Key);
                }
            }
            var node = new LinkedListNode<(string, byte[])>((key, value));
            _order.AddLast(node);
            _map[key] = node;
        }
    }

    public int Count { get { lock (_lock) { return _map.Count; } } }
}

/// <summary>
/// Reuses a single HttpClient, deduplicates requested URLs before fetching, and bounds concurrent I/O. The cache
/// stores immutable byte arrays by canonical URI; a FastReport integration may consume these bytes before Prepare.
/// </summary>
public sealed class ImageResolver : IImageResolver
{
    private readonly HttpClient _httpClient;
    private readonly ImageResolutionOptions _options;
    private readonly SemaphoreSlim _gate;
    private readonly LruImageCache _cache;
    private readonly ConcurrentDictionary<string, DateTimeOffset> _failureCache = new(StringComparer.Ordinal);

    public ImageResolver(HttpClient httpClient, ImageResolutionOptions options)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        if (_options.MaxConcurrentFetches is < 1 or > 64)
            throw new ArgumentOutOfRangeException(nameof(options.MaxConcurrentFetches));
        if (_options.TimeoutSeconds is < 0 or > 120)
            throw new ArgumentOutOfRangeException(nameof(options.TimeoutSeconds));
        if (_options.TimeoutMilliseconds is < 0 or > 120000)
            throw new ArgumentOutOfRangeException(nameof(options.TimeoutMilliseconds));
        if (_options.FailureCacheSeconds is < 1 or > 86400)
            throw new ArgumentOutOfRangeException(nameof(options.FailureCacheSeconds));
        _cache = new LruImageCache(_options.MaxCachedItems);
        _gate = new SemaphoreSlim(_options.MaxConcurrentFetches, _options.MaxConcurrentFetches);
    }

    public async Task<ImageResolveBatch> ResolveAsync(IEnumerable<Uri> sources, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sources);
        var total = Stopwatch.StartNew();
        var unique = sources
            .Where(uri => uri is { IsAbsoluteUri: true })
            .GroupBy(uri => uri.AbsoluteUri, StringComparer.Ordinal)
            .Select(group => group.First())
            .ToArray();
        var results = new ConcurrentDictionary<string, ResolvedImage>(StringComparer.Ordinal);
        var cacheHits = 0;
        var failures = 0;

        await Parallel.ForEachAsync(
            unique,
            new ParallelOptions
            {
                CancellationToken = cancellationToken,
                MaxDegreeOfParallelism = _options.MaxConcurrentFetches
            },
            async (source, token) =>
            {
                try
                {
                    var image = await ResolveOneAsync(source, token).ConfigureAwait(false);
                    if (image.FromCache) Interlocked.Increment(ref cacheHits);
                    results[source.AbsoluteUri] = image;
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    throw;
                }
                catch
                {
                    Interlocked.Increment(ref failures);
                }
            }).ConfigureAwait(false);

        total.Stop();
        return new ImageResolveBatch(
            results,
            cacheHits,
            failures,
            results.Values.Sum(image => (long)image.Bytes.Length),
            total.ElapsedMilliseconds);
    }

    private async Task<ResolvedImage> ResolveOneAsync(Uri source, CancellationToken cancellationToken)
    {
        var key = source.AbsoluteUri;
        var cached = _cache.Get(key);
        if (cached is not null)
            return new ResolvedImage(key, cached, Hash(cached), true, 0);
        ThrowIfRecentlyFailed(key);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cached = _cache.Get(key);
            if (cached is not null)
                return new ResolvedImage(key, cached, Hash(cached), true, 0);
            ThrowIfRecentlyFailed(key);

            try
            {
                var timer = Stopwatch.StartNew();
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(_options.EffectiveTimeout);
                using var response = await _httpClient.GetAsync(source, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                var bytes = await response.Content.ReadAsByteArrayAsync(timeout.Token).ConfigureAwait(false);
                timer.Stop();

                _cache.Add(key, bytes);
                _failureCache.TryRemove(key, out _);
                return new ResolvedImage(key, bytes, Hash(bytes), false, timer.ElapsedMilliseconds);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                _failureCache[key] = DateTimeOffset.UtcNow.AddSeconds(_options.FailureCacheSeconds);
                throw;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private void ThrowIfRecentlyFailed(string key)
    {
        if (!_failureCache.TryGetValue(key, out var expiresAt)) return;
        if (expiresAt > DateTimeOffset.UtcNow)
            throw new HttpRequestException($"Image source '{key}' is temporarily unavailable (cached failure).");
        _failureCache.TryRemove(key, out _);
    }

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
}
