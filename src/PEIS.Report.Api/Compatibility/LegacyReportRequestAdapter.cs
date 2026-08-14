using System.Text.Json;
using PEIS.Report.Contracts;

namespace PEIS.Report.Api.Compatibility;

/// <summary>
/// Keeps the legacy PEIS request body intact while adapting it to the new engine contract.
/// The HTTP compatibility endpoint accepts exactly the same arbitrary JSON object as the
/// old ReportsController.GetReportByJson(object data).
/// </summary>
public sealed class LegacyReportRequestAdapter
{
    private static readonly string[] ReportIdCandidates = ["bbid", "djid", "cxid", "reportId"];

    public ReportRenderRequest Adapt(JsonElement data)
    {
        if (data.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("Legacy GetReportByJson body must be a JSON object.");

        var raw = data.Clone();
        var parameters = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        string? reportId = null;

        foreach (var property in raw.EnumerateObject())
        {
            parameters[property.Name] = property.Value.Clone();

            if (reportId is null && ReportIdCandidates.Any(x =>
                    string.Equals(x, property.Name, StringComparison.OrdinalIgnoreCase)))
            {
                reportId = JsonScalarToString(property.Value);
            }
        }

        // The old implementation exposes a generic object contract. We preserve the entire
        // payload in LegacyPayload so the production FastReport adapter can reproduce the
        // old parameter semantics exactly instead of depending on this inference.
        return new ReportRenderRequest(
            ReportId: reportId ?? "LEGACY",
            Parameters: parameters,
            Profile: "legacy",
            Watermark: null,
            FileName: null,
            LegacyPayload: raw);
    }

    private static string? JsonScalarToString(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString(),
        JsonValueKind.Number => value.GetRawText(),
        JsonValueKind.True => bool.TrueString,
        JsonValueKind.False => bool.FalseString,
        _ => null
    };
}
