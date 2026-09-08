using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using PEIS.Report.Contracts;
using System.Security.Cryptography;

namespace PEIS.PrintAgent.Printing;

/// <summary>
/// Downloads the distinct PDF artifacts of one received print batch. The degree of parallelism is bounded so that
/// an A4 report and its barcode/label can transfer concurrently without allowing a malformed scenario to saturate a
/// workstation's network, disk, or memory.
/// </summary>
public sealed class PrintArtifactDownloader(
    IHttpClientFactory httpClientFactory,
    ILogger<PrintArtifactDownloader> logger)
{
    public async Task<string> DownloadDeliveryAsync(
        string serverUrl,
        string workDirectory,
        ReportDeliveryDispatch delivery,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serverUrl);
        ArgumentException.ThrowIfNullOrWhiteSpace(workDirectory);
        ArgumentNullException.ThrowIfNull(delivery);
        Directory.CreateDirectory(workDirectory);

        var path = Path.Combine(workDirectory, $"{delivery.ArtifactId:N}.pdf");
        if (File.Exists(path) && await IsValidAsync(path, delivery, cancellationToken).ConfigureAwait(false))
            return path;
        if (File.Exists(path)) File.Delete(path);

        var temporaryPath = $"{path}.{Guid.NewGuid():N}.partial";
        try
        {
            var http = httpClientFactory.CreateClient("report-api");
            using var response = await http.GetAsync(
                new Uri(new Uri(serverUrl), delivery.DownloadPath),
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentType?.MediaType is { } mediaType &&
                !string.Equals(mediaType, "application/pdf", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Artifact content type is '{mediaType}', expected application/pdf.");

            await using var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            await using (var output = new FileStream(
                temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
            }

            if (!await IsValidAsync(temporaryPath, delivery, cancellationToken).ConfigureAwait(false))
                throw new InvalidDataException("Downloaded PDF length, signature, or SHA-256 does not match the dispatch.");
            File.Move(temporaryPath, path, overwrite: true);
            return path;
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }

    private static async Task<bool> IsValidAsync(
        string path,
        ReportDeliveryDispatch delivery,
        CancellationToken cancellationToken)
    {
        var info = new FileInfo(path);
        if (info.Length != delivery.Length || info.Length < 5) return false;
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var signature = new byte[5];
        if (await stream.ReadAsync(signature, cancellationToken).ConfigureAwait(false) != signature.Length ||
            !signature.AsSpan().SequenceEqual("%PDF-"u8)) return false;
        stream.Position = 0;
        var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
        return string.Equals(hash, delivery.Sha256, StringComparison.OrdinalIgnoreCase);
    }

    public async Task<IReadOnlyDictionary<Guid, string>> DownloadAsync(
        string serverUrl,
        string workDirectory,
        int maxConcurrentDownloads,
        IReadOnlyList<PrintDocumentDispatch> documents,
        Func<PrintDocumentDispatch, Task> markDownloadingAsync,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serverUrl);
        ArgumentException.ThrowIfNullOrWhiteSpace(workDirectory);
        ArgumentNullException.ThrowIfNull(documents);
        ArgumentNullException.ThrowIfNull(markDownloadingAsync);

        Directory.CreateDirectory(workDirectory);
        var groups = documents.GroupBy(x => x.ArtifactId).ToArray();
        var result = new ConcurrentDictionary<Guid, string>();
        var degreeOfParallelism = Math.Clamp(maxConcurrentDownloads, 1, 8);

        using var gate = new SemaphoreSlim(degreeOfParallelism, degreeOfParallelism);
        await Task.WhenAll(groups.Select(async artifactGroup =>
        {
            var first = artifactGroup.First();
            foreach (var document in artifactGroup)
                await markDownloadingAsync(document).ConfigureAwait(false);

            var path = Path.Combine(workDirectory, $"{first.ArtifactId:N}.pdf");
            if (File.Exists(path))
            {
                result[first.ArtifactId] = path;
                logger.LogDebug("Reusing cached print artifact {ArtifactId}", first.ArtifactId);
                return;
            }

            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            var temporaryPath = $"{path}.{Guid.NewGuid():N}.partial";
            try
            {
                // A unique artifact id is used as the filename, so concurrent distinct downloads never share a file.
                // Publish only after the stream has completed, never letting a failed partial file enter the cache.
                var http = httpClientFactory.CreateClient("report-api");
                using var response = await http.GetAsync(
                    new Uri(new Uri(serverUrl), first.DownloadPath),
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();

                await using var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                await using (var output = new FileStream(
                    temporaryPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    bufferSize: 128 * 1024,
                    FileOptions.Asynchronous | FileOptions.SequentialScan))
                {
                    await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
                }

                File.Move(temporaryPath, path, overwrite: true);
                result[first.ArtifactId] = path;
            }
            finally
            {
                if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
                gate.Release();
            }
        })).ConfigureAwait(false);

        return result;
    }
}
