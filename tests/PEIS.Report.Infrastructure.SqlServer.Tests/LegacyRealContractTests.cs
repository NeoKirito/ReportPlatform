using System.Text;
using System.Text.Json;
using PEIS.Report.Contracts;
using PEIS.Report.Engine;
using PEIS.Report.Infrastructure.SqlServer;
using Xunit;

namespace PEIS.Report.Infrastructure.SqlServer.Tests;

public sealed class LegacyRealContractTests
{
    [Fact]
    public void Djwh_bbid_payload_resolves_to_confirmed_djid_definition_key()
    {
        using var document = JsonDocument.Parse("{\"querytype\":\"djwh\",\"bbid\":\"xmtm\",\"djid\":\"unverified-secondary-id\"}");
        var request = new ReportRenderRequest("xmtm", new Dictionary<string, JsonElement>(), LegacyPayload: document.RootElement.Clone());

        var result = new LegacyPayloadReportResolver().Resolve(request);

        Assert.Equal("xmtm", result.DefinitionId);
        Assert.Equal("legacy-payload:querytype=djwh;bbid->djid", result.IdentifierSource);
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
}
