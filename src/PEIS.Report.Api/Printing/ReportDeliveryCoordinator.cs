using System.Security.Cryptography;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;
using PEIS.Report.Api.Hubs;
using PEIS.Report.Api.Storage;
using PEIS.Report.Contracts;

namespace PEIS.Report.Api.Printing;

public sealed class ReportDeliveryCoordinator(
    AgentRegistry registry,
    IPdfArtifactStore artifacts,
    ReportDeliveryStateStore states,
    ReportDeliveryArtifactTokenStore tokens,
    IHubContext<PrintAgentHub> hub,
    IOptions<ReportDeliverySecurityOptions> options)
{
    public async Task<CreateReportDeliveryResponse> CreateAsync(
        string? stationId,
        ReportDeliveryAction action,
        string fileName,
        Stream pdf,
        long declaredLength,
        string? printerRole,
        int copies,
        bool duplex,
        CancellationToken cancellationToken,
        string? clientAddress = null,
        string? djid = null)
    {
        ArgumentNullException.ThrowIfNull(pdf);
        var agent = ResolveAgent(stationId, clientAddress);

        var maxLength = Math.Max(1024, options.Value.MaxUploadBytes);
        if (declaredLength <= 0 || declaredLength > maxLength)
            throw new ArgumentException($"PDF length must be between 1 and {maxLength} bytes.");

        string? printerName = null;
        if (action is ReportDeliveryAction.Print or ReportDeliveryAction.PreviewAndPrint)
        {
            // Existing role bindings remain a compatible preconfigured fallback. New desktop printing normally sends
            // Djid and lets the workstation remember the user's physical-printer choice locally.
            if (!string.IsNullOrWhiteSpace(printerRole) &&
                agent.PrinterBindings.TryGetValue(printerRole, out var boundPrinter) &&
                !string.IsNullOrWhiteSpace(boundPrinter))
            {
                if (!agent.Printers.Any(x => string.Equals(x.Name, boundPrinter, StringComparison.OrdinalIgnoreCase)))
                    throw new InvalidOperationException($"Printer '{boundPrinter}' is not installed at station '{agent.StationId}'.");
                printerName = boundPrinter;
            }
            else if (string.IsNullOrWhiteSpace(djid))
            {
                throw new ArgumentException("Djid is required when no workstation printer role is preconfigured.");
            }
        }

        await using var buffer = new MemoryStream((int)Math.Min(declaredLength, int.MaxValue));
        await pdf.CopyToAsync(buffer, cancellationToken);
        var bytes = buffer.ToArray();
        if (bytes.Length != declaredLength)
            throw new InvalidDataException($"Uploaded PDF length mismatch: declared {declaredLength}, received {bytes.Length}.");
        if (bytes.Length < 5 || !bytes.AsSpan(0, 5).SequenceEqual("%PDF-"u8))
            throw new InvalidDataException("Uploaded content is not a PDF document.");

        var sha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var artifactId = await artifacts.SaveAsync(bytes, fileName, cancellationToken);
        var jobId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var expiresAt = now.AddMinutes(Math.Clamp(options.Value.ArtifactLifetimeMinutes, 5, 1440));
        var downloadToken = tokens.Create(artifactId, expiresAt);
        var dispatch = new ReportDeliveryDispatch(
            jobId,
            artifactId,
            $"/api/report-deliveries/artifacts/{artifactId}?token={downloadToken}",
            fileName,
            sha256,
            bytes.LongLength,
            expiresAt,
            action,
            printerRole,
            printerName,
            Math.Max(1, copies),
            duplex,
            string.IsNullOrWhiteSpace(djid) ? null : djid.Trim());

        states.Initialize(new ReportDeliveryResult(jobId, agent.AgentId, ReportDeliveryStatus.Queued, UpdatedAt: now));
        await hub.Clients.Group(PrintAgentHub.GroupName(agent.AgentId))
            .SendAsync("ReportDelivery", dispatch, cancellationToken);

        return new CreateReportDeliveryResponse(jobId, artifactId, agent.StationId, action, now, expiresAt);
    }

    private AgentRegistry.AgentState ResolveAgent(string? stationId, string? clientAddress)
    {
        if (!string.IsNullOrWhiteSpace(stationId))
            return registry.FindByStation(stationId)
                ?? throw new InvalidOperationException($"Report station '{stationId}' is offline.");

        var byAddress = registry.FindByClientAddress(clientAddress);
        if (byAddress is not null) return byAddress;

        var online = registry.Snapshot();
        if (online.Count == 1) return online.Single();
        if (online.Count == 0)
            throw new InvalidOperationException("No desktop report agent is online. Start PEIS.PrintAgent and retry.");

        var normalized = AgentRegistry.NormalizeAddress(clientAddress) ?? "unknown";
        throw new InvalidOperationException(
            $"Multiple desktop agents are online and none uniquely matches client address '{normalized}'. " +
            "Choose a station from /api/report-deliveries/stations once; normal same-PC calls are matched automatically.");
    }
}
