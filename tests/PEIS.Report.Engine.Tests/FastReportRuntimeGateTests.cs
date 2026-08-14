using PEIS.Report.Engine;
using Xunit;

namespace PEIS.Report.Engine.Tests;

public sealed class FastReportRuntimeGateTests
{
    [Fact]
    public async Task Missing_runtime_reports_the_legal_dependency_gate_without_fallback_renderer()
    {
        var runtime = new MissingFastReportRuntime();

        var error = await Assert.ThrowsAsync<FastReportIntegrationUnavailableException>(() =>
            runtime.PrepareAsync(null!, CancellationToken.None));

        Assert.Contains("hospital-approved", error.Message, StringComparison.Ordinal);
        Assert.Contains("license entitlement", error.Message, StringComparison.Ordinal);
    }
}
