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
    public int DefinitionCacheTtlSeconds { get; set; } = 300;
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
    public string TemplateKeyPrefix { get; set; } = "legacy-db";

    public void Validate()
    {
        ValidateIdentifier(DefinitionTable, nameof(DefinitionTable));
        ValidateIdentifier(ReportIdColumn, nameof(ReportIdColumn));
        ValidateIdentifier(TemplateColumn, nameof(TemplateColumn));
        ValidateIdentifier(SqlColumn, nameof(SqlColumn));
        if (!string.IsNullOrWhiteSpace(VersionColumn)) ValidateIdentifier(VersionColumn, nameof(VersionColumn));
        if (!string.IsNullOrWhiteSpace(UpdatedAtColumn)) ValidateIdentifier(UpdatedAtColumn, nameof(UpdatedAtColumn));
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
    private readonly TimeProvider _clock;

    public LegacyDatabaseReportDefinitionProvider(
        IOptions<ReportDatabaseOptions> database,
        IOptions<LegacyReportSchemaMapping> schema,
        TimeProvider? clock = null)
    {
        _database = database.Value;
        _schema = schema.Value;
        _clock = clock ?? TimeProvider.System;
        _schema.Validate();
    }

    public async Task<ReportDefinitionVersion> GetVersionAsync(ReportRenderRequest request, CancellationToken cancellationToken)
    {
        EnsureConfigured();
        var selected = VersionExpression();
        if (selected is null)
        {
            var seconds = Math.Clamp(_database.DefinitionCacheTtlSeconds, 1, 3600);
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
            command.CommandText = $"SELECT {selected} FROM {_schema.DefinitionTable} WHERE {_schema.ReportIdColumn} = @reportId";
            command.Parameters.Add(new SqlParameter("@reportId", SqlDbType.NVarChar, 128) { Value = request.ReportId });
            var scalar = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            if (scalar is null || scalar is DBNull)
                throw new LegacyReportDatabaseException(LegacyReportDatabaseErrorCode.ReportNotFound, $"Legacy report definition '{request.ReportId}' was not found.");
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
        try
        {
            await using var connection = new SqlConnection(_database.ConnectionString);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandTimeout = TimeoutSeconds();
            command.CommandText = BuildDefinitionQuery();
            command.Parameters.Add(new SqlParameter("@reportId", SqlDbType.NVarChar, 128) { Value = request.ReportId });
            await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SingleRow, cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                throw new LegacyReportDatabaseException(LegacyReportDatabaseErrorCode.ReportNotFound, $"Legacy report definition '{request.ReportId}' was not found.");

            var template = reader.GetString(1);
            if (string.IsNullOrWhiteSpace(template))
                throw new LegacyReportDatabaseException(LegacyReportDatabaseErrorCode.TemplateNotFound, $"Legacy report definition '{request.ReportId}' has no FRX content.");
            var sql = reader.IsDBNull(2) ? null : reader.GetString(2);
            if (string.IsNullOrWhiteSpace(sql))
                throw new LegacyReportDatabaseException(LegacyReportDatabaseErrorCode.QueryDefinitionNotFound, $"Legacy report definition '{request.ReportId}' has no SQL definition.");

            var version = reader.IsDBNull(3)
                ? Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(template + "\n" + sql)))
                : Convert.ToString(reader.GetValue(3), System.Globalization.CultureInfo.InvariantCulture)!;
            var updatedAt = reader.IsDBNull(4) ? _clock.GetUtcNow() : Convert.ToDateTime(reader.GetValue(4), System.Globalization.CultureInfo.InvariantCulture);
            var metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["schemaMapping"] = _schema.DefinitionTable,
                ["identifierSource"] = request.LegacyPayload is null ? "typed-request" : "legacy-payload-preserved"
            };
            return new ReportDefinition(
                request.ReportId,
                version,
                $"{_schema.TemplateKeyPrefix}:{request.ReportId}",
                sql,
                metadata,
                new DateTimeOffset(DateTime.SpecifyKind(updatedAt, DateTimeKind.Utc)),
                "legacy-sql-server",
                template);
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
        return $"SELECT {_schema.ReportIdColumn}, {_schema.TemplateColumn}, {_schema.SqlColumn}, {version}, {updated} FROM {_schema.DefinitionTable} WHERE {_schema.ReportIdColumn} = @reportId";
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
        if (string.IsNullOrEmpty(definition.TemplateContent))
            throw new LegacyReportDatabaseException(LegacyReportDatabaseErrorCode.TemplateNotFound, $"Report '{definition.ReportId}' did not include database FRX content.");

        var content = definition.TemplateContent;
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content)));
        return Task.FromResult(new ReportTemplate(definition.TemplateKey, definition.Version, content, hash));
    }
}
