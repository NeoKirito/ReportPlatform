using System.Data;
using System.Text.Json;
using PEIS.Report.Contracts;

namespace PEIS.Report.Engine;

public enum LegacyReportDatabaseErrorCode
{
    ReportNotFound,
    TemplateNotFound,
    QueryDefinitionNotFound,
    DatabaseConnectionFailed,
    DatabaseTimeout,
    ParameterBindFailed,
    QueryExecutionFailed,
    DataSetMappingFailed,
    SchemaMappingUnverified
}

public sealed class LegacyReportDatabaseException(
    LegacyReportDatabaseErrorCode code,
    string message,
    Exception? innerException = null) : Exception(message, innerException)
{
    public LegacyReportDatabaseErrorCode Code { get; } = code;
}

/// <summary>
/// A lightweight token obtained before definition loading. If the legacy schema exposes an update/version column,
/// the token represents that value; otherwise it represents a bounded TTL window and deliberately does not claim
/// database-change precision.
/// </summary>
public sealed record ReportDefinitionVersion(
    string CacheToken,
    bool IsDatabaseVersion,
    DateTimeOffset? ExpiresAt,
    string Source);

public interface IReportDefinitionVersionProvider
{
    Task<ReportDefinitionVersion> GetVersionAsync(ReportRenderRequest request, CancellationToken cancellationToken);
}

public sealed class ReportDefinitionCacheOptions
{
    /// <summary>Fallback refresh window for schemas without a usable version/update column.</summary>
    public int FallbackTtlSeconds { get; set; } = 300;
}

public sealed record LegacyQueryParameter(string Name, DbType DbType, object? Value);

public sealed record LegacyQueryBinding(string CommandText, IReadOnlyList<LegacyQueryParameter> Parameters);

/// <summary>
/// Isolates legacy SQL parameter semantics from ADO.NET execution. It receives the full render request so a future
/// implementation can consult the untouched LegacyPayload rather than relying only on a newly-designed DTO.
/// </summary>
public interface ILegacyQueryParameterBinder
{
    LegacyQueryBinding Bind(ReportDefinition definition, ReportRenderRequest request);
}

/// <summary>
/// Legacy binder with evidence-backed support for ADO.NET-style <c>@name</c> parameters and the confirmed
/// legacy forms <c>[name]</c> and <c>'${name}'</c>. Legacy tokens are transformed only when the preserved
/// payload supplies a matching scalar, so ordinary SQL identifiers and unresolved tokens remain untouched.
/// </summary>
public sealed class AdoNetLegacyQueryParameterBinder : ILegacyQueryParameterBinder
{
    private static readonly System.Text.RegularExpressions.Regex ParameterPattern = new(
        "(?<!@)@(?<name>[A-Za-z_][A-Za-z0-9_]*)",
        System.Text.RegularExpressions.RegexOptions.Compiled | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    private static readonly System.Text.RegularExpressions.Regex BracketParameterPattern = new(
        "\\[(?<name>[A-Za-z_][A-Za-z0-9_]*)\\]",
        System.Text.RegularExpressions.RegexOptions.Compiled | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    private static readonly System.Text.RegularExpressions.Regex QuotedDollarBraceParameterPattern = new(
        "'\\$\\{(?<name>[A-Za-z_][A-Za-z0-9_]*)\\}'",
        System.Text.RegularExpressions.RegexOptions.Compiled | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    public LegacyQueryBinding Bind(ReportDefinition definition, ReportRenderRequest request)
    {
        if (string.IsNullOrWhiteSpace(definition.SqlText))
            throw new LegacyReportDatabaseException(
                LegacyReportDatabaseErrorCode.QueryDefinitionNotFound,
                $"Report '{definition.ReportId}' has no SQL definition.");

        var source = BuildParameterIndex(request);
        bool TryResolveParameterName(string name, out string matchedName)
        {
            if (source.ContainsKey(name)) { matchedName = name; return true; }
            if (name.EndsWith("Arr", StringComparison.OrdinalIgnoreCase) && name.Length > 3 && source.ContainsKey(name[..^3])) { matchedName = name[..^3]; return true; }
            if (source.ContainsKey(name + "Arr")) { matchedName = name + "Arr"; return true; }
            matchedName = name;
            return false;
        }

        var bracketParameterNames = BracketParameterPattern.Matches(definition.SqlText)
            .Select(match => match.Groups["name"].Value)
            .Where(name => TryResolveParameterName(name, out _))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var dollarBraceParameterNames = QuotedDollarBraceParameterPattern.Matches(definition.SqlText)
            .Select(match => match.Groups["name"].Value)
            .Where(name => TryResolveParameterName(name, out _))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var commandText = QuotedDollarBraceParameterPattern.Replace(definition.SqlText, match =>
        {
            var name = match.Groups["name"].Value;
            return TryResolveParameterName(name, out var resolved) ? "@" + resolved : match.Value;
        });
        commandText = BracketParameterPattern.Replace(commandText, match =>
        {
            var name = match.Groups["name"].Value;
            return TryResolveParameterName(name, out var resolved) ? "@" + resolved : match.Value;
        });

        var parameters = new List<LegacyQueryParameter>();
        foreach (System.Text.RegularExpressions.Match match in ParameterPattern.Matches(commandText))
        {
            var name = match.Groups["name"].Value;
            if (parameters.Any(parameter => string.Equals(parameter.Name, name, StringComparison.OrdinalIgnoreCase)))
                continue;
            if (!source.TryGetValue(name, out var value))
            {
                if (name.EndsWith("Arr", StringComparison.OrdinalIgnoreCase) && source.TryGetValue(name[..^3], out var singular))
                    value = singular;
                else if (source.TryGetValue(name + "Arr", out var arrVal))
                    value = arrVal;
                else if (IsOptionalParameter(name))
                {
                    value = default;
                }
                else
                {
                    throw new LegacyReportDatabaseException(
                        LegacyReportDatabaseErrorCode.ParameterBindFailed,
                        $"Report '{definition.ReportId}' SQL requires parameter '@{name}', but legacy payload does not contain it.");
                }
            }

            parameters.Add(ToParameter(
                name,
                value,
                bracketParameterNames.Contains(name) || dollarBraceParameterNames.Contains(name)));
        }

        return new LegacyQueryBinding(commandText, parameters);
    }

    private static bool IsOptionalParameter(string name) =>
        string.Equals(name, "yhmc", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(name, "servertime", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(name, "czy", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(name, "hospitalName", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(name, "newTime", StringComparison.OrdinalIgnoreCase);

    private static Dictionary<string, JsonElement> BuildParameterIndex(ReportRenderRequest request)
    {
        var values = new Dictionary<string, JsonElement>(request.Parameters, StringComparer.OrdinalIgnoreCase);
        if (request.LegacyPayload is { ValueKind: JsonValueKind.Object } payload)
            IndexPayloadValues(payload, values);

        NormalizePluralSingularVariants(values);
        AddHospitalAliases(values);
        NormalizePluralSingularVariants(values);
        return values;
    }

    private static void NormalizePluralSingularVariants(Dictionary<string, JsonElement> values)
    {
        foreach (var (key, val) in values.ToArray())
        {
            if (key.EndsWith("Arr", StringComparison.OrdinalIgnoreCase) && key.Length > 3)
            {
                var singular = key[..^3];
                if (!values.ContainsKey(singular))
                    values[singular] = val.Clone();
            }
            else
            {
                var plural = key + "Arr";
                if (!values.ContainsKey(plural))
                    values[plural] = val.Clone();
            }
        }
    }

    private static void AddHospitalAliases(Dictionary<string, JsonElement> values)
    {
        // Aliases for registration ID: djid <-> grtjgcjjgid <-> tjdjid <-> id
        string[] idKeys = ["djid", "grtjgcjjgid", "tjdjid", "id"];
        JsonElement? foundIdVal = null;
        foreach (var key in idKeys)
        {
            if (values.TryGetValue(key, out var val) && val.ValueKind != JsonValueKind.Null && val.ValueKind != JsonValueKind.Undefined)
            {
                foundIdVal = val;
                break;
            }
        }
        if (foundIdVal.HasValue)
        {
            foreach (var key in idKeys)
            {
                if (!values.ContainsKey(key))
                    values[key] = foundIdVal.Value.Clone();
            }
        }

        // Aliases for physical examination number: tjh <-> tjbh
        if (values.TryGetValue("tjh", out var tjhVal) && !values.ContainsKey("tjbh"))
            values["tjbh"] = tjhVal.Clone();
        else if (values.TryGetValue("tjbh", out var tjbhVal) && !values.ContainsKey("tjh"))
            values["tjh"] = tjbhVal.Clone();
    }

    private static void IndexPayloadValues(JsonElement payload, Dictionary<string, JsonElement> values)
    {
        foreach (var property in payload.EnumerateObject())
        {
            if (property.Value.ValueKind is JsonValueKind.String or JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False or JsonValueKind.Null)
            {
                values[property.Name] = property.Value.Clone();
                if (property.Name.EndsWith("Arr", StringComparison.OrdinalIgnoreCase))
                {
                    var singular = property.Name[..^3];
                    if (!values.ContainsKey(singular))
                        values[singular] = property.Value.Clone();
                }
                else
                {
                    var plural = property.Name + "Arr";
                    if (!values.ContainsKey(plural))
                        values[plural] = property.Value.Clone();
                }
                continue;
            }

            if (property.Value.ValueKind == JsonValueKind.Array)
            {
                var length = property.Value.GetArrayLength();
                if (length > 0)
                {
                    var first = property.Value[0];
                    values[property.Name] = first.Clone();
                    if (property.Name.EndsWith("Arr", StringComparison.OrdinalIgnoreCase))
                    {
                        var singular = property.Name[..^3];
                        if (!values.ContainsKey(singular))
                            values[singular] = first.Clone();
                    }
                    else
                    {
                        var plural = property.Name + "Arr";
                        if (!values.ContainsKey(plural))
                            values[plural] = first.Clone();
                    }
                }
                continue;
            }

            if (property.Value.ValueKind == JsonValueKind.Object)
                IndexPayloadValues(property.Value, values);
        }
    }

    private static LegacyQueryParameter ToParameter(string name, JsonElement value, bool useAnsiString)
    {
        if (value.ValueKind == JsonValueKind.Undefined)
            return new LegacyQueryParameter(name, useAnsiString ? DbType.AnsiString : DbType.String, IsOptionalParameter(name) ? string.Empty : DBNull.Value);

        return value.ValueKind switch
        {
            JsonValueKind.String => new LegacyQueryParameter(name, useAnsiString ? DbType.AnsiString : DbType.String, value.GetString()),
            JsonValueKind.Number when value.TryGetInt64(out var integer) => new LegacyQueryParameter(name, DbType.Int64, integer),
            JsonValueKind.Number when value.TryGetDecimal(out var number) => new LegacyQueryParameter(name, DbType.Decimal, number),
            JsonValueKind.True => new LegacyQueryParameter(name, DbType.Boolean, true),
            JsonValueKind.False => new LegacyQueryParameter(name, DbType.Boolean, false),
            JsonValueKind.Null => new LegacyQueryParameter(name, DbType.Object, DBNull.Value),
            JsonValueKind.Array when value.GetArrayLength() > 0 => ToParameter(name, value[0], useAnsiString),
            _ => new LegacyQueryParameter(name, useAnsiString ? DbType.AnsiString : DbType.String, IsOptionalParameter(name) ? string.Empty : DBNull.Value)
        };
    }
}
