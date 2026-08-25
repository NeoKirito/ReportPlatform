using PEIS.Report.Api.Printing;

namespace PEIS.Report.Api.Storage;

/// <summary>
/// Filesystem-backed PDF storage. Artifact names are server-issued GUIDs only; caller-supplied filenames are used
/// exclusively as sanitized download names and never participate in a filesystem path.
/// </summary>
public sealed class LocalPdfArtifactStore(IHostEnvironment environment) : IPdfArtifactStore
{
    private readonly string _root = Path.Combine(environment.ContentRootPath, ".runtime", "pdf-artifacts");

    public async Task<Guid> SaveAsync(byte[] pdf, string fileName, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(pdf);
        Directory.CreateDirectory(_root);
        var id = Guid.NewGuid();
        await File.WriteAllBytesAsync(PdfPath(id), pdf, cancellationToken);
        await File.WriteAllTextAsync(NamePath(id), Sanitize(fileName), cancellationToken);
        return id;
    }

    public Task<PdfArtifact?> OpenAsync(Guid artifactId, CancellationToken cancellationToken)
    {
        var pdfPath = PdfPath(artifactId);
        if (!File.Exists(pdfPath)) return Task.FromResult<PdfArtifact?>(null);

        var namePath = NamePath(artifactId);
        var name = File.Exists(namePath) ? File.ReadAllText(namePath) : $"{artifactId:N}.pdf";
        var stream = new FileStream(pdfPath, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Task.FromResult<PdfArtifact?>(new PdfArtifact(stream, name, stream.Length));
    }

    public Task<IReadOnlyCollection<PdfArtifactMetadata>> ListAsync(CancellationToken cancellationToken)
    {
        if (!Directory.Exists(_root)) return Task.FromResult<IReadOnlyCollection<PdfArtifactMetadata>>(Array.Empty<PdfArtifactMetadata>());
        var artifacts = new List<PdfArtifactMetadata>();
        foreach (var path in Directory.EnumerateFiles(_root, "*.pdf", SearchOption.TopDirectoryOnly))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var idText = Path.GetFileNameWithoutExtension(path);
            if (!Guid.TryParseExact(idText, "N", out var id)) continue;
            var file = new FileInfo(path);
            var namePath = NamePath(id);
            var name = File.Exists(namePath) ? File.ReadAllText(namePath) : $"{id:N}.pdf";
            artifacts.Add(new PdfArtifactMetadata(id, name, file.Length, file.LastWriteTimeUtc));
        }
        return Task.FromResult<IReadOnlyCollection<PdfArtifactMetadata>>(artifacts);
    }

    public Task<bool> DeleteAsync(Guid artifactId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var pdfPath = PdfPath(artifactId);
        var namePath = NamePath(artifactId);
        if (!File.Exists(pdfPath)) return Task.FromResult(false);
        File.Delete(pdfPath);
        if (File.Exists(namePath)) File.Delete(namePath);
        return Task.FromResult(true);
    }

    private string PdfPath(Guid artifactId) => Path.Combine(_root, $"{artifactId:N}.pdf");
    private string NamePath(Guid artifactId) => Path.Combine(_root, $"{artifactId:N}.name");

    private static string Sanitize(string value)
    {
        foreach (var c in Path.GetInvalidFileNameChars()) value = value.Replace(c, '_');
        return string.IsNullOrWhiteSpace(value) ? "report.pdf" : value;
    }
}
