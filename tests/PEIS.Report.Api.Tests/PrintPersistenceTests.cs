using Microsoft.Extensions.Options;
using PEIS.Report.Api.Printing;
using PEIS.Report.Contracts;
using Xunit;

namespace PEIS.Report.Api.Tests;

public sealed class PrintPersistenceTests : IDisposable
{
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"peis-print-state-{Guid.NewGuid():N}.db");

    [Fact]
    public async Task Job_state_and_idempotency_are_retained_after_store_restart()
    {
        var jobId = Guid.NewGuid();
        var targetId = Guid.NewGuid();
        var artifactId = Guid.NewGuid();
        var createdAt = DateTimeOffset.UtcNow;
        var target = new PrintTargetResult(jobId, targetId, "agent-a", "guide", "A4_GUIDE", "Printer A", PrintTargetStatus.Queued);
        var response = new CreatePrintJobResponse(jobId, 1, 1, createdAt);

        var first = new PrintJobStateStore(_databasePath);
        var reservation = await first.ReserveIdempotencyAsync("REGISTRATION_PRINT", "REG-01", "retry-1", TimeSpan.FromMinutes(1));
        Assert.Equal(IdempotencyReservationStatus.Acquired, reservation.Status);
        await first.InitializeAsync(new PrintJobInitialization(
            new PrintJobRecord(jobId, "REGISTRATION_PRINT", "REG-01", "agent-a", "registration", "retry-1", createdAt, createdAt, 0, null, null),
            [new PrintJobTargetState(target, artifactId)]));
        await first.CompleteIdempotencyAsync("REGISTRATION_PRINT", "REG-01", "retry-1", response);

        var restarted = new PrintJobStateStore(_databasePath);
        var recovered = await restarted.GetAsync(jobId);
        var replay = await restarted.ReserveIdempotencyAsync("REGISTRATION_PRINT", "REG-01", "retry-1", TimeSpan.FromMinutes(1));

        Assert.NotNull(recovered);
        Assert.Single(recovered!);
        Assert.Equal(PrintTargetStatus.Queued, recovered.Single().Status);
        Assert.True(await restarted.IsArtifactAuthorizedForAgentAsync(artifactId, "agent-a"));
        Assert.Equal(IdempotencyReservationStatus.Existing, replay.Status);
        Assert.Equal(jobId, replay.ExistingResponse!.JobId);
    }

    [Fact]
    public async Task State_machine_rejects_terminal_or_reverse_transitions()
    {
        var jobId = Guid.NewGuid();
        var targetId = Guid.NewGuid();
        var initial = new PrintTargetResult(jobId, targetId, "agent-a", "barcode", "BARCODE", "Printer B", PrintTargetStatus.Queued);
        var store = new PrintJobStateStore(_databasePath);
        await store.InitializeAsync(new PrintJobInitialization(
            new PrintJobRecord(jobId, "REGISTRATION_PRINT", "REG-01", "agent-a", "registration", null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, 0, null, null),
            [new PrintJobTargetState(initial, Guid.NewGuid())]));

        var premature = await store.UpdateAsync(initial with { Status = PrintTargetStatus.Downloading });
        Assert.False(premature.Applied);
        await store.MarkDispatchedAsync(jobId, [targetId]);
        var dispatched = await store.GetAsync(jobId);
        Assert.Equal(PrintTargetStatus.Dispatched, dispatched!.Single().Status);
        Assert.True((await store.UpdateAsync(initial with { Status = PrintTargetStatus.Downloading })).Applied);
        Assert.True((await store.UpdateAsync(initial with { Status = PrintTargetStatus.Printing })).Applied);
        Assert.True((await store.UpdateAsync(initial with { Status = PrintTargetStatus.Completed, CompletedAt = DateTimeOffset.UtcNow })).Applied);
        var rejected = await store.UpdateAsync(initial with { Status = PrintTargetStatus.Printing });

        Assert.False(rejected.Applied);
        Assert.Contains("Illegal", rejected.Reason!);
    }

    [Fact]
    public async Task Failed_idempotent_operation_is_released_for_safe_retry()
    {
        var store = new PrintJobStateStore(_databasePath);
        var idempotency = new PrintRequestIdempotencyStore(store, Options.Create(new PrintPersistenceOptions()));
        var attempts = 0;

        await Assert.ThrowsAsync<InvalidOperationException>(() => idempotency.GetOrCreateAsync(
            "REGISTRATION_PRINT", "REG-01", "retry-after-failure", () => throw new InvalidOperationException("render failed")));

        var response = await idempotency.GetOrCreateAsync(
            "REGISTRATION_PRINT", "REG-01", "retry-after-failure", () =>
            {
                attempts++;
                return Task.FromResult(new CreatePrintJobResponse(Guid.NewGuid(), 1, 1, DateTimeOffset.UtcNow));
            });

        Assert.Equal(1, attempts);
        Assert.NotEqual(Guid.Empty, response.JobId);
    }

    public void Dispose()
    {
        var directory = Path.GetDirectoryName(_databasePath)!;
        foreach (var file in Directory.EnumerateFiles(directory, $"{Path.GetFileName(_databasePath)}*")) File.Delete(file);
    }
}
