using Microsoft.Extensions.Options;
using PEIS.Report.Api.Printing;
using PEIS.Report.Contracts;
using Xunit;

namespace PEIS.Report.Api.Tests;

public sealed class AgentRegistryTests
{
    [Fact]
    public void Different_agents_cannot_claim_the_same_active_station()
    {
        var registry = CreateRegistry();

        var first = registry.TryRegister("connection-a", Registration("agent-a", "REG-01", "PC-A"));
        var second = registry.TryRegister("connection-b", Registration("agent-b", "REG-01", "PC-B"));

        Assert.True(first.Succeeded);
        Assert.Equal(AgentRegistrationStatus.StationConflict, second.Status);
        Assert.Equal("agent-a", second.ConflictingAgentId);
        Assert.Equal("PC-A", second.ConflictingMachineName);
        Assert.Equal("agent-a", registry.FindByStation("REG-01")?.AgentId);
    }

    [Fact]
    public void Same_persisted_agent_can_reconnect_without_station_conflict()
    {
        var registry = CreateRegistry();
        Assert.True(registry.TryRegister("connection-a", Registration("agent-a", "REG-01", "PC-A")).Succeeded);

        var reconnected = registry.TryRegister("connection-b", Registration("agent-a", "REG-01", "PC-A"));

        Assert.True(reconnected.Succeeded);
        var state = Assert.Single(registry.Snapshot());
        Assert.Equal("connection-b", state.ConnectionId);
    }

    [Fact]
    public void Heartbeat_from_replaced_connection_cannot_refresh_agent()
    {
        var registry = CreateRegistry();
        registry.TryRegister("connection-a", Registration("agent-a", "REG-01", "PC-A"));
        registry.TryRegister("connection-b", Registration("agent-a", "REG-01", "PC-A"));

        Assert.False(registry.Touch("agent-a", "connection-a"));
        Assert.False(registry.IsCurrentConnection("agent-a", "connection-a"));
        Assert.True(registry.Touch("agent-a", "connection-b"));
        Assert.True(registry.IsCurrentConnection("agent-a", "connection-b"));
    }

    [Fact]
    public void Empty_station_or_agent_is_rejected()
    {
        var registry = CreateRegistry();

        var noAgent = registry.TryRegister("connection-a", Registration(" ", "REG-01", "PC-A"));
        var noStation = registry.TryRegister("connection-b", Registration("agent-b", " ", "PC-B"));

        Assert.Equal(AgentRegistrationStatus.Invalid, noAgent.Status);
        Assert.Equal(AgentRegistrationStatus.Invalid, noStation.Status);
    }

    private static AgentRegistry CreateRegistry()
        => new(Options.Create(new AgentRegistryOptions { OfflineAfterSeconds = 90 }));

    private static AgentRegistration Registration(string agentId, string stationId, string machineName)
        => new(
            agentId,
            stationId,
            machineName,
            [new PrinterDescriptor("Test Printer", true)],
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["A4_GUIDE"] = "Test Printer" },
            "1.0.0");
}
