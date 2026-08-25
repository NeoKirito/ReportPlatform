using PEIS.Report.Api.Printing;

namespace PEIS.Report.Api.Storage;

public interface IPdfArtifactStore
{
    Task<Guid> SaveAsync(byte[] pdf, string fileName, CancellationToken cancellationToken);
    Task<PdfArtifact?> OpenAsync(Guid artifactId, CancellationToken cancellationToken);
    Task<IReadOnlyCollection<PdfArtifactMetadata>> ListAsync(CancellationToken cancellationToken);
    Task<bool> DeleteAsync(Guid artifactId, CancellationToken cancellationToken);
}

public sealed record PdfArtifact(Stream Stream, string FileName, long Length) : IAsyncDisposable
{
    public ValueTask DisposeAsync() => Stream.DisposeAsync();
}
