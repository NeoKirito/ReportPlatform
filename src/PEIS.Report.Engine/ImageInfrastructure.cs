using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;

namespace PEIS.Report.Engine;

public sealed class ImageResolutionOptions
{
    public int MaxConcurrentFetches { get; set; } = 4;
    public int TimeoutSeconds { get; set; } = 15;
    public int MaxCachedItems { get; set; } = 256;
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
/// Reuses a single HttpClient, deduplicates requested URLs before fetching, and bounds concurrent I/O. The cache
/// stores immutable byte arrays by canonical URI; a FastReport integration may consume these bytes before Prepare.
/// </summary>
public sealed class ImageResolver : IImageResolver
{
    private readonly HttpClient _httpClient;
    private readonly ImageResolutionOptions _options;
    private readonly SemaphoreSlim _gate;
    private readonly ConcurrentDictionary<string, byte[]> _cache = new(StringComparer.Ordinal);

    public ImageResolver(HttpClient httpClient, ImageResolutionOptions options)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        if (_options.MaxConcurrentFetches is < 1 or > 64)
            throw new ArgumentOutOfRangeException(nameof(options.MaxConcurrentFetches));
        if (_options.TimeoutSeconds is < 1 or > 120)
            throw new ArgumentOutOfRangeException(nameof(options.TimeoutSeconds));
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
        if (_cache.TryGetValue(key, out var cached))
            return new ResolvedImage(key, cached, Hash(cached), true, 0);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_cache.TryGetValue(key, out cached))
                return new ResolvedImage(key, cached, Hash(cached), true, 0);

            var timer = Stopwatch.StartNew();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(_options.TimeoutSeconds));
            using var response = await _httpClient.GetAsync(source, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var bytes = await response.Content.ReadAsByteArrayAsync(timeout.Token).ConfigureAwait(false);
            timer.Stop();

            if (_cache.Count >= _options.MaxCachedItems)
                _cache.TryRemove(_cache.Keys.OrderBy(key => key, StringComparer.Ordinal).FirstOrDefault() ?? string.Empty, out _);
            _cache.TryAdd(key, bytes);
            return new ResolvedImage(key, bytes, Hash(bytes), false, timer.ElapsedMilliseconds);
        }
        finally
        {
            _gate.Release();
        }
    }

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
}
