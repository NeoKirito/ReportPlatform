using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using PEIS.Report.Contracts;

namespace PEIS.Report.Api.Printing;

public sealed class AgentRegistry(IOptions<AgentRegistryOptions> options)
{
    private readonly ConcurrentDictionary<string, AgentState> _agents = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyCollection<AgentState> Snapshot()
    {
        PruneExpired();
        return _agents.Values.OrderBy(x => x.StationId).ThenBy(x => x.MachineName).ToArray();
    }

    public AgentState? FindByStation(string stationId)
    {
        PruneExpired();
        return _agents.Values.SingleOrDefault(x => string.Equals(x.StationId, stationId, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Registers a single active agent for a business station. A reconnect using the same persisted AgentId replaces
    /// its former SignalR connection. A different active installation may not claim the same StationId.
    /// </summary>
    public AgentRegistrationResult TryRegister(string connectionId, AgentRegistration registration)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionId);
        ArgumentNullException.ThrowIfNull(registration);
        if (string.IsNullOrWhiteSpace(registration.AgentId))
            return AgentRegistrationResult.Invalid("AgentId is required.");
        if (string.IsNullOrWhiteSpace(registration.StationId))
            return AgentRegistrationResult.Invalid("StationId is required.");

        PruneExpired();
        var stationConflict = _agents.Values.FirstOrDefault(x =>
            !string.Equals(x.AgentId, registration.AgentId, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(x.StationId, registration.StationId, StringComparison.OrdinalIgnoreCase));
        if (stationConflict is not null)
            return AgentRegistrationResult.StationConflict(registration.StationId, stationConflict.AgentId, stationConflict.MachineName);

        var agentId = registration.AgentId.Trim();
        _agents[agentId] = new AgentState(
            agentId,
            registration.StationId.Trim(),
            registration.MachineName,
            registration.Printers,
            new Dictionary<string, string>(registration.PrinterBindings, StringComparer.OrdinalIgnoreCase),
            registration.Version,
            connectionId,
            DateTimeOffset.UtcNow);
        return AgentRegistrationResult.Accepted(agentId, registration.StationId.Trim());
    }

    public bool Touch(string agentId, string connectionId)
    {
        if (_agents.TryGetValue(agentId, out var state) && string.Equals(state.ConnectionId, connectionId, StringComparison.Ordinal))
        {
            _agents[agentId] = state with { LastSeenAt = DateTimeOffset.UtcNow };
            return true;
        }
        return false;
    }

    public bool IsCurrentConnection(string agentId, string connectionId)
        => _agents.TryGetValue(agentId, out var state) &&
           string.Equals(state.ConnectionId, connectionId, StringComparison.Ordinal);

    public void RemoveByConnection(string connectionId)
    {
        foreach (var item in _agents)
        {
            if (item.Value.ConnectionId == connectionId)
                _agents.TryRemove(item.Key, out _);
        }
    }

    private void PruneExpired()
    {
        var cutoff = DateTimeOffset.UtcNow.AddSeconds(-Math.Max(15, options.Value.OfflineAfterSeconds));
        foreach (var item in _agents)
        {
            if (item.Value.LastSeenAt < cutoff)
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

public sealed class AgentRegistryOptions
{
    /// <summary>Agents without a validated heartbeat beyond this window are no longer routeable.</summary>
    public int OfflineAfterSeconds { get; set; } = 90;
}

public sealed record AgentRegistrationResult(
    AgentRegistrationStatus Status,
    string? Message = null,
    string? ConflictingAgentId = null,
    string? ConflictingMachineName = null)
{
    public bool Succeeded => Status == AgentRegistrationStatus.Accepted;

    public static AgentRegistrationResult Accepted(string agentId, string stationId)
        => new(AgentRegistrationStatus.Accepted, $"Agent '{agentId}' registered for station '{stationId}'.");

    public static AgentRegistrationResult Invalid(string message)
        => new(AgentRegistrationStatus.Invalid, message);

    public static AgentRegistrationResult StationConflict(string stationId, string agentId, string machineName)
        => new(AgentRegistrationStatus.StationConflict, $"Station '{stationId}' is already claimed by an active agent.", agentId, machineName);
}

public enum AgentRegistrationStatus
{
    Accepted,
    Invalid,
    StationConflict
}
