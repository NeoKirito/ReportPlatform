namespace PEIS.PrintAgent.Previewing;

public interface IPdfPreviewer
{
    Task OpenAsync(PdfPreviewRequest request, CancellationToken cancellationToken);
}

public sealed record PdfPreviewRequest(
    string PdfPath,
    string Title,
    Func<string, Task>? PrintAsync = null,
    IReadOnlyList<string>? PrinterNames = null,
    string? SelectedPrinterName = null);
