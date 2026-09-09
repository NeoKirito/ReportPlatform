using System;
using System.IO;
using System.IO.Compression;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PEIS.Report.Contracts;
using PEIS.Report.Contracts.Logging;
using PEIS.Report.Engine;
using Xunit;

namespace PEIS.Report.Api.Tests;

public sealed class LoggingAndArchiveTests : IDisposable
{
    private readonly string _tempDir;

    public LoggingAndArchiveTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "peis_log_tests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, true);
            }
        }
        catch
        {
            // Ignore cleanup failures in test teardown
        }
    }

    [Fact]
    public async Task RollingFileLogger_writes_formatted_logs_to_daily_file()
    {
        var options = new RollingFileLoggerOptions
        {
            LogDirectory = _tempDir,
            FilePrefix = "testapi",
            RetentionDays = 30
        };

        var provider = new RollingFileLoggerProvider(options);
        var logger = provider.CreateLogger("TestCategory");

        logger.LogInformation("API operation executed: PatientId={PatientId}", 12345);
        logger.LogWarning("Sample warning message");

        // Disposing provider drains the channel and flushes to disk
        await provider.DisposeAsync();

        var todayStr = DateTime.Today.ToString("yyyy-MM-dd");
        var expectedFilePath = Path.Combine(_tempDir, $"testapi-{todayStr}.log");

        Assert.True(File.Exists(expectedFilePath), $"Expected log file {expectedFilePath} does not exist.");

        var content = await File.ReadAllTextAsync(expectedFilePath);
        Assert.Contains("[INFO ] [TestCategory] API operation executed: PatientId=12345", content);
        Assert.Contains("[WARN ] [TestCategory] Sample warning message", content);
    }

    [Fact]
    public void LogArchiveWorker_archives_files_older_than_retention_period_into_zip()
    {
        var options = new RollingFileLoggerOptions
        {
            LogDirectory = _tempDir,
            FilePrefix = "testagent",
            RetentionDays = 30
        };

        // Create an old log (e.g. 40 days ago)
        var oldDateStr = "2026-01-15";
        var oldFileName = $"testagent-{oldDateStr}.log";
        var oldFilePath = Path.Combine(_tempDir, oldFileName);
        File.WriteAllText(oldFilePath, "Log line from January 2026");

        // Create a today log (should NOT be archived)
        var todayDateStr = DateTime.Today.ToString("yyyy-MM-dd");
        var todayFileName = $"testagent-{todayDateStr}.log";
        var todayFilePath = Path.Combine(_tempDir, todayFileName);
        File.WriteAllText(todayFilePath, "Log line from today");

        var worker = new LogArchiveWorker(options, NullLogger<LogArchiveWorker>.Instance);
        worker.RunArchiveCycle();

        // Check archive
        var expectedZipPath = Path.Combine(_tempDir, "archive", "testagent-2026-01.zip");
        Assert.True(File.Exists(expectedZipPath), $"Expected zip file {expectedZipPath} does not exist.");

        // Old file must be removed
        Assert.False(File.Exists(oldFilePath), $"Old file {oldFilePath} should have been deleted after zip.");

        // Today file must still exist
        Assert.True(File.Exists(todayFilePath), $"Today file {todayFilePath} must remain uncompressed.");

        // Verify content inside the zip archive
        using var zip = ZipFile.OpenRead(expectedZipPath);
        var entry = zip.GetEntry(oldFileName);
        Assert.NotNull(entry);
        using var stream = entry.Open();
        using var reader = new StreamReader(stream);
        var unzippedContent = reader.ReadToEnd();
        Assert.Equal("Log line from January 2026", unzippedContent);
    }

    [Fact]
    public async Task WarmupService_ReloadCatalogAsync_handles_new_and_faulty_reports_gracefully()
    {
        var mockCatalog = new TestCatalogProvider(["R-NEW", "R-BAD"]);
        var mockDefProvider = new TestDefinitionProvider();

        var cache = new ReportDefinitionCache();
        var config = new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build();
        var warmup = new ReportDefinitionWarmupService(
            config,
            mockDefProvider,
            cache,
            NullLogger<ReportDefinitionWarmupService>.Instance,
            catalog: mockCatalog);

        var (total, success, failed, newlyCached) = await warmup.ReloadCatalogAsync(CancellationToken.None);

        Assert.Equal(2, total);
        Assert.Equal(1, success);
        Assert.Equal(1, failed);
        Assert.Single(newlyCached);
        Assert.Contains("R-NEW", newlyCached);

        // Second reload should find 0 new items
        var (_, _, _, newlyCached2) = await warmup.ReloadCatalogAsync(CancellationToken.None);
        Assert.Empty(newlyCached2);
    }

    private sealed class TestCatalogProvider(IReadOnlyList<string> ids) : IReportCatalogProvider
    {
        public Task<IReadOnlyList<string>> ListReportIdsAsync(CancellationToken cancellationToken)
        {
            return Task.FromResult(ids);
        }
    }

    private sealed class TestDefinitionProvider : IReportDefinitionProvider
    {
        public Task<ReportDefinition> GetRequiredAsync(ReportRenderRequest request, CancellationToken cancellationToken)
        {
            if (request.ReportId == "R-BAD")
            {
                throw new InvalidOperationException("Corrupted template");
            }
            return Task.FromResult(new ReportDefinition(
                request.ReportId,
                "1",
                "Test",
                null,
                new Dictionary<string, string>(),
                DateTimeOffset.UtcNow,
                "<Report></Report>"));
        }
    }
}
