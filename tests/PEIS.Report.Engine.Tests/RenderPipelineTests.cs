using System.Text.Json;
using PEIS.Report.Contracts;
using PEIS.Report.Engine;
using Xunit;

namespace PEIS.Report.Engine.Tests;

public sealed class RenderPipelineTests
{
    [Fact]
    public async Task Definition_cache_single_flights_concurrent_requests()
    {
        var cache = new ReportDefinitionCache();
        var calls = 0;
        var request = new ReportRenderRequest("GUIDE_A4", new Dictionary<string, JsonElement>());

        var results = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ =>
            cache.GetOrCreateAsync(request.ReportId, async cancellationToken =>
            {
                Interlocked.Increment(ref calls);
                await Task.Delay(10, cancellationToken);
                return new ReportDefinition(request.ReportId, "v1", "guide", null,
                    new Dictionary<string, string>(), DateTimeOffset.UtcNow, "test");
            }, CancellationToken.None)));

        Assert.Equal(1, calls);
        Assert.All(results, item => Assert.Equal("GUIDE_A4", item.ReportId));
        var snapshot = cache.Snapshot();
        Assert.Equal(1, snapshot.Misses);
        Assert.Equal(11, snapshot.Hits);
    }

    [Fact]
    public async Task Deterministic_renderer_emits_required_stage_timings_and_pdf()
    {
        var telemetry = new InMemoryReportRenderTelemetry();
        var renderer = new StubReportRenderer(
            new ReportDefinitionCache(),
            new DeterministicReportDefinitionProvider(),
            new DeterministicTemplateProvider(),
            new EmptyReportDataProvider(),
            new RenderConcurrencyGate(new RenderConcurrencyOptions { MaxConcurrentRenders = 1 }),
            telemetry);
        using var json = JsonDocument.Parse("{\"tjh\":\"TJ-001\"}");
        var request = new ReportRenderRequest(
            "GUIDE_A4",
            new Dictionary<string, JsonElement> { ["tjh"] = json.RootElement.GetProperty("tjh").Clone() },
            "print-a4");

        var result = await renderer.RenderPdfAsync(request, CancellationToken.None);

        Assert.StartsWith("%PDF-", System.Text.Encoding.ASCII.GetString(result.Pdf));
        Assert.Equal("GUIDE_A4.pdf", result.FileName);
        Assert.Contains(result.Timings, timing => timing.Stage == "DefinitionLoad");
        Assert.Contains(result.Timings, timing => timing.Stage == "SqlQuery");
        Assert.Contains(result.Timings, timing => timing.Stage == "Prepare");
        Assert.Contains(result.Timings, timing => timing.Stage == "PdfExport");
        Assert.Contains(result.Timings, timing => timing.Stage == "Total");
        Assert.Single(telemetry.Snapshot());
    }

    [Fact]
    public async Task Render_gate_limits_simultaneous_leases()
    {
        var gate = new RenderConcurrencyGate(new RenderConcurrencyOptions { MaxConcurrentRenders = 1 });
        using var first = await gate.EnterAsync(CancellationToken.None);
        var pending = gate.EnterAsync(CancellationToken.None);

        await Task.Delay(10);
        Assert.Equal(1, gate.Snapshot().Active);
        Assert.Equal(1, gate.Snapshot().Queued);

        first.Dispose();
        using var second = await pending;
        Assert.Equal(1, gate.Snapshot().Active);
    }
}
