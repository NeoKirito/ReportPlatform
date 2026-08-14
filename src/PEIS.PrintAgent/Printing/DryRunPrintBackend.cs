namespace PEIS.PrintAgent.Printing;

public sealed class DryRunPrintBackend(ILogger<DryRunPrintBackend> logger) : IPrintBackend
{
    public Task PrintAsync(string pdfPath, string printerName, int copies, bool duplex, CancellationToken cancellationToken)
    {
        logger.LogInformation("DRY-RUN print: {File} -> {Printer}, copies={Copies}, duplex={Duplex}", pdfPath, printerName, copies, duplex);
        return Task.CompletedTask;
    }
}
