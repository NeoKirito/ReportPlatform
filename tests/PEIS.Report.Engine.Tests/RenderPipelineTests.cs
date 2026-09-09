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

    [Fact]
    public async Task FastReport_renderer_throws_ReportNotFound_when_page_count_is_zero()
    {
        var runtimeMock = new ZeroPageFastReportRuntime();
        var renderer = new FastReportReportRenderer(
            new ReportDefinitionCache(),
            new DeterministicReportDefinitionProvider(),
            new DeterministicTemplateProvider(),
            new EmptyReportDataProvider(),
            new RenderConcurrencyGate(new RenderConcurrencyOptions { MaxConcurrentRenders = 1 }),
            runtimeMock,
            new DisabledWatermarkTextProvider(),
            new InMemoryReportRenderTelemetry());

        var request = new ReportRenderRequest("GUIDE_A4", new Dictionary<string, JsonElement>());

        var ex = await Assert.ThrowsAsync<LegacyReportDatabaseException>(() => renderer.RenderPdfAsync(request, CancellationToken.None));
        Assert.Equal(LegacyReportDatabaseErrorCode.ReportNotFound, ex.Code);
        Assert.Contains("未查询到有效体检数据（生成页数为0）", ex.Message);
    }

    private sealed class ZeroPageFastReportRuntime : IFastReportRuntime
    {
        public Task<FastReportRuntimePreparation> PrepareAsync(FastReportRenderContext context, CancellationToken cancellationToken)
            => Task.FromResult(new FastReportRuntimePreparation(new DummyPreparedDocument(), Array.Empty<ReportStageTiming>()));

        public Task ApplyWatermarkAsync(IFastReportPreparedDocument prepared, WatermarkOptions watermark, CancellationToken cancellationToken)
            => Task.CompletedTask;

        public Task<FastReportPdfOutput> ExportPdfAsync(IFastReportPreparedDocument prepared, PdfExportProfile profile, CancellationToken cancellationToken)
            => Task.FromResult(new FastReportPdfOutput(new byte[] { 1, 2, 3 }, 0));

        private sealed class DummyPreparedDocument : IFastReportPreparedDocument
        {
            public int ExportedPageCount => 0;
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
