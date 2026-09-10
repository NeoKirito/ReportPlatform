using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;

namespace PEIS.Report.Engine;

/// <summary>
/// 图片解析选项配置。
/// </summary>
public sealed class ImageResolutionOptions
{
    /// <summary>最大并发下载数，默认12</summary>
    public int MaxConcurrentFetches { get; set; } = 12;
    /// <summary>超时秒数（备用，默认3秒）</summary>
    public int TimeoutSeconds { get; set; } = 3;
    /// <summary>超时毫秒数（优先使用，默认150ms）</summary>
    public int TimeoutMilliseconds { get; set; } = 150;
    /// <summary>LRU缓存最大条目数，默认1024</summary>
    public int MaxCachedItems { get; set; } = 1024;
    /// <summary>失败缓存时间（秒），默认3600秒（1小时）</summary>
    public int FailureCacheSeconds { get; set; } = 3600;

    /// <summary>计算有效超时时间</summary>
    public TimeSpan EffectiveTimeout => TimeoutMilliseconds > 0
        ? TimeSpan.FromMilliseconds(TimeoutMilliseconds)
        : TimeSpan.FromSeconds(Math.Max(0.1, TimeoutSeconds));
}

/// <summary>
/// 已解析的图片：包含来源URL、字节内容、哈希、是否缓存命中、耗时。
/// </summary>
public sealed record ResolvedImage(
    string Source,        // 图片URL
    byte[] Bytes,         // 图片字节内容
    string ContentHash,   // 内容SHA256哈希
    bool FromCache,       // 是否从缓存加载
    long FetchMilliseconds); // 下载耗时（毫秒）

/// <summary>
/// 图片解析批次结果：包含所有已解析的图片、缓存命中数、失败数、总字节数、总耗时。
/// </summary>
public sealed record ImageResolveBatch(
    IReadOnlyDictionary<string, ResolvedImage> Images, // URL → 已解析图片
    int CacheHits,        // 缓存命中数
    int FailureCount,     // 失败数
    long TotalBytes,      // 总字节数
    long ElapsedMilliseconds); // 总耗时（毫秒）

/// <summary>
/// 图片解析器接口：批量解析URL列表，返回图片字节数组。
/// </summary>
public interface IImageResolver
{
    Task<ImageResolveBatch> ResolveAsync(IEnumerable<Uri> sources, CancellationToken cancellationToken);
}

/// <summary>
/// LRU（最近最少使用）图片缓存。
/// 
/// 特点：
/// - 线程安全（lock实现）
/// - 按访问顺序维护链表
/// - 容量满时淘汰最久未访问的条目
/// - 支持更新已有条目
/// 
/// 用于缓存已下载的图片字节数组，避免重复下载。
/// </summary>
internal sealed class LruImageCache
{
    private readonly int _capacity;
    private readonly Dictionary<string, LinkedListNode<(string Key, byte[] Value)>> _map = new(StringComparer.Ordinal);
    private readonly LinkedList<(string Key, byte[] Value)> _order = new();
    private readonly object _lock = new();

    public LruImageCache(int capacity) => _capacity = Math.Max(1, capacity);

    /// <summary>
    /// 获取缓存条目：命中时将条目移到链表末尾（标记为最近使用）。
    /// </summary>
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

    /// <summary>
    /// 添加缓存条目：
    /// - 如果已存在，更新值并移到末尾
    /// - 如果容量满，淘汰链表头部（最久未访问）的条目
    /// - 添加到链表末尾
    /// </summary>
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
/// 图片解析器实现：
/// - 复用单个HttpClient
/// - 批量请求前去重
/// - 并发控制（SemaphoreSlim）
/// - LRU缓存
/// - 失败缓存（避免重复请求已知失败的URL）
/// - 超时控制
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

    /// <summary>
    /// 批量解析图片URL：
    /// 1. 过滤无效URL并去重
    /// 2. 并行下载（受SemaphoreSlim控制）
    /// 3. 统计缓存命中、失败数
    /// </summary>
    public async Task<ImageResolveBatch> ResolveAsync(IEnumerable<Uri> sources, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sources);
        var total = Stopwatch.StartNew();

        // 去重：按AbsoluteUri分组，每组只取第一个
        var unique = sources
            .Where(uri => uri is { IsAbsoluteUri: true })
            .GroupBy(uri => uri.AbsoluteUri, StringComparer.Ordinal)
            .Select(group => group.First())
            .ToArray();

        var results = new ConcurrentDictionary<string, ResolvedImage>(StringComparer.Ordinal);
        var cacheHits = 0;
        var failures = 0;

        // 并行下载
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

    /// <summary>
    /// 解析单个图片URL：
    /// 1. 检查LRU缓存
    /// 2. 检查失败缓存
    /// 3. 获取并发控制信号量
    /// 4. 双重检查缓存（避免重复下载）
    /// 5. 带超时下载
    /// 6. 缓存结果
    /// </summary>
    private async Task<ResolvedImage> ResolveOneAsync(Uri source, CancellationToken cancellationToken)
    {
        var key = source.AbsoluteUri;

        // 第一次检查缓存（无需获取信号量）
        var cached = _cache.Get(key);
        if (cached is not null)
            return new ResolvedImage(key, cached, Hash(cached), true, 0);
        ThrowIfRecentlyFailed(key);

        // 获取并发控制信号量
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // 第二次检查缓存（可能在等待信号量时被其他线程加载）
            cached = _cache.Get(key);
            if (cached is not null)
                return new ResolvedImage(key, cached, Hash(cached), true, 0);
            ThrowIfRecentlyFailed(key);

            try
            {
                // 带超时下载
                var timer = Stopwatch.StartNew();
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(_options.EffectiveTimeout);
                using var response = await _httpClient.GetAsync(source, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                var bytes = await response.Content.ReadAsByteArrayAsync(timeout.Token).ConfigureAwait(false);
                timer.Stop();

                // 缓存结果，清除失败缓存
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
                // 下载失败：添加到失败缓存，避免短时间内重复请求
                _failureCache[key] = DateTimeOffset.UtcNow.AddSeconds(_options.FailureCacheSeconds);
                throw;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// 检查URL是否最近失败过：
    /// 如果在失败缓存中且未过期，直接抛出异常。
    /// </summary>
    private void ThrowIfRecentlyFailed(string key)
    {
        if (!_failureCache.TryGetValue(key, out var expiresAt)) return;
        if (expiresAt > DateTimeOffset.UtcNow)
            throw new HttpRequestException($"Image source '{key}' is temporarily unavailable (cached failure).");
        _failureCache.TryRemove(key, out _);
    }

    /// <summary>计算字节数组的SHA256哈希</summary>
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
}
