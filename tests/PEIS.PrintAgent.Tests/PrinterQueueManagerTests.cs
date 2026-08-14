using System.Collections.Concurrent;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PEIS.PrintAgent;
using PEIS.PrintAgent.Printing;
using PEIS.Report.Contracts;
using Xunit;

namespace PEIS.PrintAgent.Tests;

public sealed class PrinterQueueManagerTests
{
    [Fact]
    public async Task Same_printer_jobs_are_serialized()
    {
        var backend = new RecordingBackend(TimeSpan.FromMilliseconds(30));
        var manager = CreateManager(backend, retryCount: 0);
        var completed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var completions = 0;

        await manager.EnqueueAsync(CreateWorkItem("A4", () =>
        {
            if (Interlocked.Increment(ref completions) == 2) completed.TrySetResult(true);
        }), CancellationToken.None);
        await manager.EnqueueAsync(CreateWorkItem("A4", () =>
        {
            if (Interlocked.Increment(ref completions) == 2) completed.TrySetResult(true);
        }), CancellationToken.None);

        await completed.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(1, backend.MaxActiveByPrinter["A4"]);
    }

    [Fact]
    public async Task Different_printer_jobs_can_run_in_parallel()
    {
        var backend = new RecordingBackend(TimeSpan.FromMilliseconds(60));
        var manager = CreateManager(backend, retryCount: 0);
        var completed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var completions = 0;

        await manager.EnqueueAsync(CreateWorkItem("A4", () =>
        {
            if (Interlocked.Increment(ref completions) == 2) completed.TrySetResult(true);
        }), CancellationToken.None);
        await manager.EnqueueAsync(CreateWorkItem("BARCODE", () =>
        {
            if (Interlocked.Increment(ref completions) == 2) completed.TrySetResult(true);
        }), CancellationToken.None);

        await completed.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(backend.MaxGlobalActive >= 2);
    }

    [Fact]
    public async Task Failed_backend_is_retried_before_completion()
    {
        var backend = new RecordingBackend(TimeSpan.Zero, failuresBeforeSuccess: 1);
        var manager = CreateManager(backend, retryCount: 1);
        var completed = new TaskCompletionSource<IReadOnlyList<PrintTargetStatus>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var statuses = new ConcurrentQueue<PrintTargetStatus>();

        await manager.EnqueueAsync(CreateWorkItem("A4", () => { }, (status, _) =>
        {
            statuses.Enqueue(status);
            if (status is PrintTargetStatus.Completed or PrintTargetStatus.Failed)
                completed.TrySetResult(statuses.ToArray());
            return Task.CompletedTask;
        }), CancellationToken.None);

        var finalStatuses = await completed.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(2, backend.Attempts);
        Assert.Equal(PrintTargetStatus.Completed, finalStatuses.Last());
    }

    private static PrinterQueueManager CreateManager(RecordingBackend backend, int retryCount)
        => new(
            backend,
            Options.Create(new AgentOptions
            {
                PrintBackend = new PrintBackendOptions { RetryCount = retryCount, RetryDelaySeconds = 0 }
            }),
            NullLogger<PrinterQueueManager>.Instance);

    private static PrintWorkItem CreateWorkItem(
        string printerName,
        Action completed,
        Func<PrintTargetStatus, string?, Task>? statusOverride = null)
    {
        var document = new PrintDocumentDispatch(
            Guid.NewGuid(), Guid.NewGuid(), "/artifact", "test", "TEST", "ROLE", printerName, 1, false);
        return new PrintWorkItem(Guid.NewGuid(), document, "test.pdf", (status, message) =>
        {
            if (status == PrintTargetStatus.Completed) completed();
            return statusOverride?.Invoke(status, message) ?? Task.CompletedTask;
        });
    }

    private sealed class RecordingBackend(TimeSpan delay, int failuresBeforeSuccess = 0) : IPrintBackend
    {
        private readonly ConcurrentDictionary<string, int> _activeByPrinter = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, int> _maxActiveByPrinter = new(StringComparer.OrdinalIgnoreCase);
        private int _globalActive;
        private int _maxGlobalActive;
        private int _attempts;
        private int _remainingFailures = failuresBeforeSuccess;

        public IReadOnlyDictionary<string, int> MaxActiveByPrinter => _maxActiveByPrinter;
        public int MaxGlobalActive => Volatile.Read(ref _maxGlobalActive);
        public int Attempts => Volatile.Read(ref _attempts);

        public async Task PrintAsync(string pdfPath, string printerName, int copies, bool duplex, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _attempts);
            var printerActive = _activeByPrinter.AddOrUpdate(printerName, 1, (_, count) => count + 1);
            SetMax(_maxActiveByPrinter, printerName, printerActive);
            var global = Interlocked.Increment(ref _globalActive);
            SetMax(ref _maxGlobalActive, global);
            try
            {
                if (Interlocked.Decrement(ref _remainingFailures) >= 0)
                    throw new InvalidOperationException("transient backend failure");
                if (delay > TimeSpan.Zero)
                    await Task.Delay(delay, cancellationToken);
            }
            finally
            {
                _activeByPrinter.AddOrUpdate(printerName, 0, (_, count) => count - 1);
                Interlocked.Decrement(ref _globalActive);
            }
        }

        private static void SetMax(ConcurrentDictionary<string, int> values, string key, int value)
            => values.AddOrUpdate(key, value, (_, prior) => Math.Max(prior, value));

        private static void SetMax(ref int target, int value)
        {
            var current = Volatile.Read(ref target);
            while (value > current)
            {
                var observed = Interlocked.CompareExchange(ref target, value, current);
                if (observed == current) return;
                current = observed;
            }
        }
    }
}
