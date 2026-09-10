using Microsoft.Extensions.Logging;
using System.Diagnostics;
using Microsoft.Extensions.Options;

namespace PEIS.PrintAgent.Printing;

/// <summary>
/// Adapter for a hospital-approved silent PDF printing executable.
/// The exact command is environment-specific, so it is configuration-driven.
/// Supported placeholders: {file}, {printer}, {copies}, {duplex}.
/// </summary>
public sealed class CommandPrintBackend(IOptions<AgentOptions> options, ILogger<CommandPrintBackend> logger) : IPrintBackend
{
    public async Task PrintAsync(string pdfPath, string printerName, int copies, bool duplex, CancellationToken cancellationToken)
    {
        var backend = options.Value.PrintBackend;
        var executable = backend.Executable;
        if (string.IsNullOrWhiteSpace(executable) || !File.Exists(executable))
        {
            executable = SpoolPrintBackend.ResolvePrintExecutable(options.Value);
        }

        if (string.IsNullOrWhiteSpace(executable))
            throw new InvalidOperationException("PrintBackend:Executable is required for Command mode, or ensure tools/SumatraPDF.exe exists.");

        var template = backend.ArgumentsTemplate;
        if (string.IsNullOrWhiteSpace(template) || template == "{file} {printer} {copies}")
        {
            var isSumatra = Path.GetFileNameWithoutExtension(executable).Contains("Sumatra", StringComparison.OrdinalIgnoreCase);
            template = isSumatra
                ? "-print-to {printer} -silent -exit-when-done -print-settings {copies}x {file}"
                : "{file} {printer} {copies}";
        }

        var arguments = template
            .Replace("{file}", Quote(pdfPath), StringComparison.Ordinal)
            .Replace("{printer}", Quote(printerName), StringComparison.Ordinal)
            .Replace("{copies}", copies.ToString(), StringComparison.Ordinal)
            .Replace("{duplex}", duplex ? "true" : "false", StringComparison.Ordinal);

        logger.LogInformation("Starting print backend {Executable} for printer {Printer}", executable, printerName);
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = executable,
            Arguments = arguments,
            UseShellExecute = false,
            CreateNoWindow = true
        }) ?? throw new InvalidOperationException("Failed to start print backend.");

        await process.WaitForExitAsync(cancellationToken);
        if (process.ExitCode != 0) throw new InvalidOperationException($"Print backend exited with code {process.ExitCode}.");
    }

    private static string Quote(string value) => $"\"{value.Replace("\"", "\\\"")}\"";
}
