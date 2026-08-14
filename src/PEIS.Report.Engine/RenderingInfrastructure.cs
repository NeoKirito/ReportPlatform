using System.Diagnostics;
using System.Collections.Concurrent;

namespace PEIS.Report.Engine;

public sealed class RenderConcurrencyOptions
{
    /// <summary>Conservative default for CPU- and memory-heavy FastReport preparation.</summary>
    public int MaxConcurrentRenders { get; set; } = 2;
}

/// <summary>
/// Process-wide bounded scheduler. The gate surrounds the expensive render phase only; it does not hold a permit
/// while a request waits for metadata or a downstream HTTP response.
/// </summary>
public sealed class RenderConcurrencyGate
{
    private readonly SemaphoreSlim _semaphore;
    private int _queued;
    private int _active;

    public RenderConcurrencyGate(RenderConcurrencyOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.MaxConcurrentRenders is < 1 or > 64)
            throw new ArgumentOutOfRangeException(nameof(options.MaxConcurrentRenders), "Render concurrency must be between 1 and 64.");
        _semaphore = new SemaphoreSlim(options.MaxConcurrentRenders, options.MaxConcurrentRenders);
    }

    public async Task<RenderLease> EnterAsync(CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _queued);
        try
        {
            await _semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
            Interlocked.Increment(ref _active);
            return new RenderLease(this);
        }
        finally
        {
            Interlocked.Decrement(ref _queued);
        }
    }

    public RenderConcurrencySnapshot Snapshot() => new(Volatile.Read(ref _active), Volatile.Read(ref _queued));

    private void Exit()
    {
        Interlocked.Decrement(ref _active);
        _semaphore.Release();
    }

    public sealed class RenderLease : IDisposable
    {
        private RenderConcurrencyGate? _owner;
        internal RenderLease(RenderConcurrencyGate owner) => _owner = owner;
        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.Exit();
    }
}

public sealed record RenderConcurrencySnapshot(int Active, int Queued);

/// <summary>
/// Captures the required stage timings and scalar metrics in a single render-local object. Each renderer invocation
/// constructs a new collector, preventing cross-request contamination.
/// </summary>
public sealed class ReportRenderMetrics
{
    private readonly Stopwatch _total = Stopwatch.StartNew();
    private readonly List<ReportStageTiming> _timings = [];

    public string? RequestId { get; init; }
    public string? ReportId { get; init; }
    public string? Profile { get; init; }
    public bool DefinitionCacheHit { get; set; }
    public int Rows { get; set; }
    public int ImageCount { get; set; }
    public long ImageBytes { get; set; }
    public int ImageCacheHits { get; set; }
    public int ImageFailures { get; set; }
    public int Pages { get; set; }
    public long PdfBytes { get; set; }

    public async Task MeasureAsync(string stage, Func<Task> operation)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            await operation().ConfigureAwait(false);
        }
        finally
        {
            stopwatch.Stop();
            _timings.Add(new ReportStageTiming(stage, stopwatch.ElapsedMilliseconds));
        }
    }

    public async Task<T> MeasureAsync<T>(string stage, Func<Task<T>> operation)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            return await operation().ConfigureAwait(false);
        }
        finally
        {
            stopwatch.Stop();
            _timings.Add(new ReportStageTiming(stage, stopwatch.ElapsedMilliseconds));
        }
    }

    public ReportRenderObservation Complete()
    {
        _total.Stop();
        _timings.Add(new ReportStageTiming("Total", _total.ElapsedMilliseconds));
        return new ReportRenderObservation(
            RequestId,
            ReportId,
            Profile,
            DefinitionCacheHit,
            Rows,
            ImageCount,
            ImageBytes,
            ImageCacheHits,
            ImageFailures,
            Pages,
            PdfBytes,
            _timings.ToArray());
    }
}

public sealed record ReportRenderObservation(
    string? RequestId,
    string? ReportId,
    string? Profile,
    bool DefinitionCacheHit,
    int Rows,
    int ImageCount,
    long ImageBytes,
    int ImageCacheHits,
    int ImageFailures,
    int Pages,
    long PdfBytes,
    IReadOnlyList<ReportStageTiming> Timings);

public interface IReportRenderTelemetry
{
    void Record(ReportRenderObservation observation);
}

/// <summary>In-memory last-observation store for health checks and deterministic tests.</summary>
public sealed class InMemoryReportRenderTelemetry : IReportRenderTelemetry
{
    private readonly ConcurrentQueue<ReportRenderObservation> _observations = new();
    private const int Capacity = 200;

    public void Record(ReportRenderObservation observation)
    {
        _observations.Enqueue(observation);
        while (_observations.Count > Capacity)
            _observations.TryDequeue(out _);
    }

    public IReadOnlyList<ReportRenderObservation> Snapshot() => _observations.ToArray();
}
