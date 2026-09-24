using System.Text;
using System.Text.RegularExpressions;

namespace PEIS.Report.Contracts;

public sealed record AgentInstallerConfig
{
    public const string BeginMarker = "###PEIS_CONFIG_BEGIN###";
    public const string EndMarker = "###PEIS_CONFIG_END###";

    public string ServerUrl { get; set; } = "http://localhost:82";
    public string? StationId { get; set; }
    public bool? SilentPrint { get; set; }
    public string? PrintBackend { get; set; }
    public string? DefaultPrinter { get; set; }
}

public sealed record AgentProbeResponse
{
    public bool Online { get; init; }
    public string? ClientIp { get; init; }
    public string? StationId { get; init; }
    public string? AgentId { get; init; }
    public string? MachineName { get; init; }
    public string? Version { get; init; }
    public DateTimeOffset? LastSeenAt { get; init; }
    public IReadOnlyList<PrinterDescriptor> Printers { get; init; } = Array.Empty<PrinterDescriptor>();
    public IReadOnlyDictionary<string, string> PrinterBindings { get; init; } = new Dictionary<string, string>();
    public string ServerUrl { get; init; } = string.Empty;
    public string DownloadUrl { get; init; } = string.Empty;
    public string Message { get; init; } = string.Empty;
}

public static class AgentInstallerPackageHelper
{
    private static readonly Regex HostPortFileNameRegex = new(
        @"_(\d{1,3}-\d{1,3}-\d{1,3}-\d{1,3})_(\d+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static byte[] EncodeOverlay(AgentInstallerConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        var sb = new StringBuilder();
        sb.AppendLine();
        sb.AppendLine(AgentInstallerConfig.BeginMarker);
        if (!string.IsNullOrWhiteSpace(config.ServerUrl))
            sb.AppendLine($"ServerUrl={config.ServerUrl.Trim()}");
        if (!string.IsNullOrWhiteSpace(config.StationId))
            sb.AppendLine($"StationId={config.StationId.Trim()}");
        if (config.SilentPrint.HasValue)
            sb.AppendLine($"SilentPrint={config.SilentPrint.Value.ToString().ToLowerInvariant()}");
        if (!string.IsNullOrWhiteSpace(config.PrintBackend))
            sb.AppendLine($"PrintBackend={config.PrintBackend.Trim()}");
        if (!string.IsNullOrWhiteSpace(config.DefaultPrinter))
            sb.AppendLine($"DefaultPrinter={config.DefaultPrinter.Trim()}");
        sb.AppendLine(AgentInstallerConfig.EndMarker);
        sb.AppendLine();
        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    public static AgentInstallerConfig? TryExtractConfigFromStream(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanSeek || stream.Length == 0) return null;

        const int maxTailSize = 64 * 1024;
        var readLength = (int)Math.Min(stream.Length, maxTailSize);
        stream.Seek(-readLength, SeekOrigin.End);

        var buffer = new byte[readLength];
        var bytesRead = 0;
        while (bytesRead < readLength)
        {
            var n = stream.Read(buffer, bytesRead, readLength - bytesRead);
            if (n <= 0) break;
            bytesRead += n;
        }

        var text = Encoding.UTF8.GetString(buffer, 0, bytesRead);
        return TryExtractConfigFromText(text);
    }

    public static AgentInstallerConfig? TryExtractConfigFromText(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        var beginIdx = text.LastIndexOf(AgentInstallerConfig.BeginMarker, StringComparison.Ordinal);
        if (beginIdx < 0) return null;

        var endIdx = text.IndexOf(AgentInstallerConfig.EndMarker, beginIdx, StringComparison.Ordinal);
        if (endIdx < 0) return null;

        var configSection = text.Substring(
            beginIdx + AgentInstallerConfig.BeginMarker.Length,
            endIdx - (beginIdx + AgentInstallerConfig.BeginMarker.Length));

        var config = new AgentInstallerConfig();
        var hasAny = false;

        using var reader = new StringReader(configSection);
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith('#') || trimmed.StartsWith(';'))
                continue;

            var sep = trimmed.IndexOf('=');
            if (sep <= 0) continue;

            var key = trimmed[..sep].Trim();
            var val = trimmed[(sep + 1)..].Trim();

            if (string.Equals(key, "ServerUrl", StringComparison.OrdinalIgnoreCase))
            {
                config.ServerUrl = val;
                hasAny = true;
            }
            else if (string.Equals(key, "StationId", StringComparison.OrdinalIgnoreCase))
            {
                config.StationId = val;
                hasAny = true;
            }
            else if (string.Equals(key, "SilentPrint", StringComparison.OrdinalIgnoreCase))
            {
                if (bool.TryParse(val, out var b)) config.SilentPrint = b;
                hasAny = true;
            }
            else if (string.Equals(key, "PrintBackend", StringComparison.OrdinalIgnoreCase))
            {
                config.PrintBackend = val;
                hasAny = true;
            }
            else if (string.Equals(key, "DefaultPrinter", StringComparison.OrdinalIgnoreCase))
            {
                config.DefaultPrinter = val;
                hasAny = true;
            }
        }

        return hasAny ? config : null;
    }

    public static AgentInstallerConfig? TryExtractConfigFromFileName(string? fileNameOrPath)
    {
        if (string.IsNullOrWhiteSpace(fileNameOrPath)) return null;
        var fileName = Path.GetFileName(fileNameOrPath);
        var match = HostPortFileNameRegex.Match(fileName);
        if (match.Success)
        {
            var ip = match.Groups[1].Value.Replace('-', '.');
            var port = match.Groups[2].Value;
            return new AgentInstallerConfig
            {
                ServerUrl = $"http://{ip}:{port}"
            };
        }
        return null;
    }

    public static string GenerateDownloadFileName(string serverUrl)
    {
        if (Uri.TryCreate(serverUrl, UriKind.Absolute, out var uri))
        {
            var hostSafe = uri.Host.Replace('.', '-');
            var port = uri.Port > 0 ? uri.Port : 80;
            return $"PEIS-PrintAgent-Setup_{hostSafe}_{port}.exe";
        }
        return "PEIS-PrintAgent-Setup.exe";
    }
}
