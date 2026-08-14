namespace PEIS.PrintAgent.Printing;

public interface IPrintBackend
{
    Task PrintAsync(string pdfPath, string printerName, int copies, bool duplex, CancellationToken cancellationToken);
}
