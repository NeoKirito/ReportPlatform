using System.Reflection;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using PEIS.Report.Api.Compatibility;
using PEIS.Report.Contracts;
using PEIS.Report.Docx.OpenXml;
using PEIS.Report.Engine;
using Xunit;

namespace PEIS.Report.Api.Tests;

public sealed class LegacyCompatibilityTests
{
    [Fact]
    public void Adapter_preserves_arbitrary_payload_and_case_insensitive_report_identifier()
    {
        const string body = "{\"BBID\":\"R-100\",\"nested\":{\"keep\":true},\"unknownField\":[1,2,3],\"fileName\":\"123456\"}";
        using var json = JsonDocument.Parse(body);

        var request = new LegacyReportRequestAdapter().Adapt(json.RootElement);

        Assert.Equal("R-100", request.ReportId);
        Assert.Equal("legacy", request.Profile);
        Assert.Equal("123456", request.FileName);
        Assert.True(request.Parameters.ContainsKey("nested"));
        Assert.True(request.Parameters.ContainsKey("unknownField"));
        Assert.NotNull(request.LegacyPayload);
        Assert.Equal(body, request.LegacyPayload!.Value.GetRawText());
    }

    [Fact]
    public void Endpoint_contract_retains_legacy_controller_action_route_and_post_method()
    {
        var controllerRoute = typeof(ReportsController).GetCustomAttribute<RouteAttribute>();
        var pdfMethod = typeof(ReportsController).GetMethod(nameof(ReportsController.GetReportByJson));
        var docxMethod = typeof(ReportsController).GetMethod(nameof(ReportsController.GetReportDocxByJson));

        Assert.NotNull(controllerRoute);
        Assert.Equal("api/[controller]/[action]", controllerRoute!.Template);
        Assert.NotNull(pdfMethod);
        var pdfRoutes = pdfMethod!.GetCustomAttributes<HttpPostAttribute>().ToArray();
        Assert.Contains(pdfRoutes, route => route.Template is null);
        Assert.Contains(pdfRoutes, route => route.Template == "/BaseInfo/Report/GetReportByJson");
        Assert.Equal("GetReportByJson", pdfMethod.Name);
        Assert.NotNull(docxMethod);
        Assert.NotNull(docxMethod!.GetCustomAttribute<HttpPostAttribute>());
        Assert.Equal("GetReportDocxByJson", docxMethod.Name);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public async Task Controller_returns_direct_pdf_file_without_json_wrapper(int unavailableImages)
    {
        using var json = JsonDocument.Parse("{\"bbid\":\"GUIDE_A4\"}");
        var controller = new ReportsController(new FixedPdfRenderer(unavailableImages), new FixedDocxExporter(), new LegacyReportRequestAdapter())
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };

        var action = await controller.GetReportByJson(json.RootElement, CancellationToken.None);

        var file = Assert.IsType<FileContentResult>(action);
        Assert.Equal("application/pdf", file.ContentType);
        Assert.Equal("legacy.pdf", file.FileDownloadName);
        Assert.Equal(new byte[] { 1, 2, 3 }, file.FileContents);
        Assert.Equal(unavailableImages == 0 ? "" : "2", controller.Response.Headers["X-ReportPlatform-Unavailable-Images"].ToString());
    }

    [Fact]
    public async Task Controller_returns_direct_docx_file_from_the_same_legacy_json_payload()
    {
        using var json = JsonDocument.Parse("{\"bbid\":\"GUIDE_A4\"}");
        var controller = new ReportsController(new FixedPdfRenderer(), new FixedDocxExporter(), new LegacyReportRequestAdapter())
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };

        var action = await controller.GetReportDocxByJson(json.RootElement, CancellationToken.None);

        var file = Assert.IsType<FileContentResult>(action);
        Assert.Equal("application/vnd.openxmlformats-officedocument.wordprocessingml.document", file.ContentType);
        Assert.Equal("legacy.docx", file.FileDownloadName);
        Assert.Equal(new byte[] { 80, 75, 3, 4 }, file.FileContents);
        Assert.Equal("2", controller.Response.Headers["X-ReportPlatform-Docx-Unsupported-Objects"].ToString());
    }

    private sealed class FixedPdfRenderer(int unavailableImages = 0) : IReportRenderer
    {
        public Task<ReportRenderResult> RenderPdfAsync(ReportRenderRequest request, CancellationToken cancellationToken)
            => Task.FromResult(new ReportRenderResult([1, 2, 3], "legacy.pdf", 1, []) { UnavailableImageCount = unavailableImages });
    }

    private sealed class FixedDocxExporter : IFrxDocxReportExporter
    {
        public Task<FrxDocxExportResult> ExportAsync(ReportRenderRequest request, CancellationToken cancellationToken)
            => Task.FromResult(new FrxDocxExportResult([80, 75, 3, 4], "legacy.docx", 1, 1, ["LineObject", "PictureObject"]));
    }
}
