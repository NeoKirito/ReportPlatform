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
/// Conservative baseline binder. It supports existing ADO.NET-style <c>@name</c> parameters without text
/// substitution. Unknown historical placeholder formats remain intentionally unimplemented until a legacy fixture
/// confirms their semantics.
/// </summary>
public sealed class AdoNetLegacyQueryParameterBinder : ILegacyQueryParameterBinder
{
    private static readonly System.Text.RegularExpressions.Regex ParameterPattern = new(
        "(?<!@)@(?<name>[A-Za-z_][A-Za-z0-9_]*)",
        System.Text.RegularExpressions.RegexOptions.Compiled | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    public LegacyQueryBinding Bind(ReportDefinition definition, ReportRenderRequest request)
    {
        if (string.IsNullOrWhiteSpace(definition.SqlText))
            throw new LegacyReportDatabaseException(
                LegacyReportDatabaseErrorCode.QueryDefinitionNotFound,
                $"Report '{definition.ReportId}' has no SQL definition.");

        var source = BuildParameterIndex(request);
        var parameters = new List<LegacyQueryParameter>();
        foreach (System.Text.RegularExpressions.Match match in ParameterPattern.Matches(definition.SqlText))
        {
            var name = match.Groups["name"].Value;
            if (parameters.Any(parameter => string.Equals(parameter.Name, name, StringComparison.OrdinalIgnoreCase)))
                continue;
            if (!source.TryGetValue(name, out var value))
                throw new LegacyReportDatabaseException(
                    LegacyReportDatabaseErrorCode.ParameterBindFailed,
                    $"Report '{definition.ReportId}' SQL requires parameter '@{name}', but legacy payload does not contain it.");

            parameters.Add(ToParameter(name, value));
        }

        return new LegacyQueryBinding(definition.SqlText, parameters);
    }

    private static Dictionary<string, JsonElement> BuildParameterIndex(ReportRenderRequest request)
    {
        var values = new Dictionary<string, JsonElement>(request.Parameters, StringComparer.OrdinalIgnoreCase);
        if (request.LegacyPayload is { ValueKind: JsonValueKind.Object } payload)
        {
            foreach (var property in payload.EnumerateObject())
                values[property.Name] = property.Value.Clone();
        }
        return values;
    }

    private static LegacyQueryParameter ToParameter(string name, JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => new LegacyQueryParameter(name, DbType.String, value.GetString()),
        JsonValueKind.Number when value.TryGetInt64(out var integer) => new LegacyQueryParameter(name, DbType.Int64, integer),
        JsonValueKind.Number when value.TryGetDecimal(out var number) => new LegacyQueryParameter(name, DbType.Decimal, number),
        JsonValueKind.True => new LegacyQueryParameter(name, DbType.Boolean, true),
        JsonValueKind.False => new LegacyQueryParameter(name, DbType.Boolean, false),
        JsonValueKind.Null => new LegacyQueryParameter(name, DbType.Object, DBNull.Value),
        _ => throw new LegacyReportDatabaseException(
            LegacyReportDatabaseErrorCode.ParameterBindFailed,
            $"Legacy parameter '{name}' has unsupported JSON kind '{value.ValueKind}'.")
    };
}
