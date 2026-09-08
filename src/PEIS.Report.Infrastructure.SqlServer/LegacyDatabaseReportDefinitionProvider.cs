using System.Data;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using PEIS.Report.Contracts;
using PEIS.Report.Engine;

namespace PEIS.Report.Infrastructure.SqlServer;

public sealed class ReportDatabaseOptions
{
    public string Provider { get; set; } = "SqlServer";
    public string ConnectionString { get; set; } = string.Empty;
    public int CommandTimeoutSeconds { get; set; } = 30;
    public int DefinitionCacheTtlSeconds { get; set; } = 3600;
}

/// <summary>
/// Schema mapping is configuration, not an assertion about the production legacy schema. Its defaults are synthetic
/// placeholders derived only from table names supplied for development; every column relationship remains UNVERIFIED
/// until a read-only schema/sample is provided.
/// </summary>
public sealed class LegacyReportSchemaMapping
{
    public string DefinitionTable { get; set; } = "xt_bbdy";
    public string ReportIdColumn { get; set; } = "bbid";
    public string TemplateColumn { get; set; } = "bb_frx";
    public string SqlColumn { get; set; } = "bb_sql";
    public string? VersionColumn { get; set; }
    public string? UpdatedAtColumn { get; set; }
    /// <summary>Raw means template text is stored directly; Base64Utf8 means the database field stores Base64-encoded UTF-8 FRX XML.</summary>
    public string TemplateContentEncoding { get; set; } = "Raw";
    /// <summary>Optional evidence-backed DataTable name required by the FRX for the first SQL result set.</summary>
    public string? FirstResultSetTableName { get; set; }
    /// <summary>
    /// Optional column used to order definition rows whose ids start with the main report id plus an underscore.
    /// When configured, their SQL is loaded as Master1, Master2, and so on for legacy multi-table FRX templates.
    /// </summary>
    public string? SupplementalQueryOrderColumn { get; set; }
    /// <summary>Optional column used to match reports by their human-readable name or alias.</summary>
    public string? ReportNameColumn { get; set; } = "djmc";
    public string TemplateKeyPrefix { get; set; } = "legacy-db";

    public void Validate()
    {
        ValidateIdentifier(DefinitionTable, nameof(DefinitionTable));
        ValidateIdentifier(ReportIdColumn, nameof(ReportIdColumn));
        ValidateIdentifier(TemplateColumn, nameof(TemplateColumn));
        ValidateIdentifier(SqlColumn, nameof(SqlColumn));
        if (!string.IsNullOrWhiteSpace(VersionColumn)) ValidateIdentifier(VersionColumn, nameof(VersionColumn));
        if (!string.IsNullOrWhiteSpace(UpdatedAtColumn)) ValidateIdentifier(UpdatedAtColumn, nameof(UpdatedAtColumn));
        if (!string.IsNullOrWhiteSpace(ReportNameColumn)) ValidateIdentifier(ReportNameColumn, nameof(ReportNameColumn));
        if (!string.IsNullOrWhiteSpace(SupplementalQueryOrderColumn)) ValidateIdentifier(SupplementalQueryOrderColumn, nameof(SupplementalQueryOrderColumn));
        if (!string.Equals(TemplateContentEncoding, "Raw", StringComparison.OrdinalIgnoreCase) && !string.Equals(TemplateContentEncoding, "Base64Utf8", StringComparison.OrdinalIgnoreCase))
            throw new LegacyReportDatabaseException(LegacyReportDatabaseErrorCode.SchemaMappingUnverified, "Legacy schema mapping option 'TemplateContentEncoding' must be Raw or Base64Utf8.");
    }

    private static void ValidateIdentifier(string value, string optionName)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Any(character => !(char.IsLetterOrDigit(character) || character is '_' or '.')))
            throw new LegacyReportDatabaseException(
                LegacyReportDatabaseErrorCode.SchemaMappingUnverified,
                $"Legacy schema mapping option '{optionName}' must be a SQL identifier composed of letters, digits, underscore, or dot.");
    }
}

public sealed class LegacyDatabaseReportDefinitionProvider : IReportDefinitionProvider, IReportDefinitionVersionProvider
{
    private readonly ReportDatabaseOptions _database;
    private readonly LegacyReportSchemaMapping _schema;
    private readonly ILegacyReportResolver _resolver;
    private readonly TimeProvider _clock;

    public LegacyDatabaseReportDefinitionProvider(
        IOptions<ReportDatabaseOptions> database,
        IOptions<LegacyReportSchemaMapping> schema,
        TimeProvider? clock = null,
        ILegacyReportResolver? resolver = null)
    {
        _database = database.Value;
        _schema = schema.Value;
        _resolver = resolver ?? new LegacyPayloadReportResolver();
        _clock = clock ?? TimeProvider.System;
        _schema.Validate();
    }

    public async Task<ReportDefinitionVersion> GetVersionAsync(ReportRenderRequest request, CancellationToken cancellationToken)
    {
        EnsureConfigured();
        var resolution = _resolver.Resolve(request);
        var selected = VersionExpression();
        if (selected is null)
        {
            var seconds = Math.Clamp(_database.DefinitionCacheTtlSeconds, 1, 86400);
            var now = _clock.GetUtcNow();
            var bucket = now.ToUnixTimeSeconds() / seconds;
            return new ReportDefinitionVersion($"ttl:{bucket}", false, now.AddSeconds(seconds), "ttl-fallback-unverified-schema");
        }

        try
        {
            await using var connection = new SqlConnection(_database.ConnectionString);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandTimeout = TimeoutSeconds();
            var nameCondition = string.IsNullOrWhiteSpace(_schema.ReportNameColumn)
                ? string.Empty
                : $" OR {_schema.ReportNameColumn} = @reportId";
            command.CommandText = $"SELECT TOP 1 {selected} FROM {_schema.DefinitionTable} WHERE {_schema.ReportIdColumn} = @reportId{nameCondition} ORDER BY CASE WHEN {_schema.ReportIdColumn} = @reportId THEN 0 ELSE 1 END";
            command.Parameters.Add(new SqlParameter("@reportId", SqlDbType.NVarChar, 128) { Value = resolution.DefinitionId });
            var scalar = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            if (scalar is null || scalar is DBNull)
                throw new LegacyReportDatabaseException(LegacyReportDatabaseErrorCode.ReportNotFound, $"Legacy report definition '{resolution.DefinitionId}' was not found.");
            return new ReportDefinitionVersion(Convert.ToString(scalar, System.Globalization.CultureInfo.InvariantCulture)!, true, null, selected);
        }
        catch (LegacyReportDatabaseException)
        {
            throw;
        }
        catch (SqlException exception) when (exception.Number == -2)
        {
            throw new LegacyReportDatabaseException(LegacyReportDatabaseErrorCode.DatabaseTimeout, "Timed out checking the legacy report definition version.", exception);
        }
        catch (SqlException exception)
        {
            throw new LegacyReportDatabaseException(LegacyReportDatabaseErrorCode.DatabaseConnectionFailed, "Unable to check the legacy report definition version.", exception);
        }
    }

    public async Task<ReportDefinition> GetRequiredAsync(ReportRenderRequest request, CancellationToken cancellationToken)
    {
        EnsureConfigured();
        var resolution = _resolver.Resolve(request);
        try
        {
            await using var connection = new SqlConnection(_database.ConnectionString);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandTimeout = TimeoutSeconds();
            command.CommandText = BuildDefinitionQuery();
            command.Parameters.Add(new SqlParameter("@reportId", SqlDbType.NVarChar, 128) { Value = resolution.DefinitionId });
            await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SingleRow, cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                throw new LegacyReportDatabaseException(LegacyReportDatabaseErrorCode.ReportNotFound, $"Legacy report definition '{resolution.DefinitionId}' was not found.");

            var actualReportId = reader.GetString(0);
            var template = reader.GetString(1);
            if (string.IsNullOrWhiteSpace(template))
                throw new LegacyReportDatabaseException(LegacyReportDatabaseErrorCode.TemplateNotFound, $"Legacy report definition '{actualReportId}' has no FRX content.");
            var sql = reader.IsDBNull(2) ? null : reader.GetString(2);
            if (string.IsNullOrWhiteSpace(sql))
                throw new LegacyReportDatabaseException(LegacyReportDatabaseErrorCode.QueryDefinitionNotFound, $"Legacy report definition '{actualReportId}' has no SQL definition.");

            var storedVersion = reader.IsDBNull(3)
                ? null
                : Convert.ToString(reader.GetValue(3), System.Globalization.CultureInfo.InvariantCulture)!;
            var updatedAt = reader.IsDBNull(4) ? _clock.GetUtcNow() : new DateTimeOffset(DateTime.SpecifyKind(Convert.ToDateTime(reader.GetValue(4), System.Globalization.CultureInfo.InvariantCulture), DateTimeKind.Utc));
            await reader.DisposeAsync().ConfigureAwait(false);

            var supplementalQueries = await LoadSupplementalQueriesAsync(connection, actualReportId, cancellationToken).ConfigureAwait(false);
            var versionMaterial = string.Join("\n", supplementalQueries.Select(query => query.SqlText).Prepend(sql).Prepend(template));
            var version = storedVersion ?? Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(versionMaterial)));
            var metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["schemaMapping"] = _schema.DefinitionTable,
                ["identifierSource"] = resolution.IdentifierSource,
                ["templateContentEncoding"] = _schema.TemplateContentEncoding
            };
            if (!string.IsNullOrWhiteSpace(_schema.FirstResultSetTableName))
                metadata["resultSet:0:tableName"] = _schema.FirstResultSetTableName;
            return new ReportDefinition(
                actualReportId,
                version,
                $"{_schema.TemplateKeyPrefix}:{actualReportId}",
                sql,
                metadata,
                updatedAt,
                "legacy-sql-server",
                template,
                supplementalQueries);
        }
        catch (LegacyReportDatabaseException)
        {
            throw;
        }
        catch (SqlException exception) when (exception.Number == -2)
        {
            throw new LegacyReportDatabaseException(LegacyReportDatabaseErrorCode.DatabaseTimeout, "Timed out loading the legacy report definition.", exception);
        }
        catch (SqlException exception)
        {
            throw new LegacyReportDatabaseException(LegacyReportDatabaseErrorCode.DatabaseConnectionFailed, "Unable to load the legacy report definition.", exception);
        }
    }

    private string BuildDefinitionQuery()
    {
        var version = VersionExpression() ?? "NULL";
        var updated = string.IsNullOrWhiteSpace(_schema.UpdatedAtColumn) ? "NULL" : _schema.UpdatedAtColumn;
        var nameCondition = string.IsNullOrWhiteSpace(_schema.ReportNameColumn)
            ? string.Empty
            : $" OR {_schema.ReportNameColumn} = @reportId";
        return $"SELECT TOP 1 {_schema.ReportIdColumn}, {_schema.TemplateColumn}, {_schema.SqlColumn}, {version}, {updated} FROM {_schema.DefinitionTable} WHERE {_schema.ReportIdColumn} = @reportId{nameCondition} ORDER BY CASE WHEN {_schema.ReportIdColumn} = @reportId THEN 0 ELSE 1 END";
    }

    private async Task<IReadOnlyList<ReportDataQuery>> LoadSupplementalQueriesAsync(
        SqlConnection connection,
        string reportId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_schema.SupplementalQueryOrderColumn))
            return Array.Empty<ReportDataQuery>();

        await using var command = connection.CreateCommand();
        command.CommandTimeout = TimeoutSeconds();
        command.CommandText = $"""
            SELECT {_schema.ReportIdColumn}, {_schema.SqlColumn}
            FROM {_schema.DefinitionTable}
            WHERE {_schema.ReportIdColumn} LIKE @reportPrefix ESCAPE '\'
              AND {_schema.SqlColumn} IS NOT NULL
            ORDER BY {_schema.SupplementalQueryOrderColumn}, {_schema.ReportIdColumn}
            """;
        var escapedPrefix = reportId
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("%", "\\%", StringComparison.Ordinal)
            .Replace("_", "\\_", StringComparison.Ordinal) + "\\_%";
        command.Parameters.Add(new SqlParameter("@reportPrefix", SqlDbType.NVarChar, 260) { Value = escapedPrefix });

        var queries = new List<ReportDataQuery>();
        await using var reader = await command.ExecuteReaderAsync(CommandBehavior.Default, cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var subId = reader.IsDBNull(0) ? string.Empty : reader.GetString(0);
            if (reader.IsDBNull(1))
                continue;
            var supplementalSql = reader.GetString(1);
            if (string.IsNullOrWhiteSpace(supplementalSql))
                continue;
            queries.Add(new ReportDataQuery(subId, supplementalSql) { SubReportId = subId });
        }
        return queries;
    }

    private string? VersionExpression() => !string.IsNullOrWhiteSpace(_schema.VersionColumn)
        ? _schema.VersionColumn
        : !string.IsNullOrWhiteSpace(_schema.UpdatedAtColumn) ? _schema.UpdatedAtColumn : null;

    private int TimeoutSeconds() => Math.Clamp(_database.CommandTimeoutSeconds, 1, 300);

    private void EnsureConfigured()
    {
        if (!string.Equals(_database.Provider, "SqlServer", StringComparison.OrdinalIgnoreCase))
            throw new LegacyReportDatabaseException(LegacyReportDatabaseErrorCode.DatabaseConnectionFailed, $"ReportDatabase provider '{_database.Provider}' is not supported by this SQL Server implementation.");
        if (string.IsNullOrWhiteSpace(_database.ConnectionString))
            throw new LegacyReportDatabaseException(LegacyReportDatabaseErrorCode.DatabaseConnectionFailed, "ReportDatabase connection string is not configured.");
    }
}

public sealed class LegacyDatabaseTemplateProvider : ITemplateProvider
{
    public Task<ReportTemplate> GetRequiredAsync(ReportDefinition definition, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(definition.TemplateContent))
            throw new LegacyReportDatabaseException(LegacyReportDatabaseErrorCode.TemplateNotFound, $"Report '{definition.ReportId}' did not include database FRX content.");

        var content = DecodeTemplateContent(definition.TemplateContent, definition.ReportId);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content)));
        return Task.FromResult(new ReportTemplate(definition.TemplateKey, definition.Version, content, hash));
    }

    public static string DecodeTemplateContent(string rawContent, string reportId)
    {
        if (string.IsNullOrWhiteSpace(rawContent))
            return string.Empty;

        var trimmed = rawContent.Trim().TrimStart('\uFEFF', '\u0000', '\u200B');

        // 1. If it already starts with XML declaration or <Report, it's raw XML
        if (trimmed.StartsWith("<?xml", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("<Report", StringComparison.OrdinalIgnoreCase))
        {
            return trimmed;
        }

        // 2. Try Base64 decoding
        try
        {
            var cleanBase64 = trimmed.Replace("\r", "").Replace("\n", "").Replace(" ", "");
            var bytes = Convert.FromBase64String(cleanBase64);
            var decoded = Encoding.UTF8.GetString(bytes).Trim().TrimStart('\uFEFF', '\u0000', '\u200B');
            if (decoded.StartsWith("<?xml", StringComparison.OrdinalIgnoreCase) ||
                decoded.StartsWith("<Report", StringComparison.OrdinalIgnoreCase) ||
                decoded.Contains("<Report", StringComparison.OrdinalIgnoreCase))
            {
                return decoded;
            }
        }
        catch (FormatException)
        {
            // Not Base64, fall through
        }

        // 3. If rawContent contains <Report anywhere inside, extract from <Report
        var reportIdx = trimmed.IndexOf("<Report", StringComparison.OrdinalIgnoreCase);
        if (reportIdx >= 0)
            return trimmed.Substring(reportIdx);

        var xmlIdx = trimmed.IndexOf("<?xml", StringComparison.OrdinalIgnoreCase);
        if (xmlIdx >= 0)
            return trimmed.Substring(xmlIdx);

        return trimmed;
    }
}
