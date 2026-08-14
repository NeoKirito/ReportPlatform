using System.Text.Json;
using PEIS.Report.Contracts;
using PEIS.Report.Engine;
using Xunit;

namespace PEIS.Report.Engine.Tests;

public sealed class LegacyDatabaseContractsTests
{
    [Fact]
    public async Task Version_change_uses_a_new_cache_key_and_reload()
    {
        var cache = new ReportDefinitionCache();
        var loads = 0;
        var firstKey = ReportDefinitionCache.BuildCacheKey("GUIDE_A4", new ReportDefinitionVersion("20260814121030", true, null, "updated_at"));
        var secondKey = ReportDefinitionCache.BuildCacheKey("GUIDE_A4", new ReportDefinitionVersion("20260814123000", true, null, "updated_at"));

        var first = await cache.GetOrCreateAsync(firstKey, _ => Task.FromResult(CreateDefinition("v1", ref loads)), CancellationToken.None);
        var second = await cache.GetOrCreateAsync(secondKey, _ => Task.FromResult(CreateDefinition("v2", ref loads)), CancellationToken.None);

        Assert.Equal("v1", first.Version);
        Assert.Equal("v2", second.Version);
        Assert.Equal(2, loads);
    }

    [Fact]
    public async Task Invalidate_report_removes_all_versioned_entries_and_forces_reload()
    {
        var cache = new ReportDefinitionCache();
        var calls = 0;
        var key = ReportDefinitionCache.BuildCacheKey("GUIDE_A4", new ReportDefinitionVersion("1", true, null, "version"));
        await cache.GetOrCreateAsync(key, _ => Task.FromResult(CreateDefinition("1", ref calls)), CancellationToken.None);
        await cache.GetOrCreateAsync(key, _ => Task.FromResult(CreateDefinition("1", ref calls)), CancellationToken.None);

        Assert.Equal(1, cache.InvalidateReport("guide_a4"));
        await cache.GetOrCreateAsync(key, _ => Task.FromResult(CreateDefinition("1", ref calls)), CancellationToken.None);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task Provider_failure_is_not_retained_in_definition_cache()
    {
        var cache = new ReportDefinitionCache();
        var key = ReportDefinitionCache.BuildCacheKey("GUIDE_A4", new ReportDefinitionVersion("1", true, null, "version"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => cache.GetOrCreateAsync(
            key,
            _ => Task.FromException<ReportDefinition>(new InvalidOperationException("fixture failure")),
            CancellationToken.None));

        var calls = 0;
        await cache.GetOrCreateAsync(key, _ => Task.FromResult(CreateDefinition("1", ref calls)), CancellationToken.None);
        Assert.Equal(1, calls);
    }

    [Fact]
    public void Binder_prefers_complete_legacy_payload_without_string_substitution()
    {
        using var payloadDocument = JsonDocument.Parse("{\"tjh\":\"LEGACY-001\",\"ignored\":\"x\"}");
        using var typedDocument = JsonDocument.Parse("{\"tjh\":\"TYPED-001\"}");
        var request = new ReportRenderRequest(
            "GUIDE_A4",
            new Dictionary<string, JsonElement> { ["tjh"] = typedDocument.RootElement.GetProperty("tjh").Clone() },
            LegacyPayload: payloadDocument.RootElement.Clone());
        var ignored = 0;
        var definition = CreateDefinition("1", ref ignored) with { SqlText = "select * from guide where tjh = @tjh" };

        var binding = new AdoNetLegacyQueryParameterBinder().Bind(definition, request);

        var parameter = Assert.Single(binding.Parameters);
        Assert.Equal("tjh", parameter.Name);
        Assert.Equal("LEGACY-001", parameter.Value);
        Assert.Equal("select * from guide where tjh = @tjh", binding.CommandText);
    }

    [Fact]
    public void Binder_translates_confirmed_bracket_placeholders_and_flattens_nested_legacy_payload()
    {
        using var payloadDocument = JsonDocument.Parse("{\"djh\":{\"grtjgcjjgid\":\"GROUP-001\",\"sfxmddid\":\"ITEM-001\"},\"bbid\":\"xmtm\"}");
        var request = new ReportRenderRequest("xmtm", new Dictionary<string, JsonElement>(), LegacyPayload: payloadDocument.RootElement.Clone());
        var ignored = 0;
        var definition = CreateDefinition("1", ref ignored) with { SqlText = "exec tjxt_fastreportgetTxmxx [grtjgcjjgid],[sfxmddid]" };

        var binding = new AdoNetLegacyQueryParameterBinder().Bind(definition, request);

        Assert.Equal("exec tjxt_fastreportgetTxmxx @grtjgcjjgid,@sfxmddid", binding.CommandText);
        Assert.Collection(binding.Parameters,
            parameter =>
            {
                Assert.Equal("grtjgcjjgid", parameter.Name);
                Assert.Equal(System.Data.DbType.AnsiString, parameter.DbType);
                Assert.Equal("GROUP-001", parameter.Value);
            },
            parameter =>
            {
                Assert.Equal("sfxmddid", parameter.Name);
                Assert.Equal(System.Data.DbType.AnsiString, parameter.DbType);
                Assert.Equal("ITEM-001", parameter.Value);
            });
    }

    [Fact]
    public void Binder_reports_missing_parameter_explicitly()
    {
        var ignored = 0;
        var definition = CreateDefinition("1", ref ignored) with { SqlText = "select * from guide where tjh = @tjh" };
        var request = new ReportRenderRequest("GUIDE_A4", new Dictionary<string, JsonElement>());

        var exception = Assert.Throws<LegacyReportDatabaseException>(() => new AdoNetLegacyQueryParameterBinder().Bind(definition, request));

        Assert.Equal(LegacyReportDatabaseErrorCode.ParameterBindFailed, exception.Code);
    }

    private static ReportDefinition CreateDefinition(string version, ref int calls)
    {
        calls++;
        return new ReportDefinition("GUIDE_A4", version, "guide", "select 1", new Dictionary<string, string>(), DateTimeOffset.UtcNow, "test");
    }

}
