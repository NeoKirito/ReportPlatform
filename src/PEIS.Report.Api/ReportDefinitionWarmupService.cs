using PEIS.Report.Contracts;
using PEIS.Report.Engine;

namespace PEIS.Report.Api;

/// <summary>
/// Background warmup service. On startup, it dynamically discovers all available report definitions from the database
/// (or uses the configured warmup list) and pre-loads them into the memory cache.
/// Individual faulty or deprecated templates are safely isolated and skipped without blocking service readiness.
/// </summary>
public sealed class ReportDefinitionWarmupService(
    IConfiguration configuration,
    IReportDefinitionProvider definitions,
    ReportDefinitionCache cache,
    ILogger<ReportDefinitionWarmupService> logger,
    ITemplateProvider? templates = null,
    IReportCatalogProvider? catalog = null) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Yield execution to allow the Web server (Kestrel) to finish binding and start accepting requests immediately.
        await Task.Yield();

        try
        {
            var configuredIds = configuration.GetSection("ReportEngine:WarmupReportIds").Get<string[]>() ?? [];
            var isDynamicAll = configuredIds.Length == 0 || configuredIds.Any(x => string.Equals(x.Trim(), "*", StringComparison.OrdinalIgnoreCase));

            IReadOnlyList<string> candidateReportIds;
            if (!isDynamicAll)
            {
                candidateReportIds = configuredIds
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .Select(x => x.Trim())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                logger.LogInformation("启动报表预热: 采用配置的固定列表 ({Count} 个报表)", candidateReportIds.Count);
            }
            else
            {
                logger.LogInformation("启动报表预热: 采用动态全量模式，正在扫描数据库中的全部报表模板...");
                var catalogProvider = catalog ?? (definitions as IReportCatalogProvider);
                if (catalogProvider != null)
                {
                    try
                    {
                        candidateReportIds = await catalogProvider.ListReportIdsAsync(stoppingToken).ConfigureAwait(false);
                        logger.LogInformation("启动报表预热: 从数据库成功扫描到 {Count} 个可用报表模板", candidateReportIds.Count);
                    }
                    catch (Exception ex)
                    {
                        candidateReportIds = ["jktjbbd", "zytjbbd", "dwtjbgd", "dwzytjbgd", "tjdjd", "tjzydjd", "tjdj", "xmtm"];
                        logger.LogWarning(ex, "扫描报表目录时发生异常，已回退至核心常用报表集合 ({Count} 个)", candidateReportIds.Count);
                    }
                }
                else
                {
                    candidateReportIds = ["jktjbbd", "zytjbbd", "dwtjbgd", "dwzytjbgd", "tjdjd", "tjzydjd", "tjdj", "xmtm"];
                    logger.LogInformation("启动报表预热: 未找到目录扫描器，回退至核心常用报表集合 ({Count} 个)", candidateReportIds.Count);
                }
            }

            int successCount = 0;
            int failedCount = 0;

            foreach (var reportId in candidateReportIds)
            {
                if (stoppingToken.IsCancellationRequested)
                    break;

                try
                {
                    var request = new ReportRenderRequest(reportId.Trim(), new Dictionary<string, System.Text.Json.JsonElement>());
                    var cacheKey = request.ReportId;
                    if (definitions is IReportDefinitionVersionProvider versions)
                    {
                        var version = await versions.GetVersionAsync(request, stoppingToken).ConfigureAwait(false);
                        cacheKey = ReportDefinitionCache.BuildCacheKey(request.ReportId, version);
                    }

                    // 1. 预加载报表定义进内存缓存
                    var definition = await cache.GetOrCreateAsync(cacheKey, token => definitions.GetRequiredAsync(request, token), stoppingToken)
                        .ConfigureAwait(false);

                    // 2. 预解析模板 XML 结构与校验 SHA-256
                    if (templates != null)
                    {
                        await templates.GetRequiredAsync(definition, stoppingToken).ConfigureAwait(false);
                    }

                    successCount++;
                    logger.LogInformation("  [OK] 报表预热成功: {ReportId}", reportId);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    failedCount++;
                    // 单个报表异常（如老旧模板语法错误、缺少字段等）被严格隔离，记录警告并跳过，绝不阻断后续其他报表
                    logger.LogWarning("  [SKIP] 报表 {ReportId} 预热失败(已跳过，不影响服务启动及其他报表): {Message}", reportId, ex.Message);
                }
            }

            logger.LogInformation("报表预热完成: 总计检测 {Total} 个报表，成功缓存 {Success} 个，跳过异常报表 {Failed} 个。服务已就绪。",
                candidateReportIds.Count, successCount, failedCount);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // 服务正常关闭
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "后台全量报表预热任务执行异常，常规请求仍可正常按需加载。");
        }
    }
}
