using Microsoft.Extensions.Options;
using PEIS.Report.Api.Compatibility;
using PEIS.Report.Api.Hubs;
using PEIS.Report.Api.Printing;
using PEIS.Report.Api.Storage;
using PEIS.Report.Contracts;
using PEIS.Report.Engine;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();

builder.Services.Configure<PrintRoutingOptions>(builder.Configuration.GetSection("PrintRouting"));
builder.Services.Configure<RenderConcurrencyOptions>(builder.Configuration.GetSection("Rendering"));
builder.Services.Configure<ImageResolutionOptions>(builder.Configuration.GetSection("ImageResolution"));
builder.Services.AddSignalR(options =>
{
    options.EnableDetailedErrors = builder.Environment.IsDevelopment();
});
builder.Services.AddSingleton<AgentRegistry>();
builder.Services.AddSingleton<PrintJobStateStore>();
builder.Services.AddSingleton<PrintRequestIdempotencyStore>();
builder.Services.AddSingleton<PrintScenarioCatalog>();
builder.Services.AddSingleton<IPdfArtifactStore, LocalPdfArtifactStore>();
builder.Services.AddSingleton<ReportDefinitionCache>();
builder.Services.AddSingleton<IReportDefinitionProvider, DeterministicReportDefinitionProvider>();
builder.Services.AddSingleton<ITemplateProvider, DeterministicTemplateProvider>();
builder.Services.AddSingleton<IReportDataProvider, EmptyReportDataProvider>();
builder.Services.AddSingleton<InMemoryReportRenderTelemetry>();
builder.Services.AddSingleton<IReportRenderTelemetry>(sp => sp.GetRequiredService<InMemoryReportRenderTelemetry>());
builder.Services.AddSingleton(sp => new RenderConcurrencyGate(sp.GetRequiredService<IOptions<RenderConcurrencyOptions>>().Value));
builder.Services.AddHttpClient("report-images");
builder.Services.AddSingleton<IImageResolver>(sp => new ImageResolver(
    sp.GetRequiredService<IHttpClientFactory>().CreateClient("report-images"),
    sp.GetRequiredService<IOptions<ImageResolutionOptions>>().Value));
builder.Services.AddSingleton<IReportRenderer, StubReportRenderer>();
builder.Services.AddSingleton<LegacyReportRequestAdapter>();
builder.Services.AddSingleton<PrintJobCoordinator>();
builder.Services.AddSingleton<BusinessPrintCoordinator>();

var app = builder.Build();
app.UseDefaultFiles();
app.UseStaticFiles();
app.MapControllers();

app.MapGet("/health", () => Results.Ok(new { status = "ok", service = "PEIS.Report.Api" }));
app.MapGet("/internal/diagnostics/rendering", (
    ReportDefinitionCache definitions,
    RenderConcurrencyGate gate,
    InMemoryReportRenderTelemetry telemetry) => Results.Ok(new
{
    definitionCache = definitions.Snapshot(),
    renderConcurrency = gate.Snapshot(),
    recentRenders = telemetry.Snapshot()
}));

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
