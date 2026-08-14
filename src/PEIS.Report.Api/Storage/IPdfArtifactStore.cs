namespace PEIS.Report.Api.Storage;

public interface IPdfArtifactStore
{
    Task<Guid> SaveAsync(byte[] pdf, string fileName, CancellationToken cancellationToken);
    Task<PdfArtifact?> OpenAsync(Guid artifactId, CancellationToken cancellationToken);
}

public sealed record PdfArtifact(Stream Stream, string FileName, long Length) : IAsyncDisposable
{
    public ValueTask DisposeAsync() => Stream.DisposeAsync();
}
