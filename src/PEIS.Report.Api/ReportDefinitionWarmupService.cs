using PEIS.Report.Contracts;
using PEIS.Report.Engine;

namespace PEIS.Report.Api;

/// <summary>
/// Loads only immutable report definitions after startup. It does not query patient data or render a report.
/// A real request for the same report shares the same cache task, so warmup cannot duplicate the large template load.
/// </summary>
public sealed class ReportDefinitionWarmupService(
    IConfiguration configuration,
    IReportDefinitionProvider definitions,
    ReportDefinitionCache cache,
    ILogger<ReportDefinitionWarmupService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var reportIds = configuration.GetSection("ReportEngine:WarmupReportIds").Get<string[]>() ?? [];
        foreach (var reportId in reportIds.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var request = new ReportRenderRequest(reportId.Trim(), new Dictionary<string, System.Text.Json.JsonElement>());
                var cacheKey = request.ReportId;
                if (definitions is IReportDefinitionVersionProvider versions)
                {
                    var version = await versions.GetVersionAsync(request, stoppingToken).ConfigureAwait(false);
                    cacheKey = ReportDefinitionCache.BuildCacheKey(request.ReportId, version);
                }
                await cache.GetOrCreateAsync(cacheKey, token => definitions.GetRequiredAsync(request, token), stoppingToken)
                    .ConfigureAwait(false);
                logger.LogInformation("Report definition {ReportId} warmed into cache.", request.ReportId);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Report definition warmup failed for {ReportId}; normal requests remain available.", reportId);
            }
        }
    }
}
