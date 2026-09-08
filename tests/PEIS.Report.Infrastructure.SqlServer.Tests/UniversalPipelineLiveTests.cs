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
}
