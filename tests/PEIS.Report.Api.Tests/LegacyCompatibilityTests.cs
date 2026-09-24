using System.Reflection;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
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
        var pdfPostRoutes = pdfMethod!.GetCustomAttributes<HttpPostAttribute>().ToArray();
        Assert.Contains(pdfPostRoutes, route => route.Template is null);
        Assert.Contains(pdfPostRoutes, route => route.Template == "/BaseInfo/Report/GetReportByJson");
        Assert.Contains(pdfPostRoutes, route => route.Template == "/TJ/exportTemplate/exportPdf");
        Assert.Contains(pdfPostRoutes, route => route.Template == "/jmreport/exportPdfStream");

        var pdfGetRoutes = pdfMethod!.GetCustomAttributes<HttpGetAttribute>().ToArray();
        Assert.Contains(pdfGetRoutes, route => route.Template == "/BaseInfo/Report/GetReportByJson");
        Assert.Contains(pdfGetRoutes, route => route.Template == "/TJ/exportTemplate/exportPdf");
        Assert.Contains(pdfGetRoutes, route => route.Template == "/jmreport/exportPdfStream");

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
    public async Task Controller_parses_query_string_when_body_is_empty()
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Request.QueryString = new QueryString("?templateid=773424455578746880&filename=测试单据&tjjfjlid=123");
        var controller = new ReportsController(new FixedPdfRenderer(), new FixedDocxExporter(), new LegacyReportRequestAdapter())
        {
            ControllerContext = new ControllerContext { HttpContext = httpContext }
        };

        var action = await controller.GetReportByJson(null, CancellationToken.None);

        var file = Assert.IsType<FileContentResult>(action);
        Assert.Equal("application/pdf", file.ContentType);
        Assert.Equal("legacy.pdf", file.FileDownloadName);
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

    [Theory]
    [InlineData("bbid", "R-BBID")]
    [InlineData("BBID", "R-UPPER")]
    [InlineData("djid", "R-DJID")]
    [InlineData("cxid", "R-CXID")]
    [InlineData("reportId", "R-REPID")]
    [InlineData("report_id", "R-REP_ID")]
    [InlineData("templateid", "R-TPLID")]
    [InlineData("templateId", "R-TPL_ID")]
    [InlineData("id", "R-ID")]
    [InlineData("bgid", "R-BGID")]
    [InlineData("bgmc", "R-BGMC")]
    [InlineData("djmc", "R-DJMC")]
    public void Adapter_resolves_report_id_from_various_candidate_keys(string fieldName, string expectedId)
    {
        var json = $"{{\"{fieldName}\":\"{expectedId}\",\"tjh\":\"TJ-001\"}}";
        using var doc = JsonDocument.Parse(json);
        var request = new LegacyReportRequestAdapter().Adapt(doc.RootElement);

        Assert.Equal(expectedId, request.ReportId);
        Assert.Equal("TJ-001", request.Parameters["tjh"].GetString());
    }

    [Fact]
    public void Adapter_resolves_numeric_report_id_correctly()
    {
        using var doc = JsonDocument.Parse("{\"templateid\": 773424455578746880, \"fileName\": \"MyReport\"}");
        var request = new LegacyReportRequestAdapter().Adapt(doc.RootElement);

        Assert.Equal("773424455578746880", request.ReportId);
        Assert.Equal("MyReport", request.FileName);
    }

    [Fact]
    public void Adapter_resolves_nested_report_id_and_flattened_parameters()
    {
        using var doc = JsonDocument.Parse("""
            {
                "data": {
                    "bbid": "NESTED-REPORT",
                    "patientName": "李四",
                    "age": 45
                },
                "fileName": "NestedReport.pdf"
            }
            """);
        var request = new LegacyReportRequestAdapter().Adapt(doc.RootElement);

        Assert.Equal("NESTED-REPORT", request.ReportId);
        Assert.Equal("NestedReport.pdf", request.FileName);
        Assert.Equal("李四", request.Parameters["patientName"].GetString());
        Assert.Equal(45, request.Parameters["age"].GetInt32());
    }

    [Fact]
    public void Adapter_handles_watermark_in_various_formats()
    {
        var adapter = new LegacyReportRequestAdapter();

        // 1. watermark: false
        using (var d = JsonDocument.Parse("{\"bbid\":\"R1\",\"watermark\":false}"))
        {
            var r = adapter.Adapt(d.RootElement);
            Assert.NotNull(r.Watermark);
            Assert.False(r.Watermark!.Enabled);
        }

        // 2. watermark: true
        using (var d = JsonDocument.Parse("{\"bbid\":\"R1\",\"watermark\":true}"))
        {
            var r = adapter.Adapt(d.RootElement);
            Assert.NotNull(r.Watermark);
            Assert.True(r.Watermark!.Enabled);
        }

        // 3. watermark: string
        using (var d = JsonDocument.Parse("{\"bbid\":\"R1\",\"watermark\":\"内部机密\"}"))
        {
            var r = adapter.Adapt(d.RootElement);
            Assert.NotNull(r.Watermark);
            Assert.True(r.Watermark!.Enabled);
            Assert.Equal("内部机密", r.Watermark!.Text);
        }

        // 4. watermark: full object
        using (var d = JsonDocument.Parse("""
            {
                "bbid": "R1",
                "watermark": {
                    "enabled": true,
                    "text": "高级定制水印",
                    "opacity": 0.28,
                    "angle": -45.0,
                    "fontSize": 64.0
                }
            }
            """))
        {
            var r = adapter.Adapt(d.RootElement);
            Assert.NotNull(r.Watermark);
            Assert.True(r.Watermark!.Enabled);
            Assert.Equal("高级定制水印", r.Watermark!.Text);
            Assert.Equal(0.28, r.Watermark!.Opacity);
            Assert.Equal(-45.0, r.Watermark!.Angle);
            Assert.Equal(64.0f, r.Watermark!.FontSize);
        }

        // 5. watermarkEnabled: "false" (string)
        using (var d = JsonDocument.Parse("{\"bbid\":\"R1\",\"watermarkEnabled\":\"false\"}"))
        {
            var r = adapter.Adapt(d.RootElement);
            Assert.NotNull(r.Watermark);
            Assert.False(r.Watermark!.Enabled);
        }

        // 6. watermarkText: "院内预览"
        using (var d = JsonDocument.Parse("{\"bbid\":\"R1\",\"watermarkText\":\"院内预览\"}"))
        {
            var r = adapter.Adapt(d.RootElement);
            Assert.NotNull(r.Watermark);
            Assert.True(r.Watermark!.Enabled);
            Assert.Equal("院内预览", r.Watermark!.Text);
        }
    }

    [Theory]
    [InlineData(LegacyReportDatabaseErrorCode.ReportNotFound, StatusCodes.Status404NotFound)]
    [InlineData(LegacyReportDatabaseErrorCode.TemplateNotFound, StatusCodes.Status404NotFound)]
    [InlineData(LegacyReportDatabaseErrorCode.ParameterBindFailed, StatusCodes.Status400BadRequest)]
    [InlineData(LegacyReportDatabaseErrorCode.DatabaseConnectionFailed, StatusCodes.Status500InternalServerError)]
    public async Task Controller_maps_database_error_codes_to_proper_http_status(
        LegacyReportDatabaseErrorCode code, int expectedHttpStatus)
    {
        var failingRenderer = new ThrowingRenderer(new LegacyReportDatabaseException(code, $"Error for {code}"));
        var controller = new ReportsController(failingRenderer, new FixedDocxExporter(), new LegacyReportRequestAdapter())
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };

        using var json = JsonDocument.Parse("{\"bbid\":\"R-FAIL\"}");
        var action = await controller.GetReportByJson(json.RootElement, CancellationToken.None);

        var statusResult = Assert.IsType<ObjectResult>(action);
        Assert.Equal(expectedHttpStatus, statusResult.StatusCode);
    }

    [Fact]
    public async Task Controller_returns_500_on_unexpected_exception()
    {
        var failingRenderer = new ThrowingRenderer(new InvalidOperationException("Something unexpected crashed"));
        var controller = new ReportsController(failingRenderer, new FixedDocxExporter(), new LegacyReportRequestAdapter())
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };

        using var json = JsonDocument.Parse("{\"bbid\":\"R-FAIL\"}");
        var action = await controller.GetReportByJson(json.RootElement, CancellationToken.None);

        var statusResult = Assert.IsType<ObjectResult>(action);
        Assert.Equal(StatusCodes.Status500InternalServerError, statusResult.StatusCode);
    }

    private sealed class ThrowingRenderer(Exception exception) : IReportRenderer
    {
        public Task<ReportRenderResult> RenderPdfAsync(ReportRenderRequest request, CancellationToken cancellationToken)
            => Task.FromException<ReportRenderResult>(exception);
    }

    [Fact]
    public void FastReportReportRenderer_can_be_activated_by_service_provider_without_constructor_ambiguity()
    {
        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
        services.AddOptions();
        services.AddSingleton(new ReportDefinitionCache());
        services.AddSingleton<IReportDefinitionProvider, DeterministicReportDefinitionProvider>();
        services.AddSingleton<ITemplateProvider, DeterministicTemplateProvider>();
        services.AddSingleton<IReportDataProvider, EmptyReportDataProvider>();
        services.AddSingleton(new RenderConcurrencyGate(new RenderConcurrencyOptions()));
        services.AddSingleton<IFastReportRuntime>(new DummyRuntime());
        services.AddSingleton<IWatermarkTextProvider, DisabledWatermarkTextProvider>();
        services.AddSingleton<IWatermarkResolver, DefaultWatermarkResolver>();
        services.AddSingleton<IPdfSecurityService, PdfSecurityService>();
        services.AddSingleton<IReportRenderTelemetry, InMemoryReportRenderTelemetry>();
        services.AddSingleton<IReportRenderer, FastReportReportRenderer>();

        using var provider = services.BuildServiceProvider();
        var renderer = provider.GetRequiredService<IReportRenderer>();

        Assert.NotNull(renderer);
        Assert.IsType<FastReportReportRenderer>(renderer);
    }

    private sealed class DummyRuntime : IFastReportRuntime
    {
        public Task<FastReportRuntimePreparation> PrepareAsync(FastReportRenderContext context, CancellationToken cancellationToken)
            => throw new NotImplementedException();
        public Task ApplyWatermarkAsync(IFastReportPreparedDocument prepared, WatermarkOptions watermark, CancellationToken cancellationToken)
            => Task.CompletedTask;
        public Task<FastReportPdfOutput> ExportPdfAsync(IFastReportPreparedDocument prepared, PdfExportProfile profile, CancellationToken cancellationToken)
            => throw new NotImplementedException();
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

