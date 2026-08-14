using System.Collections.Concurrent;
using PEIS.Report.Contracts;

namespace PEIS.Report.Api.Printing;

public sealed class PrintJobStateStore
{
    private readonly ConcurrentDictionary<Guid, ConcurrentDictionary<Guid, PrintTargetResult>> _jobs = new();

    public void Initialize(Guid jobId, IEnumerable<PrintTargetResult> targets)
    {
        _jobs[jobId] = new ConcurrentDictionary<Guid, PrintTargetResult>(targets.ToDictionary(x => x.TargetId));
    }

    public void Update(PrintTargetResult result)
    {
        var job = _jobs.GetOrAdd(result.JobId, _ => new ConcurrentDictionary<Guid, PrintTargetResult>());
        job[result.TargetId] = result;
    }

    public IReadOnlyCollection<PrintTargetResult>? Get(Guid jobId)
        => _jobs.TryGetValue(jobId, out var state) ? state.Values.ToArray() : null;
}
