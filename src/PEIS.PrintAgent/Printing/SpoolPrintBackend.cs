using Microsoft.Extensions.Logging;
using System.Diagnostics;

namespace PEIS.PrintAgent.Printing;

/// <summary>
/// Windows print spooler backend: sends PDF directly to the specified printer
/// using the system's default PDF print handler (printto verb).
/// Falls back to ShellExecute with 'print' verb if direct spooler fails.
/// </summary>
public sealed class SpoolPrintBackend(ILogger<SpoolPrintBackend> logger) : IPrintBackend
{
    public async Task PrintAsync(string pdfPath, string printerName, int copies, bool duplex, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(printerName);
        if (!File.Exists(pdfPath))
            throw new FileNotFoundException("PDF file not found for printing.", pdfPath);

        var fullPath = Path.GetFullPath(pdfPath);
        logger.LogInformation("Spool print: {File} -> {Printer}, copies={Copies}, duplex={Duplex}", fullPath, printerName, copies, duplex);

        // Attempt 1: Use rundll32 printui.dll to print directly to the printer spooler
        for (var i = 0; i < copies; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await PrintViaShellAsync(fullPath, printerName, cancellationToken);
        }
    }

    private async Task PrintViaShellAsync(string fullPath, string printerName, CancellationToken cancellationToken)
    {
        try
        {
            // Use Process.Start with UseShellExecute=true and verb="printto"
            // This sends the PDF to the Windows print spooler for the specified printer
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

            if (process.ExitCode != 0)
            {
                logger.LogWarning("printto verb exited with code {Code}, trying fallback 'print' verb", process.ExitCode);
                await PrintViaFallbackAsync(fullPath, cancellationToken);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "printto failed for {Printer}, trying fallback 'print' verb", printerName);
            await PrintViaFallbackAsync(fullPath, cancellationToken);
        }
    }

    private async Task PrintViaFallbackAsync(string fullPath, CancellationToken cancellationToken)
    {
        // Fallback: use 'print' verb (opens default PDF handler's print dialog briefly)
        var psi = new ProcessStartInfo
        {
            FileName = fullPath,
            Verb = "print",
            UseShellExecute = true,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        };
        using var process = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start fallback print process.");
        await process.WaitForExitAsync(cancellationToken);
    }
}
