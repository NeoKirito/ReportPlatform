using System.Collections.Concurrent;
using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using PEIS.PrintAgent.Printing;
using PEIS.Report.Contracts;
using Xunit;
using System.Security.Cryptography;

namespace PEIS.PrintAgent.Tests;

public sealed class PrintArtifactDownloaderTests
{
    [Fact]
    public async Task Different_artifacts_download_in_bounded_parallelism()
    {
        var handler = new DelayedPdfHandler(TimeSpan.FromMilliseconds(50));
        var downloader = CreateDownloader(handler);
        var folder = NewTemporaryFolder();
        try
        {
            var documents = Enumerable.Range(0, 3)
                .Select(index => CreateDocument(Guid.NewGuid(), $"/artifacts/{index}"))
                .ToArray();
            var statuses = new ConcurrentQueue<Guid>();

            var paths = await downloader.DownloadAsync(
                "https://report.test",
                folder,
                maxConcurrentDownloads: 2,
                documents,
                document =>
                {
                    statuses.Enqueue(document.TargetId);
                    return Task.CompletedTask;
                },
                CancellationToken.None);

            Assert.Equal(3, handler.RequestCount);
            Assert.Equal(2, handler.MaxActive);
            Assert.Equal(3, statuses.Count);
            Assert.All(paths.Values, path => Assert.True(File.Exists(path)));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public async Task Same_artifact_is_downloaded_once_but_all_targets_are_marked_downloading()
    {
        var handler = new DelayedPdfHandler(TimeSpan.Zero);
        var downloader = CreateDownloader(handler);
        var folder = NewTemporaryFolder();
        try
        {
            var artifactId = Guid.NewGuid();
            var documents = new[]
            {
                CreateDocument(artifactId, "/artifacts/shared"),
                CreateDocument(artifactId, "/artifacts/shared")
            };
            var statuses = new ConcurrentQueue<Guid>();

            var paths = await downloader.DownloadAsync(
                "https://report.test",
                folder,
                maxConcurrentDownloads: 2,
                documents,
                document =>
                {
                    statuses.Enqueue(document.TargetId);
                    return Task.CompletedTask;
                },
                CancellationToken.None);

            Assert.Single(paths);
            Assert.Equal(1, handler.RequestCount);
            Assert.Equal(2, statuses.Count);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public async Task Desktop_delivery_validates_pdf_length_and_sha256_before_publishing_cache_file()
    {
        byte[] pdf = [37, 80, 68, 70, 45, 49, 46, 52, 10];
        var handler = new StaticPdfHandler(pdf);
        var downloader = CreateDownloader(handler);
        var folder = NewTemporaryFolder();
        try
        {
            var delivery = CreateDelivery(pdf, Convert.ToHexString(SHA256.HashData(pdf)));
            var path = await downloader.DownloadDeliveryAsync("https://report.test", folder, delivery, CancellationToken.None);

            Assert.True(File.Exists(path));
            Assert.Equal(pdf, await File.ReadAllBytesAsync(path));
            Assert.DoesNotContain(Directory.EnumerateFiles(folder), file => file.EndsWith(".partial", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public async Task Desktop_delivery_rejects_tampered_pdf_and_removes_partial_file()
    {
        byte[] pdf = [37, 80, 68, 70, 45, 49, 46, 52, 10];
        var downloader = CreateDownloader(new StaticPdfHandler(pdf));
        var folder = NewTemporaryFolder();
        try
        {
            var delivery = CreateDelivery(pdf, new string('0', 64));
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                downloader.DownloadDeliveryAsync("https://report.test", folder, delivery, CancellationToken.None));

            Assert.Empty(Directory.EnumerateFiles(folder));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    private static PrintArtifactDownloader CreateDownloader(HttpMessageHandler handler)
        => new(new StaticHttpClientFactory(new HttpClient(handler)), NullLogger<PrintArtifactDownloader>.Instance);

    private static PrintDocumentDispatch CreateDocument(Guid artifactId, string downloadPath)
        => new(Guid.NewGuid(), artifactId, downloadPath, "test", "TEST", "ROLE", "Printer", 1, false);

    private static ReportDeliveryDispatch CreateDelivery(byte[] pdf, string sha256)
        => new(Guid.NewGuid(), Guid.NewGuid(), "/artifact/desktop", "report.pdf", sha256, pdf.Length,
            DateTimeOffset.UtcNow.AddMinutes(5), ReportDeliveryAction.Preview);

    private static string NewTemporaryFolder()
    {
        var folder = Path.Combine(Path.GetTempPath(), "PEIS.ReportPlatform.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        return folder;
    }

    private sealed class StaticHttpClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class DelayedPdfHandler(TimeSpan delay) : HttpMessageHandler
    {
        private int _active;
        private int _maxActive;
        private int _requestCount;

        public int MaxActive => Volatile.Read(ref _maxActive);
        public int RequestCount => Volatile.Read(ref _requestCount);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _requestCount);
            var active = Interlocked.Increment(ref _active);
            SetMaximum(ref _maxActive, active);
            try
            {
                if (delay > TimeSpan.Zero)
                    await Task.Delay(delay, cancellationToken);
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent([37, 80, 68, 70, 45])
                };
            }
            finally
            {
                Interlocked.Decrement(ref _active);
            }
        }

        private static void SetMaximum(ref int location, int candidate)
        {
            var current = Volatile.Read(ref location);
            while (candidate > current)
            {
                var observed = Interlocked.CompareExchange(ref location, candidate, current);
                if (observed == current) return;
                current = observed;
            }
        }
    }

    private sealed class StaticPdfHandler(byte[] pdf) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var content = new ByteArrayContent(pdf);
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/pdf");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }
    }
}
