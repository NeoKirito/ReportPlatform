using System.Collections.Concurrent;
using PEIS.Report.Contracts;

namespace PEIS.Report.Api.Printing;

public sealed class ReportDeliveryStateStore
{
    private readonly ConcurrentDictionary<Guid, ReportDeliveryResult> _states = new();

    public void Initialize(ReportDeliveryResult result) => _states[result.JobId] = result;

    public void Update(ReportDeliveryResult result) => _states[result.JobId] = result;

    public ReportDeliveryResult? Get(Guid jobId)
        => _states.TryGetValue(jobId, out var value) ? value : null;
}
