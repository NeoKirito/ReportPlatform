using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PEIS.PrintAgent;
using PEIS.PrintAgent.Printing;
using PEIS.Report.Api.Printing;
using PEIS.Report.Contracts;
using Xunit;

namespace PEIS.Report.Api.Tests;

/// <summary>
/// Exercises durable API state together with the unmodified PrintAgent DryRun queue sources.
/// The test deliberately never loads CommandPrintBackend, a Windows spooler, printer driver, or physical printer.
/// </summary>
public sealed class LocalDryRunEndToEndTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"peis-local-dryrun-{Guid.NewGuid():N}");

    [Fact]
    public async Task Synthetic_job_is_persisted_completed_recovered_and_replayed_without_duplicate_target()
    {
        Directory.CreateDirectory(_root);
        var databasePath = Path.Combine(_root, "print-state.db");
        var pdfPath = Path.Combine(_root, "synthetic.pdf");
        await File.WriteAllBytesAsync(pdfPath, "%PDF-1.4\n%%EOF\n"u8.ToArray());

        var jobId = Guid.NewGuid();
        var targetId = Guid.NewGuid();
        var artifactId = Guid.NewGuid();
        var createdAt = DateTimeOffset.UtcNow;
        var target = new PrintTargetResult(jobId, targetId, "dryrun-agent", "guide", "A4_GUIDE", "Synthetic Printer", PrintTargetStatus.Queued);
        var response = new CreatePrintJobResponse(jobId, 1, 1, createdAt);
        var states = new PrintJobStateStore(databasePath);
        var idempotency = new PrintRequestIdempotencyStore(states, Options.Create(new PrintPersistenceOptions()));

        var reservation = await idempotency.ReserveAsync("REGISTRATION_PRINT", "DRYRUN-01", "synthetic-key", CancellationToken.None);
        Assert.Equal(IdempotencyReservationStatus.Acquired, reservation.Status);
        await states.InitializeAsync(new PrintJobInitialization(
            new PrintJobRecord(jobId, "REGISTRATION_PRINT", "DRYRUN-01", "dryrun-agent", "synthetic", "synthetic-key", createdAt, createdAt, 0, null, null),
            [new PrintJobTargetState(target, artifactId)]));
        await idempotency.CompleteAsync("REGISTRATION_PRINT", "DRYRUN-01", "synthetic-key", response, CancellationToken.None);
        await states.MarkDispatchedAsync(jobId, [targetId]);
        Assert.True((await states.UpdateAsync(target with { Status = PrintTargetStatus.Downloading })).Applied);

        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var backend = new DryRunPrintBackend(NullLogger<DryRunPrintBackend>.Instance);
        var queue = new PrinterQueueManager(
            backend,
            Options.Create(new AgentOptions { PrintBackend = new PrintBackendOptions { Mode = "DryRun", RetryCount = 0, RetryDelaySeconds = 0 } }),
            NullLogger<PrinterQueueManager>.Instance);
        await queue.EnqueueAsync(new PrintWorkItem(jobId,
            new PrintDocumentDispatch(targetId, artifactId, "/synthetic", "guide", "GUIDE_A4", "A4_GUIDE", "Synthetic Printer", 1, false),
            pdfPath,
            async (status, message) =>
            {
                var update = await states.UpdateAsync(target with
                {
                    Status = status,
                    CompletedAt = status is PrintTargetStatus.Completed or PrintTargetStatus.Failed ? DateTimeOffset.UtcNow : null,
                    Message = message
                });
                Assert.True(update.Applied, update.Reason);
                if (status == PrintTargetStatus.Completed) completed.TrySetResult();
            }), CancellationToken.None);

        await completed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var restarted = new PrintJobStateStore(databasePath);
        var recovered = await restarted.GetAsync(jobId);
        Assert.NotNull(recovered);
        Assert.Single(recovered!);
        Assert.Equal(PrintTargetStatus.Completed, recovered.Single().Status);

        var replay = await idempotency.ReserveAsync("REGISTRATION_PRINT", "DRYRUN-01", "synthetic-key", CancellationToken.None);
        Assert.Equal(IdempotencyReservationStatus.Existing, replay.Status);
        Assert.Equal(jobId, replay.ExistingResponse!.JobId);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
