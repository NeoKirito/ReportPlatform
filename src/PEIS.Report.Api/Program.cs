using Microsoft.Extensions.Options;
using PEIS.Report.Api;
using PEIS.Report.Api.Compatibility;
using PEIS.Report.Infrastructure.SqlServer;
using PEIS.Report.Api.Hubs;
using PEIS.Report.Api.Printing;
using PEIS.Report.Api.Storage;
using PEIS.Report.Contracts;
using PEIS.Report.Docx.OpenXml;
using PEIS.Report.Engine;

using PEIS.Report.FastReport.OpenSource;
using PEIS.Report.Contracts.Logging;
using PEIS.Report.Api.Logging;

// ============================================================
// PEIS.Report.Api 启动入口
// 体检信息系统报表服务 - 替代旧版IIS FastReport服务
// ============================================================

var builder = WebApplication.CreateBuilder(args);

// ------------------------------------------------------------
// 1. 日志配置：滚动文件日志，30天自动归档
// ------------------------------------------------------------
builder.Logging.AddRollingFile(options =>
{
    options.FilePrefix = "api";
    // 检测是否为外层部署目录（有config.ini在上级目录）
    var parentMarker = Path.Combine(AppContext.BaseDirectory, "..", "config.ini");
    if (File.Exists(parentMarker))
    {
        options.LogDirectory = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "logs"));
    }
    else
    {
        options.LogDirectory = "logs";
    }
    options.RetentionDays = 30;
});

// ------------------------------------------------------------
// 2. 自动发现config.ini配置文件
// 搜索顺序：当前目录 → 上级目录 → AppBase目录 → AppBase上级目录
// 支持Port和ConnectionString两个配置项
// ------------------------------------------------------------
var candidateIniPaths = new[]
{
    Path.Combine(builder.Environment.ContentRootPath, "config.ini"),
    Path.Combine(builder.Environment.ContentRootPath, "..", "config.ini"),
    Path.Combine(AppContext.BaseDirectory, "config.ini"),
    Path.Combine(AppContext.BaseDirectory, "..", "config.ini")
};

foreach (var iniPath in candidateIniPaths)
{
    if (File.Exists(iniPath))
    {
        try
        {
            foreach (var line in File.ReadAllLines(iniPath))
            {
                var trimmed = line.Trim();
                if (string.IsNullOrWhiteSpace(trimmed) || trimmed.StartsWith('#') || trimmed.StartsWith(';'))
                    continue;
                var sep = trimmed.IndexOf('=');
                if (sep < 0) continue;
                var key = trimmed.Substring(0, sep).Trim();
                var val = trimmed.Substring(sep + 1).Trim();
                if (string.Equals(key, "ConnectionString", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(val))
                {
                    // 只在未配置或为占位符时覆盖
                    var existing = builder.Configuration["ReportDatabase:ConnectionString"];
                    if (string.IsNullOrWhiteSpace(existing) || existing.Contains("请填写"))
                    {
                        builder.Configuration["ReportDatabase:ConnectionString"] = val;
                        builder.Configuration["WatermarkDatabase:ConnectionString"] = val;
                    }
                }
                else if (string.Equals(key, "Port", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(val))
                {
                    if (string.IsNullOrWhiteSpace(builder.Configuration["Urls"]))
                    {
                        builder.Configuration["Urls"] = $"http://0.0.0.0:{val}";
                    }
                }
            }
        }
        catch { }
        break;
    }
}

// 清理占位符连接字符串
var dbConnStr = builder.Configuration.GetValue<string>("ReportDatabase:ConnectionString");
if (!string.IsNullOrWhiteSpace(dbConnStr) && dbConnStr.Contains("请填写"))
{
    dbConnStr = null;
    builder.Configuration["ReportDatabase:ConnectionString"] = string.Empty;
}

// ------------------------------------------------------------
// 3. 注册MVC控制器
// ------------------------------------------------------------
builder.Services.AddControllers();

// ------------------------------------------------------------
// 4. 配置选项绑定：将appsettings.json中的配置节绑定到强类型类
// ------------------------------------------------------------
builder.Services.Configure<PrintRoutingOptions>(builder.Configuration.GetSection("PrintRouting"));
builder.Services.Configure<PrintAgentSecurityOptions>(builder.Configuration.GetSection("PrintAgentSecurity"));
builder.Services.Configure<ReportDeliverySecurityOptions>(builder.Configuration.GetSection("ReportDeliverySecurity"));
builder.Services.Configure<AgentRegistryOptions>(builder.Configuration.GetSection("PrintAgentRegistry"));
builder.Services.Configure<RenderConcurrencyOptions>(builder.Configuration.GetSection("Rendering"));
builder.Services.Configure<ImageResolutionOptions>(builder.Configuration.GetSection("ImageResolution"));
builder.Services.Configure<ReportEngineOptions>(builder.Configuration.GetSection("ReportEngine"));
builder.Services.Configure<ReportDatabaseOptions>(builder.Configuration.GetSection("ReportDatabase"));
builder.Services.Configure<WatermarkDatabaseOptions>(builder.Configuration.GetSection("WatermarkDatabase"));
builder.Services.Configure<LegacyReportSchemaMapping>(builder.Configuration.GetSection("LegacyReportSchema"));

// ------------------------------------------------------------
// 5. SignalR配置（用于与PrintAgent实时通信）
// ------------------------------------------------------------
builder.Services.AddSignalR(options =>
{
    options.EnableDetailedErrors = builder.Environment.IsDevelopment();
});

// ------------------------------------------------------------
// 6. 单例服务注册
// ------------------------------------------------------------
builder.Services.AddSingleton<AgentRegistry>();          // 打印Agent注册表
builder.Services.AddSingleton<PrintJobStateStore>();     // 打印任务状态存储
builder.Services.AddSingleton<ReportDeliveryStateStore>(); // 报表交付状态存储
builder.Services.AddSingleton<ReportDeliveryArtifactTokenStore>(); // 交付产物令牌存储
builder.Services.AddSingleton<PrintRequestIdempotencyStore>(); // 打印请求幂等性存储
builder.Services.AddSingleton<PrintScenarioCatalog>();   // 打印场景目录
builder.Services.AddSingleton<IPdfArtifactStore, LocalPdfArtifactStore>(); // PDF产物存储
builder.Services.AddSingleton(new ReportDefinitionCache(
    builder.Configuration.GetValue<string>("ReportEngine:DefinitionCacheDirectory"))); // 报表定义缓存

// ------------------------------------------------------------
// 7. 报表定义源选择：LegacySqlServer 或 Deterministic
// 
// LegacySqlServer：从SQL Server数据库加载报表定义（生产环境）
// Deterministic：使用确定性定义（开发/测试环境）
// 
// 如果配置了数据库连接字符串，默认使用LegacySqlServer
// ------------------------------------------------------------
var definitionSource = builder.Configuration.GetValue<string>("ReportEngine:DefinitionSource");
if (string.IsNullOrWhiteSpace(definitionSource) || string.Equals(definitionSource, "Deterministic", StringComparison.OrdinalIgnoreCase))
{
    if (!string.IsNullOrWhiteSpace(dbConnStr))
    {
        definitionSource = "LegacySqlServer";
        builder.Configuration["ReportEngine:DefinitionSource"] = "LegacySqlServer";
    }
    else
    {
        definitionSource = "Deterministic";
    }
}

if (string.Equals(definitionSource, "LegacySqlServer", StringComparison.OrdinalIgnoreCase))
{
    // 生产模式：从数据库加载报表定义
    builder.Services.AddSingleton<LegacyDatabaseReportDefinitionProvider>();
    builder.Services.AddSingleton<IReportDefinitionProvider>(sp => sp.GetRequiredService<LegacyDatabaseReportDefinitionProvider>());
    builder.Services.AddSingleton<IReportDefinitionVersionProvider>(sp => sp.GetRequiredService<LegacyDatabaseReportDefinitionProvider>());
    builder.Services.AddSingleton<IReportCatalogProvider>(sp => sp.GetRequiredService<LegacyDatabaseReportDefinitionProvider>());
    builder.Services.AddSingleton<ITemplateProvider, LegacyDatabaseTemplateProvider>();
    builder.Services.AddSingleton<ILegacyQueryParameterBinder, AdoNetLegacyQueryParameterBinder>();
    builder.Services.AddSingleton<IReportDataProvider, SqlServerReportDataProvider>();
}
else
{
    // 开发模式：使用确定性定义
    builder.Services.AddSingleton<DeterministicReportDefinitionProvider>();
    builder.Services.AddSingleton<IReportDefinitionProvider>(sp => sp.GetRequiredService<DeterministicReportDefinitionProvider>());
    builder.Services.AddSingleton<IReportCatalogProvider>(sp => sp.GetRequiredService<DeterministicReportDefinitionProvider>());
    builder.Services.AddSingleton<ITemplateProvider, DeterministicTemplateProvider>();
    builder.Services.AddSingleton<IReportDataProvider, EmptyReportDataProvider>();
}

// 性能指标收集
builder.Services.AddSingleton<InMemoryReportRenderTelemetry>();
builder.Services.AddSingleton<IReportRenderTelemetry>(sp => sp.GetRequiredService<InMemoryReportRenderTelemetry>());

// 并发控制门（限制同时渲染的报表数量）
builder.Services.AddSingleton(sp => new RenderConcurrencyGate(sp.GetRequiredService<IOptions<RenderConcurrencyOptions>>().Value));

// 图片解析器（HTTP下载图片并缓存）
builder.Services.AddHttpClient("report-images");
builder.Services.AddSingleton<IImageResolver>(sp => new ImageResolver(
    sp.GetRequiredService<IHttpClientFactory>().CreateClient("report-images"),
    sp.GetRequiredService<IOptions<ImageResolutionOptions>>().Value));

// ------------------------------------------------------------
// 8. 渲染器选择：FastReportOpenSource 或 Stub
// 
// FastReportOpenSource：生产环境，使用MIT版FastReport
// Stub：未配置时的占位符，所有操作都会抛出异常
// 
// 必须使用"FastReportOpenSource"，不要用"FastReport"（会导致Stub错误）
// ------------------------------------------------------------
var renderer = builder.Configuration.GetValue<string>("ReportEngine:Renderer");
if (string.IsNullOrWhiteSpace(renderer) || string.Equals(renderer, "Stub", StringComparison.OrdinalIgnoreCase))
{
    renderer = "FastReportOpenSource";
    builder.Configuration["ReportEngine:Renderer"] = "FastReportOpenSource";
}

if (string.Equals(renderer, "FastReportOpenSource", StringComparison.OrdinalIgnoreCase))
{
    // 预热FastReport编译器（减少首次渲染延迟）
    OpenSourceFastReportRuntime.WarmupCompiler();
    builder.Services.AddSingleton<IWatermarkTextProvider, SqlServerWatermarkTextProvider>();
    builder.Services.AddSingleton<IFastReportRuntime, OpenSourceFastReportRuntime>();
    builder.Services.AddSingleton<IReportRenderer, FastReportReportRenderer>();
}
else
{
    builder.Services.AddSingleton<IReportRenderer, StubReportRenderer>();
}

// ------------------------------------------------------------
// 9. 其他服务注册
// ------------------------------------------------------------
builder.Services.AddSingleton<LegacyReportRequestAdapter>();     // 旧版请求适配器
builder.Services.AddSingleton<FastReportFrxDocxTemplateCompiler>(); // FRX到DOCX模板编译器
builder.Services.AddSingleton<OpenXmlTemplateDocxRenderer>();    // OpenXML文档渲染器
builder.Services.AddSingleton<IFrxDocxReportExporter, FrxDocxReportExporter>(); // FRX导出器
builder.Services.AddSingleton<PrintJobCoordinator>();            // 打印任务协调器
builder.Services.AddSingleton<BusinessPrintCoordinator>();       // 业务打印协调器
builder.Services.AddSingleton<ReportDeliveryCoordinator>();      // 报表交付协调器

// 报表定义预热服务（启动时加载所有报表定义到缓存）
builder.Services.AddSingleton<ReportDefinitionWarmupService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<ReportDefinitionWarmupService>());

// ============================================================
// 构建应用程序
// ============================================================
var app = builder.Build();

// ------------------------------------------------------------
// 10. 中间件配置
// ------------------------------------------------------------
app.UseMiddleware<ReportOperationLoggingMiddleware>(); // 操作日志中间件
app.UseDefaultFiles();   // 默认文件（index.html）
app.UseStaticFiles();    // 静态文件
app.MapControllers();    // MVC路由

// ------------------------------------------------------------
// 11. 端点映射
// ------------------------------------------------------------

// 健康检查端点
app.MapGet("/health", () => Results.Ok(new { status = "ok", service = "PEIS.Report.Api" }));

// 诊断端点：查看定义缓存、渲染并发、最近渲染指标
app.MapGet("/internal/diagnostics/rendering", (
    ReportDefinitionCache definitions,
    RenderConcurrencyGate gate,
    InMemoryReportRenderTelemetry telemetry,
    IOptions<ReportEngineOptions> engineOptions) => Results.Ok(new
{
    definitionSource = engineOptions.Value.DefinitionSource,
    definitionCache = definitions.Snapshot(),
    renderConcurrency = gate.Snapshot(),
    recentRenders = telemetry.Snapshot()
}));

// 缓存失效端点：手动清除指定报表的定义缓存
app.MapPost("/internal/cache/reports/{reportId}/invalidate", (string reportId, ReportDefinitionCache definitions) =>
{
    var removed = definitions.InvalidateReport(reportId);
    return Results.Ok(new { reportId, removed });
});

// 缓存重新加载端点：重新扫描数据库并预热所有报表定义
app.MapPost("/internal/cache/reload", async (ReportDefinitionWarmupService warmup, CancellationToken ct) =>
{
    var (total, success, failed, newReports) = await warmup.ReloadCatalogAsync(ct);
    return Results.Ok(new
    {
        status = "ok",
        scanned = total,
        success,
        failed,
        newlyCached = newReports
    });
});

// 新版报表端点（仅用于诊断/新集成，现有PEIS调用方应继续使用兼容端点）
app.MapPost("/internal/reports/pdf", async (ReportRenderRequest request, IReportRenderer renderer, CancellationToken ct) =>
{
    var result = await renderer.RenderPdfAsync(request, ct);
    return Results.File(result.Pdf, "application/pdf", result.FileName, enableRangeProcessing: false);
});

// ------------------------------------------------------------
// 打印相关端点
// ------------------------------------------------------------

// 查看已注册的打印Agent列表
app.MapGet("/api/print/agents", (AgentRegistry registry) => Results.Ok(registry.Snapshot().Select(a => new
{
    a.AgentId,
    a.StationId,
    a.MachineName,
    a.Version,
    a.LastSeenAt,
    online = true,
    a.Printers,
    a.PrinterBindings
})));

// 查看支持的打印场景（如REGISTRATION_PRINT）
app.MapGet("/api/print/actions", (PrintScenarioCatalog catalog) => Results.Ok(catalog.Snapshot().Select(x => new
{
    actionCode = x.Key,
    x.Value.JobName,
    documents = x.Value.Documents.Select(d => new { d.Key, d.ReportId, d.PrinterRole, d.Profile, d.Copies, d.Duplex })
})));

// 主要B/S打印API：一键点击，一个动作代码，零物理打印机选择
app.MapPost("/api/print/actions", async (BusinessPrintRequest request, BusinessPrintCoordinator coordinator, CancellationToken ct) =>
{
    var result = await coordinator.CreateAsync(request, ct);
    return Results.Accepted($"/api/print/jobs/{result.JobId}", result);
});

// 诊断/手动打印API（仅用于安装和故障排除）
app.MapPost("/api/print/jobs", async (CreatePrintJobRequest request, PrintJobCoordinator coordinator, CancellationToken ct) =>
{
    var result = await coordinator.CreateAsync(request, ct);
    return Results.Accepted($"/api/print/jobs/{result.JobId}", result);
});

// 查询打印任务状态
app.MapGet("/api/print/jobs/{jobId:guid}", (Guid jobId, PrintJobStateStore states) =>
{
    var state = states.Get(jobId);
    return state is null ? Results.NotFound() : Results.Ok(state);
});

// 下载打印产物（PDF文件）
app.MapGet("/api/print/artifacts/{artifactId:guid}", async (Guid artifactId, IPdfArtifactStore artifacts, CancellationToken ct) =>
{
    var artifact = await artifacts.OpenAsync(artifactId, ct);
    if (artifact is null) return Results.NotFound();
    return Results.File(artifact.Stream, "application/pdf", artifact.FileName, enableRangeProcessing: true);
});

// SignalR Hub（PrintAgent实时通信）
app.MapHub<PrintAgentHub>("/hubs/print-agent");

app.Run();
