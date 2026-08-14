using System.Reflection;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using PEIS.Report.Api.Compatibility;
using PEIS.Report.Contracts;
using PEIS.Report.Engine;
using Xunit;

namespace PEIS.Report.Api.Tests;

public sealed class LegacyCompatibilityTests
{
    [Fact]
    public void Adapter_preserves_arbitrary_payload_and_case_insensitive_report_identifier()
    {
        const string body = "{\"BBID\":\"R-100\",\"nested\":{\"keep\":true},\"unknownField\":[1,2,3]}";
        using var json = JsonDocument.Parse(body);

        var request = new LegacyReportRequestAdapter().Adapt(json.RootElement);

        Assert.Equal("R-100", request.ReportId);
        Assert.Equal("legacy", request.Profile);
        Assert.True(request.Parameters.ContainsKey("nested"));
        Assert.True(request.Parameters.ContainsKey("unknownField"));
        Assert.NotNull(request.LegacyPayload);
        Assert.Equal(body, request.LegacyPayload!.Value.GetRawText());
    }

    [Fact]
    public void Endpoint_contract_retains_legacy_controller_action_route_and_post_method()
    {
        var controllerRoute = typeof(ReportsController).GetCustomAttribute<RouteAttribute>();
        var method = typeof(ReportsController).GetMethod(nameof(ReportsController.GetReportByJson));

        Assert.NotNull(controllerRoute);
        Assert.Equal("api/[controller]/[action]", controllerRoute!.Template);
        Assert.NotNull(method);
        Assert.NotNull(method!.GetCustomAttribute<HttpPostAttribute>());
        Assert.Equal("GetReportByJson", method.Name);
    }

    [Fact]
    public async Task Controller_returns_direct_pdf_file_without_json_wrapper()
    {
        using var json = JsonDocument.Parse("{\"bbid\":\"GUIDE_A4\"}");
        var controller = new ReportsController(new FixedPdfRenderer(), new LegacyReportRequestAdapter());

        var action = await controller.GetReportByJson(json.RootElement, CancellationToken.None);

        var file = Assert.IsType<FileContentResult>(action);
        Assert.Equal("application/pdf", file.ContentType);
        Assert.Equal("legacy.pdf", file.FileDownloadName);
        Assert.Equal(new byte[] { 1, 2, 3 }, file.FileContents);
    }

    private sealed class FixedPdfRenderer : IReportRenderer
    {
        public Task<ReportRenderResult> RenderPdfAsync(ReportRenderRequest request, CancellationToken cancellationToken)
            => Task.FromResult(new ReportRenderResult([1, 2, 3], "legacy.pdf", 1, []));
    }
}
