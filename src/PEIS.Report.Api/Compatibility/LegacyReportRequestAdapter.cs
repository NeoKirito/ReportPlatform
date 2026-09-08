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
    private static readonly string[] ReportIdCandidates =
    [
        "bbid", "djid", "cxid", "reportId", "report_id", "reportID",
        "bgid", "bgurl", "templateid", "templateId", "id", "xh", "bgmc", "djmc"
    ];

    public ReportRenderRequest Adapt(JsonElement data)
    {
        if (data.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("Legacy GetReportByJson body must be a JSON object.");

        var raw = data.Clone();
        var parameters = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        string? reportId = null;
        string? fileName = null;

        // 1. Process root properties
        foreach (var property in raw.EnumerateObject())
        {
            parameters[property.Name] = property.Value.Clone();
            if (string.Equals(property.Name, "fileName", StringComparison.OrdinalIgnoreCase) && property.Value.ValueKind == JsonValueKind.String)
                fileName = property.Value.GetString();

            if (reportId is null && ReportIdCandidates.Any(x =>
                    string.Equals(x, property.Name, StringComparison.OrdinalIgnoreCase)))
            {
                var str = JsonScalarToString(property.Value);
                if (!string.IsNullOrWhiteSpace(str))
                    reportId = str;
            }
        }

        // 2. Check nested objects (e.g. data, report, params, request) for reportId, fileName, and additional parameters
        foreach (var property in raw.EnumerateObject())
        {
            if (property.Value.ValueKind != JsonValueKind.Object)
                continue;

            foreach (var subProp in property.Value.EnumerateObject())
            {
                if (!parameters.ContainsKey(subProp.Name))
                    parameters[subProp.Name] = subProp.Value.Clone();

                if (fileName is null && string.Equals(subProp.Name, "fileName", StringComparison.OrdinalIgnoreCase) && subProp.Value.ValueKind == JsonValueKind.String)
                    fileName = subProp.Value.GetString();

                if (reportId is null && ReportIdCandidates.Any(x =>
                        string.Equals(x, subProp.Name, StringComparison.OrdinalIgnoreCase)))
                {
                    var str = JsonScalarToString(subProp.Value);
                    if (!string.IsNullOrWhiteSpace(str))
                        reportId = str;
                }
            }
        }

        return new ReportRenderRequest(
            ReportId: reportId ?? "LEGACY",
            Parameters: parameters,
            Profile: "legacy",
            Watermark: null,
            FileName: fileName,
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
