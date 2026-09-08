using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;
using PEIS.Report.Api.Printing;
using PEIS.Report.Contracts;

namespace PEIS.Report.Api.Hubs;

public sealed class PrintAgentHub(
    AgentRegistry registry,
    PrintJobStateStore jobs,
    ReportDeliveryStateStore deliveries,
    IOptions<PrintAgentSecurityOptions> security) : Hub
{
    public async Task Register(AgentRegistration registration)
    {
        if (!security.Value.IsRegistrationAuthorized(registration.RegistrationToken))
            throw new HubException("PrintAgent registration is not authorized.");

        var outcome = registry.TryRegister(
            Context.ConnectionId,
            registration,
            Context.GetHttpContext()?.Connection.RemoteIpAddress?.ToString());
        if (!outcome.Succeeded)
            throw new HubException(outcome.Message ?? "PrintAgent registration was rejected.");

        await Groups.AddToGroupAsync(Context.ConnectionId, GroupName(registration.AgentId));
    }

    public Task Heartbeat(string agentId)
    {
        registry.Touch(agentId, Context.ConnectionId);
        return Task.CompletedTask;
    }

    public Task ReportResult(PrintTargetResult result)
    {
        if (!registry.IsCurrentConnection(result.AgentId, Context.ConnectionId))
            throw new HubException("PrintAgent result sender is not the active registered connection.");

        jobs.Update(result);
        return Task.CompletedTask;
    }

    public Task ReportDeliveryResult(ReportDeliveryResult result)
    {
        if (!registry.IsCurrentConnection(result.AgentId, Context.ConnectionId))
            throw new HubException("Report delivery result sender is not the active registered connection.");

        deliveries.Update(result with { UpdatedAt = result.UpdatedAt ?? DateTimeOffset.UtcNow });
        return Task.CompletedTask;
    }

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        registry.RemoveByConnection(Context.ConnectionId);
        return base.OnDisconnectedAsync(exception);
    }

    public static string GroupName(string agentId) => $"agent:{agentId}";
}
