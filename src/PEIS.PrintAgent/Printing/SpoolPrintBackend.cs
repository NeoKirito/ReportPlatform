using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Diagnostics;

namespace PEIS.PrintAgent.Printing;

/// <summary>
/// Windows print backend: sends PDF directly to the specified printer
/// using the bundled headless print engine (tools/SumatraPDF.exe).
/// Does NOT rely on shell file association (printto verb), completely avoiding
/// WPS Office or third-party PDF reader GUI popups.
/// </summary>
public sealed class SpoolPrintBackend(
    IOptions<AgentOptions> options,
    ILogger<SpoolPrintBackend> logger) : IPrintBackend
{
    public async Task PrintAsync(string pdfPath, string printerName, int copies, bool duplex, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(printerName);
        if (!File.Exists(pdfPath))
            throw new FileNotFoundException("PDF file not found for printing.", pdfPath);

        var fullPath = Path.GetFullPath(pdfPath);
        logger.LogInformation("Spool print: {File} -> {Printer}, copies={Copies}, duplex={Duplex}", fullPath, printerName, copies, duplex);

        var executable = ResolvePrintExecutable(options.Value);
        if (!string.IsNullOrWhiteSpace(executable) && File.Exists(executable))
        {
            await PrintViaSumatraAsync(executable, fullPath, printerName, copies, duplex, cancellationToken);
            return;
        }

        // Fallback: If SumatraPDF is missing, log warning and try fallback methods
        logger.LogWarning("未找到轻量级静默打印引擎 (SumatraPDF)，尝试备用方案打印。注意：若系统默认安装了 WPS Office，可能会调起 WPS 窗口。建议确保 tools/SumatraPDF.exe 存在。");
        await PrintViaFallbackAsync(fullPath, printerName, copies, cancellationToken);
    }

    private async Task PrintViaSumatraAsync(
        string executable,
        string fullPath,
        string printerName,
        int copies,
        bool duplex,
        CancellationToken cancellationToken)
    {
        var settings = new List<string>();
        if (copies > 1) settings.Add($"{copies}x");
        if (duplex) settings.Add("duplex");

        var settingsArg = settings.Count > 0 ? $"-print-settings \"{string.Join(",", settings)}\" " : "";
        var arguments = $"-print-to \"{printerName}\" -silent -exit-when-done {settingsArg}\"{fullPath}\"";

        logger.LogInformation("Starting headless print engine: {Executable} {Arguments}", executable, arguments);

        var psi = new ProcessStartInfo
        {
            FileName = executable,
            Arguments = arguments,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        };

        using var process = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start headless print engine.");
        await process.WaitForExitAsync(cancellationToken);

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"Silent print backend '{Path.GetFileName(executable)}' exited with error code {process.ExitCode}.");
        }
    }

    private async Task PrintViaFallbackAsync(
        string fullPath,
        string printerName,
        int copies,
        CancellationToken cancellationToken)
    {
        for (var i = 0; i < copies; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var psi = new ProcessStartInfo
            {
                FileName = fullPath,
                Verb = "printto",
                Arguments = $"\"{printerName}\"",
                UseShellExecute = true,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };
            using var process = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start print process.");
            await process.WaitForExitAsync(cancellationToken);
        }
    }

    /// <summary>
    /// Locates the headless print engine (SumatraPDF.exe).
    /// </summary>
    public static string? ResolvePrintExecutable(AgentOptions? cfg)
    {
        // 1. Explicitly configured path
        if (!string.IsNullOrWhiteSpace(cfg?.PrintBackend.Executable) &&
            File.Exists(cfg.PrintBackend.Executable))
        {
            return Path.GetFullPath(cfg.PrintBackend.Executable);
        }

        // 2. Probing application & tools folders
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "tools", "SumatraPDF.exe"),
            Path.Combine(AppContext.BaseDirectory, "SumatraPDF.exe"),
            Path.Combine(AppContext.BaseDirectory, "..", "tools", "SumatraPDF.exe"),
            Path.Combine(AppContext.BaseDirectory, "..", "app", "tools", "SumatraPDF.exe"),
            Path.Combine(Environment.CurrentDirectory, "tools", "SumatraPDF.exe"),
            Path.Combine(Environment.CurrentDirectory, "SumatraPDF.exe"),
            Path.Combine(AppContext.BaseDirectory, "tools", "SumatraPDF-3.5.2-64.exe"),
            Path.Combine(AppContext.BaseDirectory, "SumatraPDF-3.5.2-64.exe"),
        };

        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate)) return Path.GetFullPath(candidate);
        }

        // 3. System installed paths
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        var systemCandidates = new[]
        {
            Path.Combine(programFiles, "SumatraPDF", "SumatraPDF.exe"),
            Path.Combine(programFilesX86, "SumatraPDF", "SumatraPDF.exe"),
            Path.Combine(localAppData, "SumatraPDF", "SumatraPDF.exe"),
        };

        foreach (var candidate in systemCandidates)
        {
            if (File.Exists(candidate)) return Path.GetFullPath(candidate);
        }

        // 4. Probe PATH
        var pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (!string.IsNullOrWhiteSpace(pathEnv))
        {
            foreach (var dir in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                try
                {
                    var combined = Path.Combine(dir.Trim(), "SumatraPDF.exe");
                    if (File.Exists(combined)) return Path.GetFullPath(combined);
                }
                catch { }
            }
        }

        return null;
    }
}
