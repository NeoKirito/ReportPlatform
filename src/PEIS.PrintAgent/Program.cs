using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PEIS.PrintAgent;
using PEIS.PrintAgent.Printing;
using PEIS.PrintAgent.Services;
using PEIS.PrintAgent.Previewing;
using PEIS.Report.Contracts.Logging;

var builder = Host.CreateApplicationBuilder(args);
builder.Logging.AddRollingFile(options =>
{
    options.FilePrefix = "agent";
    options.LogDirectory = "logs";
    options.RetentionDays = 30;
});
var simpleConfigPath = Path.Combine(AppContext.BaseDirectory, "agent.ini");
if (File.Exists(simpleConfigPath))
{
    foreach (var rawLine in File.ReadLines(simpleConfigPath))
    {
        var line = rawLine.Trim();
        if (line.Length == 0 || line.StartsWith('#') || line.StartsWith(';')) continue;
        var separator = line.IndexOf('=');
        if (separator <= 0) continue;
        var key = line[..separator].Trim();
        var value = line[(separator + 1)..].Trim();
        if (string.Equals(key, "ServerUrl", StringComparison.OrdinalIgnoreCase))
            builder.Configuration["Agent:ServerUrl"] = value;
        else if (string.Equals(key, "StationId", StringComparison.OrdinalIgnoreCase))
            builder.Configuration["Agent:StationId"] = value;
        else if (string.Equals(key, "SilentPrint", StringComparison.OrdinalIgnoreCase))
            builder.Configuration["Agent:Printing:Silent"] = value;
        else if (string.Equals(key, "DefaultPrinter", StringComparison.OrdinalIgnoreCase))
            builder.Configuration["Agent:Printing:DefaultPrinter"] = value;
        else if (string.Equals(key, "PrintBackend", StringComparison.OrdinalIgnoreCase))
            builder.Configuration["Agent:PrintBackend:Mode"] = value;
        else if (string.Equals(key, "PrintExecutable", StringComparison.OrdinalIgnoreCase))
            builder.Configuration["Agent:PrintBackend:Executable"] = value;
        else if (string.Equals(key, "PrintArgumentsTemplate", StringComparison.OrdinalIgnoreCase))
            builder.Configuration["Agent:PrintBackend:ArgumentsTemplate"] = value;
    }
}
builder.Services.Configure<AgentOptions>(builder.Configuration.GetSection("Agent"));
builder.Services.AddHttpClient("report-api");
builder.Services.AddSingleton<AgentIdentityStore>();
builder.Services.AddSingleton<PrinterCatalog>();
builder.Services.AddSingleton<PrinterSelectionStore>();
builder.Services.AddSingleton<DeliveryPrinterResolver>();
builder.Services.AddSingleton<PrintArtifactDownloader>();
builder.Services.AddSingleton<PrinterQueueManager>();
builder.Services.AddSingleton<IPdfPreviewer, WebView2PdfPreviewer>();

var mode = builder.Configuration["Agent:PrintBackend:Mode"] ?? "DryRun";
if (string.Equals(mode, "Command", StringComparison.OrdinalIgnoreCase))
    builder.Services.AddSingleton<IPrintBackend, CommandPrintBackend>();
else
    builder.Services.AddSingleton<IPrintBackend, DryRunPrintBackend>();

builder.Services.AddHostedService<AgentWorker>();
await builder.Build().RunAsync();
