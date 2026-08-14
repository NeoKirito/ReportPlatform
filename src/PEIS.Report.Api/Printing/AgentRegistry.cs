using System.Collections.Concurrent;
using PEIS.Report.Contracts;

namespace PEIS.Report.Api.Printing;

public sealed class AgentRegistry
{
    private readonly ConcurrentDictionary<string, AgentState> _agents = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyCollection<AgentState> Snapshot()
        => _agents.Values.OrderBy(x => x.StationId).ThenBy(x => x.MachineName).ToArray();

    public AgentState? FindByStation(string stationId)
        => _agents.Values
            .Where(x => string.Equals(x.StationId, stationId, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(x => x.LastSeenAt)
            .FirstOrDefault();

    public void Upsert(string connectionId, AgentRegistration registration)
        => _agents[registration.AgentId] = new AgentState(
            registration.AgentId,
            registration.StationId,
            registration.MachineName,
            registration.Printers,
            new Dictionary<string, string>(registration.PrinterBindings, StringComparer.OrdinalIgnoreCase),
            registration.Version,
            connectionId,
            DateTimeOffset.UtcNow);

    public void Touch(string agentId)
    {
        if (_agents.TryGetValue(agentId, out var state))
            _agents[agentId] = state with { LastSeenAt = DateTimeOffset.UtcNow };
    }

    public void RemoveByConnection(string connectionId)
    {
        foreach (var item in _agents)
        {
            if (item.Value.ConnectionId == connectionId)
                _agents.TryRemove(item.Key, out _);
        }
    }

    public sealed record AgentState(
        string AgentId,
        string StationId,
        string MachineName,
        IReadOnlyList<PrinterDescriptor> Printers,
        IReadOnlyDictionary<string, string> PrinterBindings,
        string Version,
        string ConnectionId,
        DateTimeOffset LastSeenAt);
}
