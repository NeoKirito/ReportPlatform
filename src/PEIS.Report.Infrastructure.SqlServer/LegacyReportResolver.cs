using System.Text.Json;
using PEIS.Report.Contracts;

namespace PEIS.Report.Infrastructure.SqlServer;

/// <summary>
/// Resolves a database report-definition identifier from preserved legacy payload semantics.
/// The adapter remains responsible only for HTTP/JSON preservation; database selection lives here.
/// </summary>
public interface ILegacyReportResolver
{
    LegacyReportResolution Resolve(ReportRenderRequest request);
}

public sealed record LegacyReportResolution(string DefinitionId, string IdentifierSource);

/// <summary>
/// Evidence-backed resolver for the confirmed legacy guide-sheet path:
/// <c>querytype=djwh</c> with a <c>bbid</c> payload value selects the
/// <c>dbo.xt_bgdy_djwh_zzj.djid</c> definition key. Other ID families remain explicit fallbacks
/// until a real fixture confirms their database relationship.
/// </summary>
public sealed class LegacyPayloadReportResolver : ILegacyReportResolver
{
    private static readonly string[] PreferredIdKeys =
    [
        "bbid", "djid", "reportId", "report_id", "bgurl", "bgid",
        "templateId", "templateid", "cxid", "id", "xh", "djmc", "bgmc"
    ];

    private static readonly Dictionary<string, string> KnownNumericAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["724071198644850688"] = "jktjbbd",
        ["773424455578746880"] = "tjsfd",
        ["837103946955685888"] = "tjsfd",
        ["837209944550735872"] = "zytjbbd",
        ["729498909698621440"] = "tjdj",
        ["720128756003573760"] = "tjdjd",
        ["720128756003573761"] = "tjzydjd",
        ["720128756003573788"] = "dwzytjbgd",
        ["730268903554351104"] = "xdwtjbgd",
        ["828425886748311552"] = "jktjbbd",
        ["828425886748311555"] = "xmtm",
        ["828425886748311558"] = "jzkdypz",
        ["828425886748312345"] = "tjwts",
        ["828425886748344444"] = "ksfdj",
        ["828425886748354321"] = "tjzjwjxmdj",
        ["828425886748366666"] = "tjjkz",
        ["828425886748377777"] = "zyjjzgzs"
    };

    public LegacyReportResolution Resolve(ReportRenderRequest request)
    {
        if (request.LegacyPayload is not { ValueKind: JsonValueKind.Object } payload)
            return new LegacyReportResolution(request.ReportId, "typed-request-fallback");

        var queryType = ReadScalar(payload, "querytype");
        var bbid = ReadScalar(payload, "bbid");

        // 1. Confirmed legacy guide-sheet route: querytype=djwh + bbid
        if (string.Equals(queryType, "djwh", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(bbid))
        {
            if (KnownNumericAliases.TryGetValue(bbid, out var mapped))
                return new LegacyReportResolution(mapped, "legacy-payload:querytype=djwh;bbid->djid");
            return new LegacyReportResolution(bbid, "legacy-payload:querytype=djwh;bbid->djid");
        }

        // 2. Unverified querytype with typed report ID preserves fallback contract
        if (!string.IsNullOrWhiteSpace(queryType) && !string.Equals(queryType, "djwh", StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(request.ReportId) && !string.Equals(request.ReportId, "LEGACY", StringComparison.OrdinalIgnoreCase))
        {
            return new LegacyReportResolution(request.ReportId, "legacy-payload:unverified-id-family-fallback");
        }

        // 3. Check direct keys for universal resolution
        foreach (var key in PreferredIdKeys)
        {
            var value = ReadScalar(payload, key);
            if (!string.IsNullOrWhiteSpace(value))
            {
                if (KnownNumericAliases.TryGetValue(value, out var mapped))
                    return new LegacyReportResolution(mapped, $"legacy-payload:{key}->alias({value})");
                return new LegacyReportResolution(value, $"legacy-payload:{key}");
            }
        }

        // 4. Check nested objects
        foreach (var prop in payload.EnumerateObject())
        {
            if (prop.Value.ValueKind != JsonValueKind.Object)
                continue;

            foreach (var key in PreferredIdKeys)
            {
                var value = ReadScalar(prop.Value, key);
                if (!string.IsNullOrWhiteSpace(value))
                {
                    if (KnownNumericAliases.TryGetValue(value, out var mapped))
                        return new LegacyReportResolution(mapped, $"legacy-nested-payload:{prop.Name}.{key}->alias({value})");
                    return new LegacyReportResolution(value, $"legacy-nested-payload:{prop.Name}.{key}");
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(request.ReportId) && !string.Equals(request.ReportId, "LEGACY", StringComparison.OrdinalIgnoreCase))
        {
            if (KnownNumericAliases.TryGetValue(request.ReportId, out var mapped))
                return new LegacyReportResolution(mapped, "numeric-alias-map:" + request.ReportId);
            return new LegacyReportResolution(request.ReportId, "typed-request-id:" + request.ReportId);
        }

        return new LegacyReportResolution(request.ReportId, "legacy-payload:unverified-id-family-fallback");
    }

    private static string? ReadScalar(JsonElement payload, string propertyName)
    {
        foreach (var property in payload.EnumerateObject())
        {
            if (!string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase))
                continue;

            return property.Value.ValueKind switch
            {
                JsonValueKind.String => property.Value.GetString(),
                JsonValueKind.Number => property.Value.GetRawText(),
                JsonValueKind.True => bool.TrueString,
                JsonValueKind.False => bool.FalseString,
                _ => null
            };
        }

        return null;
    }
}
