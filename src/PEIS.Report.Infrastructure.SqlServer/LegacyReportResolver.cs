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
    public LegacyReportResolution Resolve(ReportRenderRequest request)
    {
        if (request.LegacyPayload is not { ValueKind: JsonValueKind.Object } payload)
            return new LegacyReportResolution(request.ReportId, "typed-request-fallback");

        var queryType = ReadScalar(payload, "querytype");
        var bbid = ReadScalar(payload, "bbid");
        if (string.Equals(queryType, "djwh", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(bbid))
        {
            return new LegacyReportResolution(bbid, "legacy-payload:querytype=djwh;bbid->djid");
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
