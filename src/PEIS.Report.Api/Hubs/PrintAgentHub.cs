using Microsoft.AspNetCore.SignalR;
using PEIS.Report.Api.Printing;
using PEIS.Report.Contracts;

namespace PEIS.Report.Api.Hubs;

public sealed class PrintAgentHub(AgentRegistry registry, PrintJobStateStore jobs) : Hub
{
    public async Task Register(AgentRegistration registration)
    {
        registry.Upsert(Context.ConnectionId, registration);
        await Groups.AddToGroupAsync(Context.ConnectionId, GroupName(registration.AgentId));
    }

    public Task Heartbeat(string agentId)
    {
        registry.Touch(agentId);
        return Task.CompletedTask;
    }

    public Task ReportResult(PrintTargetResult result)
    {
        jobs.Update(result);
        return Task.CompletedTask;
    }

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        registry.RemoveByConnection(Context.ConnectionId);
        return base.OnDisconnectedAsync(exception);
    }

    public static string GroupName(string agentId) => $"agent:{agentId}";
}
