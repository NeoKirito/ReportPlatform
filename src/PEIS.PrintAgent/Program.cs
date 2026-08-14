using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using PEIS.PrintAgent;
using PEIS.PrintAgent.Printing;
using PEIS.PrintAgent.Services;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.Configure<AgentOptions>(builder.Configuration.GetSection("Agent"));
builder.Services.AddHttpClient("report-api");
builder.Services.AddSingleton<PrinterCatalog>();
builder.Services.AddSingleton<PrinterQueueManager>();

var mode = builder.Configuration["Agent:PrintBackend:Mode"] ?? "DryRun";
if (string.Equals(mode, "Command", StringComparison.OrdinalIgnoreCase))
    builder.Services.AddSingleton<IPrintBackend, CommandPrintBackend>();
else
    builder.Services.AddSingleton<IPrintBackend, DryRunPrintBackend>();

builder.Services.AddHostedService<AgentWorker>();
await builder.Build().RunAsync();
