using Microsoft.AspNetCore.Mvc;
using PEIS.Report.Api.Printing;
using PEIS.Report.Contracts;

namespace PEIS.Report.Api.Controllers;

[ApiController]
[Route("api/agent")]
public sealed class AgentInstallController(
    AgentRegistry registry,
    IConfiguration configuration,
    IWebHostEnvironment environment) : ControllerBase
{
    [HttpGet("probe")]
    [HttpGet("status")]
    public IActionResult Probe([FromQuery] string? stationId = null, [FromQuery] string? clientIp = null)
    {
        var effectiveIp = clientIp;
        if (string.IsNullOrWhiteSpace(effectiveIp))
        {
            var forwarded = Request.Headers["X-Forwarded-For"].FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(forwarded))
                effectiveIp = forwarded.Split(',', 2, StringSplitOptions.TrimEntries)[0];
            else
                effectiveIp = HttpContext.Connection.RemoteIpAddress?.ToString();
        }
        effectiveIp = AgentRegistry.NormalizeAddress(effectiveIp);

        AgentRegistry.AgentState? agent = null;
        if (!string.IsNullOrWhiteSpace(stationId))
        {
            agent = registry.FindByStation(stationId.Trim());
        }
        if (agent == null && !string.IsNullOrWhiteSpace(effectiveIp))
        {
            agent = registry.FindByClientAddress(effectiveIp);
        }

        var serverUrl = ResolveServerUrl();
        var downloadUrl = $"{serverUrl.TrimEnd('/')}/api/agent/download-setup";

        return Ok(new AgentProbeResponse
        {
            Online = agent != null,
            ClientIp = effectiveIp,
            StationId = agent?.StationId ?? stationId,
            AgentId = agent?.AgentId,
            MachineName = agent?.MachineName,
            Version = agent?.Version,
            LastSeenAt = agent?.LastSeenAt,
            Printers = agent?.Printers ?? Array.Empty<PrinterDescriptor>(),
            PrinterBindings = agent?.PrinterBindings ?? new Dictionary<string, string>(),
            ServerUrl = serverUrl,
            DownloadUrl = downloadUrl,
            Message = agent != null ? "打印助手已在线" : "未检测到本机打印助手"
        });
    }

    [HttpGet("download-setup")]
    public IActionResult DownloadSetup(
        [FromQuery] string? serverUrl = null,
        [FromQuery] string? stationId = null,
        [FromQuery] bool? silentPrint = null,
        [FromQuery] string? printBackend = null)
    {
        var effectiveServerUrl = !string.IsNullOrWhiteSpace(serverUrl) ? serverUrl.Trim() : ResolveServerUrl();
        var setupFilePath = ResolveSetupFilePath();

        if (setupFilePath == null || !System.IO.File.Exists(setupFilePath))
        {
            return NotFound(new
            {
                error = "AgentSetupNotFound",
                message = "打印助手安装包尚未在服务器准备就绪，请先生成或将 PEIS-PrintAgent-Setup.exe 放置到 wwwroot/downloads 目录。"
            });
        }

        var config = new AgentInstallerConfig
        {
            ServerUrl = effectiveServerUrl,
            StationId = stationId,
            SilentPrint = silentPrint,
            PrintBackend = printBackend
        };

        var overlayBytes = AgentInstallerPackageHelper.EncodeOverlay(config);
        var baseFileStream = new FileStream(setupFilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        var overlayStream = new MemoryStream(overlayBytes);
        var combinedStream = new SequenceStream(baseFileStream, overlayStream);
        var downloadFileName = AgentInstallerPackageHelper.GenerateDownloadFileName(effectiveServerUrl);

        return File(combinedStream, "application/vnd.microsoft.portable-executable", downloadFileName);
    }

    private string? ResolveSetupFilePath()
    {
        var configuredPath = configuration["AgentInstall:SetupFilePath"];
        if (!string.IsNullOrWhiteSpace(configuredPath) && System.IO.File.Exists(configuredPath))
            return configuredPath;

        var webRoot = environment.WebRootPath ?? Path.Combine(AppContext.BaseDirectory, "wwwroot");
        var candidates = new[]
        {
            Path.Combine(webRoot, "downloads", "PEIS-PrintAgent-Setup.exe"),
            Path.Combine(AppContext.BaseDirectory, "wwwroot", "downloads", "PEIS-PrintAgent-Setup.exe"),
            Path.Combine(AppContext.BaseDirectory, "downloads", "PEIS-PrintAgent-Setup.exe"),
            Path.Combine(environment.ContentRootPath, "wwwroot", "downloads", "PEIS-PrintAgent-Setup.exe"),
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "artifacts", "PEIS-PrintAgent-Setup.exe"),
            Path.Combine(AppContext.BaseDirectory, "PEIS-PrintAgent-Setup.exe")
        };

        return candidates.FirstOrDefault(System.IO.File.Exists);
    }

    private string ResolveServerUrl()
    {
        var configured = configuration["AgentInstall:ServerUrl"];
        if (!string.IsNullOrWhiteSpace(configured))
            return configured.TrimEnd('/');

        var request = HttpContext.Request;
        var host = request.Host.Value;
        var scheme = request.Scheme;
        if (string.IsNullOrWhiteSpace(host))
        {
            host = "localhost:82";
        }
        return $"{scheme}://{host}".TrimEnd('/');
    }
}
