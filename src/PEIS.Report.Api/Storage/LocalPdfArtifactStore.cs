namespace PEIS.Report.Api.Storage;

public sealed class LocalPdfArtifactStore(IHostEnvironment environment) : IPdfArtifactStore
{
    private readonly string _root = Path.Combine(environment.ContentRootPath, ".runtime", "pdf-artifacts");

    public async Task<Guid> SaveAsync(byte[] pdf, string fileName, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_root);
        var id = Guid.NewGuid();
        await File.WriteAllBytesAsync(Path.Combine(_root, $"{id:N}.pdf"), pdf, cancellationToken);
        await File.WriteAllTextAsync(Path.Combine(_root, $"{id:N}.name"), Sanitize(fileName), cancellationToken);
        return id;
    }

    public Task<PdfArtifact?> OpenAsync(Guid artifactId, CancellationToken cancellationToken)
    {
        var pdfPath = Path.Combine(_root, $"{artifactId:N}.pdf");
        if (!File.Exists(pdfPath)) return Task.FromResult<PdfArtifact?>(null);

        var namePath = Path.Combine(_root, $"{artifactId:N}.name");
        var name = File.Exists(namePath) ? File.ReadAllText(namePath) : $"{artifactId:N}.pdf";
        var stream = new FileStream(pdfPath, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Task.FromResult<PdfArtifact?>(new PdfArtifact(stream, name, stream.Length));
    }

    private static string Sanitize(string value)
    {
        foreach (var c in Path.GetInvalidFileNameChars()) value = value.Replace(c, '_');
        return string.IsNullOrWhiteSpace(value) ? "report.pdf" : value;
    }
}
