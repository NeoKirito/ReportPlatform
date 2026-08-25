using Microsoft.Extensions.Options;
using PEIS.Report.Api.Printing;

namespace PEIS.Report.Api.Storage;

/// <summary>
/// Best-effort artifact lifecycle management. Cleanup failures are logged and isolated so a filesystem problem
/// never terminates the report API. Artifacts referenced by unfinished print targets are retained.
/// </summary>
public sealed class PdfArtifactCleanupService(
    IPdfArtifactStore artifacts,
    PrintJobStateStore jobs,
    IOptions<PdfArtifactStoreOptions> options,
    ILogger<PdfArtifactCleanupService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await CleanupSafelyAsync(stoppingToken);
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(Math.Max(1, options.Value.CleanupIntervalMinutes)));
        while (await timer.WaitForNextTickAsync(stoppingToken))
            await CleanupSafelyAsync(stoppingToken);
    }

    public async Task RunOnceAsync(CancellationToken cancellationToken = default)
        => await CleanupSafelyAsync(cancellationToken);

    private async Task CleanupSafelyAsync(CancellationToken cancellationToken)
    {
        try
        {
            var all = (await artifacts.ListAsync(cancellationToken)).OrderBy(x => x.CreatedAt).ToList();
            var policy = options.Value;
            var retentionCutoff = DateTimeOffset.UtcNow.AddHours(-Math.Max(1, policy.RetentionHours));
            var totalBytes = all.Sum(x => x.Length);
            var totalCount = all.Count;

            foreach (var artifact in all)
            {
                var expired = artifact.CreatedAt < retentionCutoff;
                var overCapacity = totalBytes > Math.Max(1, policy.MaxArtifactBytes) || totalCount > Math.Max(1, policy.MaxArtifactCount);
                if (!expired && !overCapacity) continue;
                if (await jobs.IsArtifactProtectedByActiveJobAsync(artifact.ArtifactId, cancellationToken)) continue;
                if (await artifacts.DeleteAsync(artifact.ArtifactId, cancellationToken))
                {
                    totalBytes -= artifact.Length;
                    totalCount--;
                    logger.LogInformation("Deleted expired or capacity-evicted PDF artifact {ArtifactId}.", artifact.ArtifactId);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "PDF artifact cleanup failed; the API will continue running and retry later.");
        }
    }
}
