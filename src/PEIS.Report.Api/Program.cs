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

var builder = WebApplication.CreateBuilder(args);
builder.Logging.AddRollingFile(options =>
{
    options.FilePrefix = "api";
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

// Auto-discover config.ini from current dir, parent dir, or app base
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

var dbConnStr = builder.Configuration.GetValue<string>("ReportDatabase:ConnectionString");
if (!string.IsNullOrWhiteSpace(dbConnStr) && dbConnStr.Contains("请填写"))
{
    dbConnStr = null;
    builder.Configuration["ReportDatabase:ConnectionString"] = string.Empty;
}

builder.Services.AddControllers();

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
builder.Services.AddSignalR(options =>
{
    options.EnableDetailedErrors = builder.Environment.IsDevelopment();
});
builder.Services.AddSingleton<AgentRegistry>();
builder.Services.AddSingleton<PrintJobStateStore>();
builder.Services.AddSingleton<ReportDeliveryStateStore>();
builder.Services.AddSingleton<ReportDeliveryArtifactTokenStore>();
builder.Services.AddSingleton<PrintRequestIdempotencyStore>();
builder.Services.AddSingleton<PrintScenarioCatalog>();
builder.Services.AddSingleton<IPdfArtifactStore, LocalPdfArtifactStore>();
builder.Services.AddSingleton(new ReportDefinitionCache(
    builder.Configuration.GetValue<string>("ReportEngine:DefinitionCacheDirectory")));

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
    builder.Services.AddSingleton<DeterministicReportDefinitionProvider>();
    builder.Services.AddSingleton<IReportDefinitionProvider>(sp => sp.GetRequiredService<DeterministicReportDefinitionProvider>());
    builder.Services.AddSingleton<IReportCatalogProvider>(sp => sp.GetRequiredService<DeterministicReportDefinitionProvider>());
    builder.Services.AddSingleton<ITemplateProvider, DeterministicTemplateProvider>();
    builder.Services.AddSingleton<IReportDataProvider, EmptyReportDataProvider>();
}
builder.Services.AddSingleton<InMemoryReportRenderTelemetry>();
builder.Services.AddSingleton<IReportRenderTelemetry>(sp => sp.GetRequiredService<InMemoryReportRenderTelemetry>());
builder.Services.AddSingleton(sp => new RenderConcurrencyGate(sp.GetRequiredService<IOptions<RenderConcurrencyOptions>>().Value));
builder.Services.AddHttpClient("report-images");
builder.Services.AddSingleton<IImageResolver>(sp => new ImageResolver(
    sp.GetRequiredService<IHttpClientFactory>().CreateClient("report-images"),
    sp.GetRequiredService<IOptions<ImageResolutionOptions>>().Value));

var renderer = builder.Configuration.GetValue<string>("ReportEngine:Renderer");
if (string.IsNullOrWhiteSpace(renderer) || string.Equals(renderer, "Stub", StringComparison.OrdinalIgnoreCase))
{
    renderer = "FastReportOpenSource";
    builder.Configuration["ReportEngine:Renderer"] = "FastReportOpenSource";
}

if (string.Equals(renderer, "FastReportOpenSource", StringComparison.OrdinalIgnoreCase))
{
    OpenSourceFastReportRuntime.WarmupCompiler();
    builder.Services.AddSingleton<IWatermarkTextProvider, SqlServerWatermarkTextProvider>();
    builder.Services.AddSingleton<IFastReportRuntime, OpenSourceFastReportRuntime>();
    builder.Services.AddSingleton<IReportRenderer, FastReportReportRenderer>();
}
else
{
    builder.Services.AddSingleton<IReportRenderer, StubReportRenderer>();
}
builder.Services.AddSingleton<LegacyReportRequestAdapter>();
builder.Services.AddSingleton<FastReportFrxDocxTemplateCompiler>();
builder.Services.AddSingleton<OpenXmlTemplateDocxRenderer>();
builder.Services.AddSingleton<IFrxDocxReportExporter, FrxDocxReportExporter>();
builder.Services.AddSingleton<PrintJobCoordinator>();

builder.Services.AddSingleton<BusinessPrintCoordinator>();
builder.Services.AddSingleton<ReportDeliveryCoordinator>();
builder.Services.AddSingleton<ReportDefinitionWarmupService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<ReportDefinitionWarmupService>());

var app = builder.Build();
app.UseMiddleware<ReportOperationLoggingMiddleware>();
app.UseDefaultFiles();
app.UseStaticFiles();
app.MapControllers();

app.MapGet("/health", () => Results.Ok(new { status = "ok", service = "PEIS.Report.Api" }));
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
app.MapPost("/internal/cache/reports/{reportId}/invalidate", (string reportId, ReportDefinitionCache definitions) =>
{
    var removed = definitions.InvalidateReport(reportId);
    return Results.Ok(new { reportId, removed });
});

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

// New typed endpoint retained for diagnostics/new integrations only. Existing PEIS callers
// should continue to use POST /api/Reports/GetReportByJson with their original JSON body.
app.MapPost("/internal/reports/pdf", async (ReportRenderRequest request, IReportRenderer renderer, CancellationToken ct) =>
{
    var result = await renderer.RenderPdfAsync(request, ct);
    return Results.File(result.Pdf, "application/pdf", result.FileName, enableRangeProcessing: false);
});

// Installation/admin visibility. Normal B/S pages do not need to enumerate printers.
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

app.MapGet("/api/print/actions", (PrintScenarioCatalog catalog) => Results.Ok(catalog.Snapshot().Select(x => new
{
    actionCode = x.Key,
    x.Value.JobName,
    documents = x.Value.Documents.Select(d => new { d.Key, d.ReportId, d.PrinterRole, d.Profile, d.Copies, d.Duplex })
})));

// PRIMARY B/S API: one click, one action code, zero physical printer selections.
app.MapPost("/api/print/actions", async (BusinessPrintRequest request, BusinessPrintCoordinator coordinator, CancellationToken ct) =>
{
    var result = await coordinator.CreateAsync(request, ct);
    return Results.Accepted($"/api/print/jobs/{result.JobId}", result);
});

// Diagnostic/manual API retained for installation and troubleshooting only.
app.MapPost("/api/print/jobs", async (CreatePrintJobRequest request, PrintJobCoordinator coordinator, CancellationToken ct) =>
{
    var result = await coordinator.CreateAsync(request, ct);
    return Results.Accepted($"/api/print/jobs/{result.JobId}", result);
});

app.MapGet("/api/print/jobs/{jobId:guid}", (Guid jobId, PrintJobStateStore states) =>
{
    var state = states.Get(jobId);
    return state is null ? Results.NotFound() : Results.Ok(state);
});

app.MapGet("/api/print/artifacts/{artifactId:guid}", async (Guid artifactId, IPdfArtifactStore artifacts, CancellationToken ct) =>
{
    var artifact = await artifacts.OpenAsync(artifactId, ct);
    if (artifact is null) return Results.NotFound();
    return Results.File(artifact.Stream, "application/pdf", artifact.FileName, enableRangeProcessing: true);
});

app.MapHub<PrintAgentHub>("/hubs/print-agent");

app.Run();
