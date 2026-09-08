using System.Text;
using System.Data;
using System.Text.Json;
using PEIS.Report.Contracts;
using PEIS.Report.Engine;
using PEIS.Report.Infrastructure.SqlServer;
using Xunit;

namespace PEIS.Report.Infrastructure.SqlServer.Tests;

public sealed class LegacyRealContractTests
{
    [Fact]
    public void Resolver_prefers_bbid_when_querytype_is_djwh()
    {
        using var document = JsonDocument.Parse("{\"querytype\":\"djwh\",\"bbid\":\"xmtm\",\"djid\":\"unverified-secondary-id\"}");
        var request = new ReportRenderRequest("xmtm", new Dictionary<string, JsonElement>(), LegacyPayload: document.RootElement.Clone());

        var result = new LegacyPayloadReportResolver().Resolve(request);

        Assert.Equal("xmtm", result.DefinitionId);
        Assert.Equal("legacy-payload:querytype=djwh;bbid->djid", result.IdentifierSource);
    }

    [Fact]
    public void Resolver_uses_request_reportid_when_no_matching_payload_pattern()
    {
        using var document = JsonDocument.Parse("{\"querytype\":\"djid\",\"bbid\":\"xmtm\",\"djid\":\"unverified-secondary-id\"}");
        var request = new ReportRenderRequest("typed-report-id", new Dictionary<string, JsonElement>(), LegacyPayload: document.RootElement.Clone());

        var result = new LegacyPayloadReportResolver().Resolve(request);

        Assert.Equal("typed-report-id", result.DefinitionId);
        Assert.Equal("legacy-payload:unverified-id-family-fallback", result.IdentifierSource);
    }

    [Fact]
    public void DataTable_column_lookup_is_case_insensitive()
    {
        var master = new DataTable("Master");
        master.Columns.Add("XMMC", typeof(string));

        var lowerCaseLookup = master.Columns["xmmc"];

        Assert.True(master.Columns.Contains("xmmc"));
        Assert.NotNull(lowerCaseLookup);
        Assert.Same(master.Columns["XMMC"], lowerCaseLookup);
    }

    [Fact]
    public async Task Template_provider_decodes_confirmed_base64_utf8_frx_storage()
    {
        const string frx = "<?xml version=\"1.0\" encoding=\"utf-8\"?><Report />";
        var definition = new ReportDefinition(
            "xmtm",
            "fixture-v1",
            "legacy-db:xmtm",
            "exec tjxt_fastreportgetTxmxx @grtjgcjjgid,@sfxmddid",
            new Dictionary<string, string>
            {
                ["templateContentEncoding"] = "Base64Utf8",
                ["resultSet:0:tableName"] = "Master"
            },
            DateTimeOffset.UtcNow,
            "real-legacy-contract-fixture",
            Convert.ToBase64String(Encoding.UTF8.GetBytes(frx)));

        var template = await new LegacyDatabaseTemplateProvider().GetRequiredAsync(definition, CancellationToken.None);

        Assert.Equal(frx, template.Content);
        Assert.StartsWith("<?xml", template.Content, StringComparison.Ordinal);
        Assert.Equal("Master", definition.ParameterMetadata["resultSet:0:tableName"]);
    }

    [Fact]
    public async Task Dump_All_54_Report_Definitions()
    {
        const string connStr = "Server=192.168.0.237;Database=TJXT0616;User ID=sa;Password=Sxyckj#123;TrustServerCertificate=True;";
        await using var conn = new Microsoft.Data.SqlClient.SqlConnection(connStr);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT xh, djid, djmc, djlx, CASE WHEN dj_frx IS NOT NULL AND LEN(dj_frx)>0 THEN 1 ELSE 0 END AS has_frx, CASE WHEN djsql IS NOT NULL AND DATALENGTH(djsql)>0 THEN 1 ELSE 0 END AS has_sql FROM dbo.xt_bgdy_djwh_zzj ORDER BY xh";
        await using var reader = await cmd.ExecuteReaderAsync();
        var sb = new StringBuilder();
        while (await reader.ReadAsync())
        {
            sb.AppendLine($"xh:{reader[0]}, djid:{reader[1]}, djmc:{reader[2]}, djlx:{reader[3]}, frx:{reader[4]}, sql:{reader[5]}");
        }
        Assert.True(sb.Length > 0);
    }
}
