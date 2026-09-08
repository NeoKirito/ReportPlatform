using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Options;
using PEIS.Report.Contracts;
using PEIS.Report.Engine;
using PEIS.Report.FastReport.OpenSource;
using PEIS.Report.Infrastructure.SqlServer;
using Xunit;
using Xunit.Abstractions;

namespace PEIS.Report.Infrastructure.SqlServer.Tests;

[Trait("Category", "RequiresLegacySqlServer")]
public class UniversalPipelineLiveTests
{
    private readonly ITestOutputHelper _output;

    public UniversalPipelineLiveTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Theory]
    [InlineData("xmtm", "724071198644850688")]
    [InlineData("tjsfd", "724071198644850688")]
    [InlineData("tjdjd", "724071198644850688")]
    [InlineData("tjzydjd", "724071198644850688")]
    [InlineData("jktjbbd", "724071198644850688")]
    [InlineData("zytjbbd", "724071198644850688")]
    [InlineData("dwtjbgd", "724071198644850688")]
    [InlineData("dwzytjbgd", "724071198644850688")]
    [InlineData("xdwtjbgd", "724071198644850688")]
    [InlineData("tjwts", "724071198644850688")]
    public async Task TestReportLiveRendering(string reportId, string testId)
    {
        var dbOptions = Options.Create(new ReportDatabaseOptions
        {
            Provider = "SqlServer",
            ConnectionString = "Server=192.168.0.237;Database=TJXT0616;User ID=sa;Password=Sxyckj#123;TrustServerCertificate=True;",
            CommandTimeoutSeconds = 30
        });

        var schemaMapping = Options.Create(new LegacyReportSchemaMapping
        {
            DefinitionTable = "dbo.xt_bgdy_djwh_zzj",
            ReportIdColumn = "djid",
            ReportNameColumn = "djmc",
            TemplateColumn = "dj_frx",
            SqlColumn = "djsql",
            TemplateContentEncoding = "Base64Utf8",
            FirstResultSetTableName = "Master",
            SupplementalQueryOrderColumn = "xh",
            TemplateKeyPrefix = "legacy-djwh"
        });

        var resolver = new LegacyPayloadReportResolver();
        var defProvider = new LegacyDatabaseReportDefinitionProvider(dbOptions, schemaMapping, TimeProvider.System, resolver);
        var templateProvider = new LegacyDatabaseTemplateProvider();
        var binder = new AdoNetLegacyQueryParameterBinder();
        var dataProvider = new SqlServerReportDataProvider(dbOptions, binder);
        var runtime = new OpenSourceFastReportRuntime();
        var telemetry = new InMemoryReportRenderTelemetry();
        using var watermark = new SqlServerWatermarkTextProvider(Options.Create(new WatermarkDatabaseOptions()), dbOptions);

        var renderer = new FastReportReportRenderer(
            new ReportDefinitionCache(),
            defProvider,
            templateProvider,
            dataProvider,
            new RenderConcurrencyGate(new RenderConcurrencyOptions { MaxConcurrentRenders = 4 }),
            runtime,
            watermark,
            telemetry);

        var payloadDict = new Dictionary<string, object>
        {
            ["djid"] = testId,
            ["grtjgcjjgid"] = testId,
            ["bbid"] = reportId
        };
        var payloadJson = JsonSerializer.Serialize(payloadDict);
        using var jsonDoc = JsonDocument.Parse(payloadJson);
        var request = new ReportRenderRequest(
            reportId,
            new Dictionary<string, JsonElement>
            {
                ["djid"] = jsonDoc.RootElement.GetProperty("djid").Clone()
            },
            "legacy",
            null,
            null,
            jsonDoc.RootElement.Clone());

        var result = await renderer.RenderPdfAsync(request, CancellationToken.None);
        Assert.NotNull(result);
        Assert.True(result.PageCount > 0, $"Report {reportId} should have > 0 pages, got {result.PageCount}");
        Assert.NotEmpty(result.Pdf);
        Assert.StartsWith("%PDF-", System.Text.Encoding.ASCII.GetString(result.Pdf.AsSpan(0, Math.Min(5, result.Pdf.Length))));

        _output.WriteLine($"[PASS] {reportId}: {result.PageCount} pages, {result.Pdf.Length} bytes PDF generated!");
    }

    [Fact]
    public async Task Test_UserScenario_TianYao_HealthReport()
    {
        var dbOptions = Options.Create(new ReportDatabaseOptions
        {
            Provider = "SqlServer",
            ConnectionString = "Server=192.168.0.237;Database=TJXT0616;User ID=sa;Password=Sxyckj#123;TrustServerCertificate=True;",
            CommandTimeoutSeconds = 30
        });

        var schemaMapping = Options.Create(new LegacyReportSchemaMapping
        {
            DefinitionTable = "dbo.xt_bgdy_djwh_zzj",
            ReportIdColumn = "djid",
            ReportNameColumn = "djmc",
            TemplateColumn = "dj_frx",
            SqlColumn = "djsql",
            TemplateContentEncoding = "Base64Utf8",
            FirstResultSetTableName = "Master",
            SupplementalQueryOrderColumn = "xh",
            TemplateKeyPrefix = "legacy-djwh"
        });

        var resolver = new LegacyPayloadReportResolver();
        var defProvider = new LegacyDatabaseReportDefinitionProvider(dbOptions, schemaMapping, TimeProvider.System, resolver);
        var templateProvider = new LegacyDatabaseTemplateProvider();
        var binder = new AdoNetLegacyQueryParameterBinder();
        var dataProvider = new SqlServerReportDataProvider(dbOptions, binder);
        var runtime = new OpenSourceFastReportRuntime();
        var telemetry = new InMemoryReportRenderTelemetry();
        using var watermark = new SqlServerWatermarkTextProvider(Options.Create(new WatermarkDatabaseOptions()), dbOptions);

        var renderer = new FastReportReportRenderer(
            new ReportDefinitionCache(),
            defProvider,
            templateProvider,
            dataProvider,
            new RenderConcurrencyGate(new RenderConcurrencyOptions { MaxConcurrentRenders = 4 }),
            runtime,
            watermark,
            telemetry);

        var payloadJson = """
        {
            "hospitalid": "1",
            "tjryidArr": "B7B173A4126C46169A86E2F38EC34927",
            "filename": "田耀",
            "grtjgcjjgidArr": "E7B68873A60C471ABC9420F77E8246E3",
            "url": "http://192.168.0.237:8081/jmreport/exportPdfStream",
            "templateid": "828425886748311552"
        }
        """;

        using var doc = JsonDocument.Parse(payloadJson);
        var parameters = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        foreach (var prop in doc.RootElement.EnumerateObject())
            parameters[prop.Name] = prop.Value.Clone();

        var request = new ReportRenderRequest(
            "828425886748311552",
            parameters,
            "legacy",
            null,
            "田耀",
            doc.RootElement.Clone());

        var result = await renderer.RenderPdfAsync(request, CancellationToken.None);
        Assert.NotNull(result);
        Assert.True(result.PageCount > 0);
        Assert.NotEmpty(result.Pdf);
        Assert.StartsWith("%PDF-", System.Text.Encoding.ASCII.GetString(result.Pdf.AsSpan(0, Math.Min(5, result.Pdf.Length))));
        _output.WriteLine($"[PASS] Tian Yao Report: {result.PageCount} pages, {result.Pdf.Length} bytes PDF generated!");
    }

    [Fact]
    public async Task Test_UserScenario_FeeReceipt_Report()
    {
        var dbOptions = Options.Create(new ReportDatabaseOptions
        {
            Provider = "SqlServer",
            ConnectionString = "Server=192.168.0.237;Database=TJXT0616;User ID=sa;Password=Sxyckj#123;TrustServerCertificate=True;",
            CommandTimeoutSeconds = 30
        });

        var schemaMapping = Options.Create(new LegacyReportSchemaMapping
        {
            DefinitionTable = "dbo.xt_bgdy_djwh_zzj",
            ReportIdColumn = "djid",
            ReportNameColumn = "djmc",
            TemplateColumn = "dj_frx",
            SqlColumn = "djsql",
            TemplateContentEncoding = "Base64Utf8",
            FirstResultSetTableName = "Master",
            SupplementalQueryOrderColumn = "xh",
            TemplateKeyPrefix = "legacy-djwh"
        });

        var resolver = new LegacyPayloadReportResolver();
        var defProvider = new LegacyDatabaseReportDefinitionProvider(dbOptions, schemaMapping, TimeProvider.System, resolver);
        var templateProvider = new LegacyDatabaseTemplateProvider();
        var binder = new AdoNetLegacyQueryParameterBinder();
        var dataProvider = new SqlServerReportDataProvider(dbOptions, binder);
        var runtime = new OpenSourceFastReportRuntime();
        var telemetry = new InMemoryReportRenderTelemetry();
        using var watermark = new SqlServerWatermarkTextProvider(Options.Create(new WatermarkDatabaseOptions()), dbOptions);

        var renderer = new FastReportReportRenderer(
            new ReportDefinitionCache(),
            defProvider,
            templateProvider,
            dataProvider,
            new RenderConcurrencyGate(new RenderConcurrencyOptions { MaxConcurrentRenders = 4 }),
            runtime,
            watermark,
            telemetry);

        var payloadJson = """
        {
            "hospitalid": "1",
            "grtjgcjjgidArr": "D832A2277D9C4FCD854A75091CC11D77",
            "templateid": "773424455578746880",
            "url": "http://192.168.0.237:8081/jmreport/exportPdfStream",
            "tjjfjlid": "9E7BBF4ABA6C4B5C85535B50E0FDAC95",
            "tjryidArr": "D6AE391A866B4D028C090A6A5923854D",
            "filename": "个人费用单据",
            "tjfyrzid": "undefined"
        }
        """;

        using var doc = JsonDocument.Parse(payloadJson);
        var parameters = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        foreach (var prop in doc.RootElement.EnumerateObject())
            parameters[prop.Name] = prop.Value.Clone();

        var request = new ReportRenderRequest(
            "773424455578746880",
            parameters,
            "legacy",
            null,
            "个人费用单据",
            doc.RootElement.Clone());

        var result = await renderer.RenderPdfAsync(request, CancellationToken.None);
        Assert.NotNull(result);
        Assert.True(result.PageCount > 0);
        Assert.NotEmpty(result.Pdf);
        Assert.StartsWith("%PDF-", System.Text.Encoding.ASCII.GetString(result.Pdf.AsSpan(0, Math.Min(5, result.Pdf.Length))));
        _output.WriteLine($"[PASS] Fee Receipt Report: {result.PageCount} pages, {result.Pdf.Length} bytes PDF generated!");
    }
}
