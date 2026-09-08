using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;
using PEIS.Report.Api.Hubs;
using PEIS.Report.Api.Printing;
using PEIS.Report.Api.Storage;
using PEIS.Report.Contracts;
using Xunit;

namespace PEIS.Report.Api.Tests;

public sealed class ReportDeliveryCoordinatorTests
{
    [Fact]
    public async Task Preview_saves_final_pdf_and_dispatches_to_the_requested_online_station()
    {
        var registry = CreateRegistry();
        Register(registry);
        var artifacts = new MemoryArtifactStore();
        var states = new ReportDeliveryStateStore();
        var hub = new CapturingHubContext();
        var coordinator = new ReportDeliveryCoordinator(
            registry, artifacts, states, new ReportDeliveryArtifactTokenStore(), hub,
            Options.Create(new ReportDeliverySecurityOptions { ArtifactLifetimeMinutes = 30 }));
        byte[] pdf = [37, 80, 68, 70, 45, 49, 46, 52, 10];

        var response = await coordinator.CreateAsync(
            "REG-01", ReportDeliveryAction.Preview, "preview.pdf", new MemoryStream(pdf), pdf.Length,
            null, 1, false, CancellationToken.None);

        Assert.Equal(ReportDeliveryAction.Preview, response.Action);
        Assert.Equal(pdf, artifacts.Bytes[response.ArtifactId]);
        var dispatch = Assert.IsType<ReportDeliveryDispatch>(hub.Proxy.Argument);
        Assert.Equal(response.JobId, dispatch.JobId);
        Assert.Equal("preview.pdf", dispatch.FileName);
        Assert.Equal(ReportDeliveryStatus.Queued, states.Get(response.JobId)!.Status);
    }

    [Fact]
    public async Task Printable_delivery_without_djid_still_requires_a_registered_printer_role()
    {
        var registry = CreateRegistry();
        Register(registry);
        var coordinator = new ReportDeliveryCoordinator(
            registry, new MemoryArtifactStore(), new ReportDeliveryStateStore(), new ReportDeliveryArtifactTokenStore(), new CapturingHubContext(),
            Options.Create(new ReportDeliverySecurityOptions()));
        byte[] pdf = [37, 80, 68, 70, 45, 49];

        await Assert.ThrowsAsync<ArgumentException>(() => coordinator.CreateAsync(
            "REG-01", ReportDeliveryAction.PreviewAndPrint, "preview.pdf", new MemoryStream(pdf), pdf.Length,
            "UNKNOWN", 1, false, CancellationToken.None));
    }

    [Fact]
    public async Task Printable_delivery_with_djid_defers_first_printer_choice_to_the_agent()
    {
        var registry = CreateRegistry();
        Register(registry);
        var hub = new CapturingHubContext();
        var coordinator = new ReportDeliveryCoordinator(
            registry, new MemoryArtifactStore(), new ReportDeliveryStateStore(), new ReportDeliveryArtifactTokenStore(), hub,
            Options.Create(new ReportDeliverySecurityOptions()));
        byte[] pdf = [37, 80, 68, 70, 45, 49];

        await coordinator.CreateAsync(
            "REG-01", ReportDeliveryAction.PreviewAndPrint, "preview.pdf", new MemoryStream(pdf), pdf.Length,
            null, 1, false, CancellationToken.None, djid: "jktjbbd");

        var dispatch = Assert.IsType<ReportDeliveryDispatch>(hub.Proxy.Argument);
        Assert.Equal("jktjbbd", dispatch.Djid);
        Assert.Null(dispatch.PrinterName);
    }

    [Fact]
    public async Task Missing_station_is_resolved_from_the_browser_address()
    {
        var registry = CreateRegistry();
        var registration = new AgentRegistration(
            "agent-1", "PC-01", "PC-01",
            [new PrinterDescriptor("Printer A", true)],
            new Dictionary<string, string>(), "1.0");
        Assert.True(registry.TryRegister("connection-1", registration, "192.168.0.21").Succeeded);
        var coordinator = new ReportDeliveryCoordinator(
            registry, new MemoryArtifactStore(), new ReportDeliveryStateStore(), new ReportDeliveryArtifactTokenStore(), new CapturingHubContext(),
            Options.Create(new ReportDeliverySecurityOptions()));
        byte[] pdf = [37, 80, 68, 70, 45, 49];

        var response = await coordinator.CreateAsync(
            null, ReportDeliveryAction.Preview, "preview.pdf", new MemoryStream(pdf), pdf.Length,
            null, 1, false, CancellationToken.None, "::ffff:192.168.0.21");

        Assert.Equal("PC-01", response.StationId);
    }

    private static AgentRegistry CreateRegistry()
        => new(Options.Create(new AgentRegistryOptions { OfflineAfterSeconds = 300 }));

    private static void Register(AgentRegistry registry)
    {
        var registration = new AgentRegistration(
            "agent-1", "REG-01", "PC-01",
            [new PrinterDescriptor("Printer A", true)],
            new Dictionary<string, string> { ["A4_REPORT"] = "Printer A" }, "1.0");
        Assert.True(registry.TryRegister("connection-1", registration).Succeeded);
    }

    private sealed class MemoryArtifactStore : IPdfArtifactStore
    {
        public Dictionary<Guid, byte[]> Bytes { get; } = [];

        public Task<Guid> SaveAsync(byte[] pdf, string fileName, CancellationToken cancellationToken)
        {
            var id = Guid.NewGuid();
            Bytes[id] = pdf;
            return Task.FromResult(id);
        }

        public Task<PdfArtifact?> OpenAsync(Guid artifactId, CancellationToken cancellationToken)
            => Task.FromResult<PdfArtifact?>(null);
    }

    private sealed class CapturingHubContext : IHubContext<PrintAgentHub>
    {
        public CapturingClientProxy Proxy { get; } = new();
        public IHubClients Clients => new CapturingHubClients(Proxy);
        public IGroupManager Groups => throw new NotSupportedException();
    }

    private sealed class CapturingHubClients(CapturingClientProxy proxy) : IHubClients
    {
        public IClientProxy All => proxy;
        public IClientProxy AllExcept(IReadOnlyList<string> excludedConnectionIds) => proxy;
        public IClientProxy Client(string connectionId) => proxy;
        public IClientProxy Clients(IReadOnlyList<string> connectionIds) => proxy;
        public IClientProxy Group(string groupName) => proxy;
        public IClientProxy GroupExcept(string groupName, IReadOnlyList<string> excludedConnectionIds) => proxy;
        public IClientProxy Groups(IReadOnlyList<string> groupNames) => proxy;
        public IClientProxy User(string userId) => proxy;
        public IClientProxy Users(IReadOnlyList<string> userIds) => proxy;
    }

    private sealed class CapturingClientProxy : IClientProxy
    {
        public string? Method { get; private set; }
        public object? Argument { get; private set; }

        public Task SendCoreAsync(string method, object?[] args, CancellationToken cancellationToken = default)
        {
            Method = method;
            Argument = args.Single();
            return Task.CompletedTask;
        }
    }
}
