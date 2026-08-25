using Microsoft.Extensions.Options;
using PEIS.Report.Contracts;

namespace PEIS.Report.Api.Printing;

/// <summary>
/// Durable idempotency keyed by business action, workstation and caller-supplied operation key.
/// A completed request is replayed after an API restart; a short-lived in-progress reservation prevents
/// concurrent browser retries from creating another logical job.
/// </summary>
public sealed class PrintRequestIdempotencyStore(
    PrintJobStateStore stateStore,
    IOptions<PrintPersistenceOptions> options)
{
    public Task<IdempotencyReservation> ReserveAsync(string actionCode, string stationId, string idempotencyKey, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actionCode);
        ArgumentException.ThrowIfNullOrWhiteSpace(stationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(idempotencyKey);
        return stateStore.ReserveIdempotencyAsync(
            actionCode,
            stationId,
            idempotencyKey,
            TimeSpan.FromSeconds(Math.Max(15, options.Value.IdempotencyReservationSeconds)),
            cancellationToken);
    }

    public Task CompleteAsync(string actionCode, string stationId, string idempotencyKey, CreatePrintJobResponse response, CancellationToken cancellationToken = default)
        => stateStore.CompleteIdempotencyAsync(actionCode, stationId, idempotencyKey, response, cancellationToken);

    public Task ReleaseAsync(string actionCode, string stationId, string idempotencyKey, CancellationToken cancellationToken = default)
        => stateStore.ReleaseIdempotencyAsync(actionCode, stationId, idempotencyKey, cancellationToken);

    public async Task<CreatePrintJobResponse> GetOrCreateAsync(
        string actionCode,
        string stationId,
        string idempotencyKey,
        Func<Task<CreatePrintJobResponse>> create,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actionCode);
        ArgumentException.ThrowIfNullOrWhiteSpace(stationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(idempotencyKey);
        ArgumentNullException.ThrowIfNull(create);

        var reservation = await ReserveAsync(actionCode, stationId, idempotencyKey, cancellationToken);

        if (reservation.Status == IdempotencyReservationStatus.Existing)
            return reservation.ExistingResponse!;
        if (reservation.Status == IdempotencyReservationStatus.Pending)
            throw new InvalidOperationException("A request with this idempotency key is already being processed. Retry with the same key after the current request completes.");

        try
        {
            var response = await create();
            await CompleteAsync(actionCode, stationId, idempotencyKey, response, cancellationToken);
            return response;
        }
        catch
        {
            await ReleaseAsync(actionCode, stationId, idempotencyKey, CancellationToken.None);
            throw;
        }
    }
}
