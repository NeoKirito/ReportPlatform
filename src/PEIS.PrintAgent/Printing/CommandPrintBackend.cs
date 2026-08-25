using System.Diagnostics;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging;

namespace PEIS.PrintAgent.Printing;

/// <summary>
/// Adapter for a hospital-approved silent PDF printing executable. Command mode is deliberately local-only:
/// no shell is launched, the executable must be an existing absolute path, and argument templates are tokenized
/// into <see cref="ProcessStartInfo.ArgumentList"/> rather than concatenated command lines.
/// </summary>
public sealed class CommandPrintBackend(IOptions<AgentOptions> options, ILogger<CommandPrintBackend> logger) : IPrintBackend
{
    private static readonly HashSet<string> ProhibitedExecutables = new(StringComparer.OrdinalIgnoreCase)
    {
        "cmd.exe", "command.com", "powershell.exe", "pwsh.exe", "wscript.exe", "cscript.exe", "rundll32.exe"
    };

    public async Task PrintAsync(string pdfPath, string printerName, int copies, bool duplex, CancellationToken cancellationToken)
    {
        var backend = options.Value.PrintBackend;
        var startInfo = BuildStartInfo(backend, pdfPath, printerName, copies, duplex);
        logger.LogInformation("Starting approved local print backend {Executable} for printer {Printer}", startInfo.FileName, printerName);
        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Failed to start print backend.");
        await process.WaitForExitAsync(cancellationToken);
        if (process.ExitCode != 0) throw new InvalidOperationException($"Print backend exited with code {process.ExitCode}.");
    }

    public static ProcessStartInfo BuildStartInfo(PrintBackendOptions backend, string pdfPath, string printerName, int copies, bool duplex)
    {
        if (string.IsNullOrWhiteSpace(backend.Executable))
            throw new InvalidOperationException("PrintBackend:Executable is required for Command mode.");
        if (!Path.IsPathFullyQualified(backend.Executable))
            throw new InvalidOperationException("PrintBackend:Executable must be an absolute local path.");

        var executable = Path.GetFullPath(backend.Executable);
        if (ProhibitedExecutables.Contains(Path.GetFileName(executable)))
            throw new InvalidOperationException("Shell and script-host executables are forbidden for Command print mode.");
        if (!File.Exists(executable))
            throw new InvalidOperationException("PrintBackend:Executable does not exist.");
        if (copies < 1 || copies > 100)
            throw new ArgumentOutOfRangeException(nameof(copies), "Copies must be between 1 and 100.");
        if (string.IsNullOrWhiteSpace(pdfPath) || !Path.IsPathFullyQualified(pdfPath))
            throw new InvalidOperationException("PDF input must be an absolute local path.");
        if (string.IsNullOrWhiteSpace(printerName) || printerName.Any(char.IsControl))
            throw new InvalidOperationException("Printer name is invalid.");

        var values = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["{file}"] = pdfPath,
            ["{printer}"] = printerName,
            ["{copies}"] = copies.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["{duplex}"] = duplex ? "true" : "false"
        };
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var token in Tokenize(backend.ArgumentsTemplate))
        {
            if (token.Contains('{') || token.Contains('}'))
            {
                if (!values.TryGetValue(token, out var value))
                    throw new InvalidOperationException($"Unsupported print argument placeholder '{token}'.");
                startInfo.ArgumentList.Add(value);
            }
            else
            {
                startInfo.ArgumentList.Add(token);
            }
        }
        return startInfo;
    }

    private static IEnumerable<string> Tokenize(string? template)
    {
        if (string.IsNullOrWhiteSpace(template)) yield break;
        var buffer = new System.Text.StringBuilder();
        var quoted = false;
        foreach (var character in template)
        {
            if (character == '"')
            {
                quoted = !quoted;
                continue;
            }
            if (char.IsWhiteSpace(character) && !quoted)
            {
                if (buffer.Length > 0)
                {
                    yield return buffer.ToString();
                    buffer.Clear();
                }
                continue;
            }
            if (char.IsControl(character)) throw new InvalidOperationException("Print argument templates cannot contain control characters.");
            buffer.Append(character);
        }
        if (quoted) throw new InvalidOperationException("Print argument template contains an unmatched quote.");
        if (buffer.Length > 0) yield return buffer.ToString();
    }
}
