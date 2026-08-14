using System.Collections.Concurrent;
using PEIS.Report.Contracts;

namespace PEIS.Report.Api.Printing;

/// <summary>
/// Prevents a browser/network retry from rendering and dispatching the same business action twice while the API
/// process is alive. The stable key is supplied by the caller and scoped by action and station. A database-backed
/// implementation can replace this store for cross-node durability without changing the coordinator contract.
/// </summary>
public sealed class PrintRequestIdempotencyStore
{
    private readonly ConcurrentDictionary<string, Lazy<Task<CreatePrintJobResponse>>> _requests = new(StringComparer.Ordinal);

    public Task<CreatePrintJobResponse> GetOrCreateAsync(
        string actionCode,
        string stationId,
        string idempotencyKey,
        Func<Task<CreatePrintJobResponse>> create)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actionCode);
        ArgumentException.ThrowIfNullOrWhiteSpace(stationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(idempotencyKey);
        ArgumentNullException.ThrowIfNull(create);

        var key = $"{actionCode.Trim().ToUpperInvariant()}:{stationId.Trim().ToUpperInvariant()}:{idempotencyKey.Trim()}";
        var candidate = new Lazy<Task<CreatePrintJobResponse>>(create, LazyThreadSafetyMode.ExecutionAndPublication);
        var operation = _requests.GetOrAdd(key, candidate);
        return AwaitAndRemoveFaultAsync(key, operation);
    }

    private async Task<CreatePrintJobResponse> AwaitAndRemoveFaultAsync(
        string key,
        Lazy<Task<CreatePrintJobResponse>> operation)
    {
        try
        {
            return await operation.Value.ConfigureAwait(false);
        }
        catch
        {
            if (_requests.TryGetValue(key, out var current) && ReferenceEquals(current, operation))
                _requests.TryRemove(key, out _);
            throw;
        }
    }
}
