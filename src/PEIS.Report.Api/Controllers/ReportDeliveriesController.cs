using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using PEIS.Report.Api.Printing;
using PEIS.Report.Contracts;
using PEIS.Report.Api.Storage;

namespace PEIS.Report.Api.Controllers;

[ApiController]
[Route("api/report-deliveries")]
public sealed class ReportDeliveriesController(
    ReportDeliveryCoordinator coordinator,
    ReportDeliveryStateStore states,
    ReportDeliveryArtifactTokenStore artifactTokens,
    AgentRegistry registry,
    IPdfArtifactStore artifacts,
    IOptions<ReportDeliverySecurityOptions> security) : ControllerBase
{
    [HttpPost]
    [Consumes("multipart/form-data")]
    [RequestSizeLimit(100 * 1024 * 1024)]
    public async Task<IActionResult> Create([FromForm] ReportDeliveryUpload request, CancellationToken cancellationToken)
    {
        if (!security.Value.IsUploadAuthorized(Request.Headers["X-Report-Delivery-Token"].FirstOrDefault()))
            return Unauthorized();
        if (request.File is null || request.File.Length == 0)
            return BadRequest(new { error = "A non-empty PDF file is required." });
        if (!Enum.TryParse<ReportDeliveryAction>(request.Action, true, out var action))
            return BadRequest(new { error = "Action must be Preview, Print, or PreviewAndPrint." });

        try
        {
            await using var stream = request.File.OpenReadStream();
            var clientAddress = request.ClientAddress ?? HttpContext.Connection.RemoteIpAddress?.ToString();
            var result = await coordinator.CreateAsync(
                request.StationId,
                action,
                request.File.FileName,
                stream,
                request.File.Length,
                request.PrinterRole,
                request.Copies,
                request.Duplex,
                cancellationToken,
                clientAddress,
                request.Djid);
            return AcceptedAtAction(nameof(Get), new { jobId = result.JobId }, result);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
        catch (InvalidDataException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(new { error = ex.Message });
        }
    }

    [HttpGet("stations")]
    public IActionResult Stations([FromQuery] string? clientAddress)
    {
        var effectiveAddress = clientAddress ?? HttpContext.Connection.RemoteIpAddress?.ToString();
        var matched = registry.FindByClientAddress(effectiveAddress);
        return Ok(new
        {
            clientAddress = AgentRegistry.NormalizeAddress(effectiveAddress),
            autoMatchedStationId = matched?.StationId,
            stations = registry.Snapshot().Select(x => new
            {
                x.StationId,
                displayName = x.MachineName,
                isCurrentClient = matched is not null && string.Equals(x.AgentId, matched.AgentId, StringComparison.OrdinalIgnoreCase)
            })
        });
    }

    [HttpGet("{jobId:guid}")]
    public IActionResult Get(Guid jobId)
    {
        var result = states.Get(jobId);
        return result is null ? NotFound() : Ok(result);
    }

    [HttpGet("artifacts/{artifactId:guid}")]
    public async Task<IActionResult> Download(Guid artifactId, [FromQuery] string? token, CancellationToken cancellationToken)
    {
        if (!artifactTokens.Validate(artifactId, token)) return Unauthorized();
        var artifact = await artifacts.OpenAsync(artifactId, cancellationToken);
        if (artifact is null) return NotFound();
        return File(artifact.Stream, "application/pdf", artifact.FileName, enableRangeProcessing: true);
    }
}

public sealed class ReportDeliveryUpload
{
    public string? StationId { get; set; }
    public string? ClientAddress { get; set; }
    /// <summary>Stable report/template identifier used by PrintAgent to remember the workstation printer.</summary>
    public string? Djid { get; set; }
    public string Action { get; set; } = nameof(ReportDeliveryAction.Preview);
    public string? PrinterRole { get; set; }
    public int Copies { get; set; } = 1;
    public bool Duplex { get; set; }
    public IFormFile? File { get; set; }
}
