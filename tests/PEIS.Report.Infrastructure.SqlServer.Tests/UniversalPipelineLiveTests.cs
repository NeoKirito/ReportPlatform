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

    [Fact]
    public async Task Test_UserScenario_XuYanXue_OccupationalReport()
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
        var imageResolver = new ImageResolver(new HttpClient(), new ImageResolutionOptions { TimeoutMilliseconds = 150, MaxConcurrentFetches = 12, FailureCacheSeconds = 3600 });
        var runtime = new OpenSourceFastReportRuntime(imageResolver);
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
            "tjryidArr": "D6AE391A866B4D028C090A6A5923854D",
            "filename": "徐燕雪（陈欣欣）        ",
            "grtjgcjjgidArr": "D832A2277D9C4FCD854A75091CC11D77",
            "url": "http://192.168.0.237:8081/jmreport/exportPdfStream",
            "templateid": "837209944550735872"
        }
        """;

        using var doc = JsonDocument.Parse(payloadJson);
        var parameters = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        foreach (var prop in doc.RootElement.EnumerateObject())
            parameters[prop.Name] = prop.Value.Clone();

        var request = new ReportRenderRequest(
            "837209944550735872",
            parameters,
            "legacy",
            null,
            "徐燕雪（陈欣欣）        ",
            doc.RootElement.Clone());

        var swTotal = System.Diagnostics.Stopwatch.StartNew();

        // 1. Definition resolution
        var swDef = System.Diagnostics.Stopwatch.StartNew();
        var definition = await defProvider.GetRequiredAsync(request, CancellationToken.None);
        swDef.Stop();
        _output.WriteLine($"[TIMING] Definition resolution: {swDef.ElapsedMilliseconds} ms (ReportId={definition.ReportId}, Key={definition.TemplateKey})");

        // 2. Data queries
        var swData = System.Diagnostics.Stopwatch.StartNew();
        var data = await dataProvider.QueryAsync(definition, request, CancellationToken.None);
        swData.Stop();
        _output.WriteLine($"[TIMING] Data retrieval: {swData.ElapsedMilliseconds} ms ({data.Tables.Count} tables retrieved)");
        foreach (var kvp in data.Tables)
        {
            _output.WriteLine($"    Table '{kvp.Key}': {kvp.Value.Rows.Count} rows, {kvp.Value.Columns.Count} cols");
        }

        // 3. Render PDF
        var swRender = System.Diagnostics.Stopwatch.StartNew();
        var result = await renderer.RenderPdfAsync(request, CancellationToken.None);
        swRender.Stop();
        swTotal.Stop();

        _output.WriteLine($"[TIMING] Full RenderPdfAsync: {swRender.ElapsedMilliseconds} ms");
        _output.WriteLine($"[TIMING] Total Time: {swTotal.ElapsedMilliseconds} ms");
        _output.WriteLine($"[PASS] Xu Yan Xue Report: {result.PageCount} pages, {result.Pdf.Length} bytes PDF generated!");

        Assert.NotNull(result);
        Assert.True(result.PageCount > 0);
        Assert.NotEmpty(result.Pdf);
        Assert.StartsWith("%PDF-", System.Text.Encoding.ASCII.GetString(result.Pdf.AsSpan(0, Math.Min(5, result.Pdf.Length))));
    }

    [Fact]
    public async Task Test_Tjdj_GroupBillingReport()
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

        var rawJson = """
        {
            "pageNo": 1,
            "pageSize": 100,
            "djh": {
                "grtjgcjjgid": "",
                "dwtjgcjjgid": "ABFBCB023B6E4E5689495BAF24AE4681"
            },
            "yhmc": "",
            "bbid": "tjdj",
            "fileName": "团检单据",
            "querytype": "djwh"
        }
        """;
        using var doc = JsonDocument.Parse(rawJson);
        var request = new ReportRenderRequest(
            "tjdj",
            new Dictionary<string, JsonElement>
            {
                ["bbid"] = doc.RootElement.GetProperty("bbid").Clone(),
                ["dwtjgcjjgid"] = doc.RootElement.GetProperty("djh").GetProperty("dwtjgcjjgid").Clone()
            },
            "legacy",
            null,
            "团检单据",
            doc.RootElement.Clone());

        // 1. Definition
        var definition = await defProvider.GetRequiredAsync(request, CancellationToken.None);
        _output.WriteLine($"[DEF] ReportId: {definition.ReportId}, Supplemental queries count: {definition.SupplementalQueries?.Count}");
        if (definition.SupplementalQueries != null)
        {
            foreach (var sq in definition.SupplementalQueries)
            {
                _output.WriteLine($"  Supplemental query: TableName={sq.TableName}, SubReportId={sq.SubReportId}");
            }
        }

        // 2. Data
        var data = await dataProvider.QueryAsync(definition, request, CancellationToken.None);
        _output.WriteLine($"[DATA] Total tables: {data.Tables.Count}");
        foreach (var kvp in data.Tables)
        {
            _output.WriteLine($"  Table '{kvp.Key}': {kvp.Value.Rows.Count} rows, cols: [{string.Join(", ", kvp.Value.Columns.Cast<System.Data.DataColumn>().Select(c => c.ColumnName))}]");
        }







        var result = await renderer.RenderPdfAsync(request, CancellationToken.None);
        Assert.NotNull(result);
        Assert.True(result.PageCount > 0);
        Assert.NotEmpty(result.Pdf);
        Assert.StartsWith("%PDF-", System.Text.Encoding.ASCII.GetString(result.Pdf.AsSpan(0, Math.Min(5, result.Pdf.Length))));
        _output.WriteLine($"[PASS] tjdj PDF generated: {result.PageCount} pages, {result.Pdf.Length} bytes");
    }

    [Fact]
    public async Task Test_DynamicReportDiscoveryAndWarmupResilience()
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
        var cache = new ReportDefinitionCache();

        // 1. Test discovery
        var discovered = await defProvider.ListReportIdsAsync(CancellationToken.None);
        Assert.NotEmpty(discovered);
        _output.WriteLine($"[DISCOVERY] Discovered {discovered.Count} report templates: {string.Join(", ", discovered)}");
        Assert.Contains("tjdj", discovered);
        Assert.Contains("jktjbbd", discovered);

        // 2. Test warmup simulation with intentional bad report injected
        var testQueue = new List<string>(discovered);
        testQueue.Add("intentionally_invalid_report_12345");

        int success = 0;
        int failed = 0;

        foreach (var reportId in testQueue)
        {
            try
            {
                var req = new ReportRenderRequest(reportId, new Dictionary<string, JsonElement>());
                var def = await cache.GetOrCreateAsync(reportId, token => defProvider.GetRequiredAsync(req, token), CancellationToken.None);
                await templateProvider.GetRequiredAsync(def, CancellationToken.None);
                success++;
            }
            catch (Exception ex)
            {
                failed++;
                _output.WriteLine($"[WARMUP-ISOLATION] Safely caught and skipped faulty report '{reportId}': {ex.Message}");
            }
        }

        _output.WriteLine($"[WARMUP-SUMMARY] Total={testQueue.Count}, Success={success}, Skipped={failed}");
        Assert.True(failed >= 1, "Should catch and skip at least the injected invalid report");
        Assert.True(success >= 20, "Should successfully preload all valid reports");
    }
}

