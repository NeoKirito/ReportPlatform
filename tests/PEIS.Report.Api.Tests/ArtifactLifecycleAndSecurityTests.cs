using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PEIS.Report.Api.Printing;
using PEIS.Report.Api.Storage;
using PEIS.Report.Contracts;
using Xunit;

namespace PEIS.Report.Api.Tests;

public sealed class ArtifactLifecycleAndSecurityTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"peis-artifacts-{Guid.NewGuid():N}");

    [Fact]
    public async Task Expired_artifact_is_deleted_but_active_job_artifact_is_retained()
    {
        var environment = new TestHostEnvironment(_root, isProduction: false);
        var artifacts = new LocalPdfArtifactStore(environment);
        var states = new PrintJobStateStore(Path.Combine(_root, "state.db"));
        var expired = await artifacts.SaveAsync([1, 2, 3], "expired.pdf", CancellationToken.None);
        var active = await artifacts.SaveAsync([4, 5, 6], "active.pdf", CancellationToken.None);
        var artifactPath = Path.Combine(_root, ".runtime", "pdf-artifacts", $"{expired:N}.pdf");
        File.SetLastWriteTimeUtc(artifactPath, DateTime.UtcNow.AddHours(-3));

        var jobId = Guid.NewGuid();
        var targetId = Guid.NewGuid();
        var target = new PrintTargetResult(jobId, targetId, "agent-a", "guide", "A4_GUIDE", "Printer A", PrintTargetStatus.Queued);
        await states.InitializeAsync(new PrintJobInitialization(
            new PrintJobRecord(jobId, "REGISTRATION_PRINT", "REG-01", "agent-a", "test", null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, 0, null, null),
            [new PrintJobTargetState(target, active)]));

        var cleanup = new PdfArtifactCleanupService(artifacts, states,
            Options.Create(new PdfArtifactStoreOptions { RetentionHours = 1, CleanupIntervalMinutes = 1 }),
            NullLogger<PdfArtifactCleanupService>.Instance);
        await cleanup.RunOnceAsync();

        Assert.Null(await artifacts.OpenAsync(expired, CancellationToken.None));
        Assert.NotNull(await artifacts.OpenAsync(active, CancellationToken.None));
    }

    [Fact]
    public async Task Cleanup_failure_is_isolated_from_api_host()
    {
        var environment = new TestHostEnvironment(_root, isProduction: false);
        var states = new PrintJobStateStore(Path.Combine(_root, "state.db"));
        var cleanup = new PdfArtifactCleanupService(new ThrowingArtifactStore(), states,
            Options.Create(new PdfArtifactStoreOptions()), NullLogger<PdfArtifactCleanupService>.Instance);

        var exception = await Record.ExceptionAsync(() => cleanup.RunOnceAsync());

        Assert.Null(exception);
    }

    [Fact]
    public void Download_signature_is_agent_bound_and_production_requires_key()
    {
        var development = new ArtifactDownloadAuthorizer(
            new TestHostEnvironment(_root, isProduction: false),
            Options.Create(new ArtifactAccessOptions { SigningKey = "unit-test-key", DownloadLifetimeMinutes = 10 }));
        var artifactId = Guid.NewGuid();
        var path = development.CreateDownloadPath(artifactId, "agent-a");
        var query = ParseQuery(path);

        Assert.True(development.IsAuthorized(artifactId, "agent-a", long.Parse(query["expires"]), query["signature"]));
        Assert.False(development.IsAuthorized(artifactId, "agent-b", long.Parse(query["expires"]), query["signature"]));
        Assert.False(development.IsAuthorized(artifactId, "agent-a", DateTimeOffset.UtcNow.AddMinutes(-1).ToUnixTimeSeconds(), query["signature"]));

        var production = new ArtifactDownloadAuthorizer(
            new TestHostEnvironment(_root, isProduction: true),
            Options.Create(new ArtifactAccessOptions()));
        Assert.Throws<InvalidOperationException>(() => production.CreateDownloadPath(artifactId, "agent-a"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private static Dictionary<string, string> ParseQuery(string path)
        => path.Split('?', 2)[1]
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(x => x.Split('=', 2))
            .ToDictionary(x => x[0], x => Uri.UnescapeDataString(x[1]), StringComparer.Ordinal);

    private sealed class TestHostEnvironment(string root, bool isProduction) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = isProduction ? "Production" : "Development";
        public string ApplicationName { get; set; } = "PEIS.Report.Api.Tests";
        public string ContentRootPath { get; set; } = root;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private sealed class ThrowingArtifactStore : IPdfArtifactStore
    {
        public Task<Guid> SaveAsync(byte[] pdf, string fileName, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<PdfArtifact?> OpenAsync(Guid artifactId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyCollection<PdfArtifactMetadata>> ListAsync(CancellationToken cancellationToken) => throw new IOException("disk unavailable");
        public Task<bool> DeleteAsync(Guid artifactId, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
