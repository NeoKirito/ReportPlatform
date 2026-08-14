using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.SqlClient;

return await LegacyDbInspectorProgram.RunAsync(args);

internal static class LegacyDbInspectorProgram
{
    private static readonly string[] TargetTables = ["xt_bbdy", "xt_djwh", "xt_cxdy", "xt_bgdy_djwh_zzj"];

    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length == 0 || args.Any(argument => argument is "--help" or "-h"))
        {
            PrintUsage();
            return 0;
        }

        try
        {
            var options = InspectorOptions.Parse(args);
            Directory.CreateDirectory(options.OutputDirectory);

            await using var connection = new SqlConnection(options.ConnectionString);
            await connection.OpenAsync();

            var schema = await InspectSchemaAsync(connection);
            await WriteJsonAsync(Path.Combine(options.OutputDirectory, "schema.json"), schema);
            await WriteJsonAsync(Path.Combine(options.OutputDirectory, "table-summary.json"), schema.Tables.Select(table => new TableSummary(
                table.Schema, table.Name, table.RowCount, table.Columns.Count, table.PrimaryKeyColumns, table.Indexes.Count, table.HasRowVersion)).ToArray());
            await File.WriteAllTextAsync(Path.Combine(options.OutputDirectory, "schema.md"), RenderSchemaMarkdown(schema), new UTF8Encoding(false));

            if (options.Command == InspectorCommand.InspectReport)
            {
                var report = await InspectReportAsync(connection, options);
                await WriteJsonAsync(Path.Combine(options.OutputDirectory, "report-sample.json"), report);
                await File.WriteAllTextAsync(Path.Combine(options.OutputDirectory, "report-sample.md"), RenderReportMarkdown(report), new UTF8Encoding(false));
                await ExportRequestedArtifactsAsync(report, options);
            }

            Console.WriteLine($"Read-only inspection complete. Output: {Path.GetFullPath(options.OutputDirectory)}");
            return 0;
        }
        catch (InspectorUsageException exception)
        {
            Console.Error.WriteLine($"Input error: {exception.Message}");
            Console.Error.WriteLine("Use --help for supported read-only commands.");
            return 2;
        }
        catch (SqlException exception)
        {
            Console.Error.WriteLine($"SQL Server inspection failed ({exception.Number}): {exception.Message}");
            return 3;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"Inspection failed: {exception.Message}");
            return 4;
        }
    }

    private static async Task<SchemaInspection> InspectSchemaAsync(SqlConnection connection)
    {
        const string schemaSql = """
            SELECT
                s.name AS SchemaName,
                t.name AS TableName,
                c.column_id AS Ordinal,
                c.name AS ColumnName,
                ty.name AS TypeName,
                c.max_length AS MaxLength,
                c.precision AS NumericPrecision,
                c.scale AS NumericScale,
                c.is_nullable AS IsNullable,
                c.is_identity AS IsIdentity,
                c.is_computed AS IsComputed,
                CASE WHEN ty.name IN ('timestamp', 'rowversion') THEN CAST(1 AS bit) ELSE CAST(0 AS bit) END AS IsRowVersion
            FROM sys.tables AS t
            INNER JOIN sys.schemas AS s ON s.schema_id = t.schema_id
            INNER JOIN sys.columns AS c ON c.object_id = t.object_id
            INNER JOIN sys.types AS ty ON ty.user_type_id = c.user_type_id
            WHERE t.name IN ('xt_bbdy', 'xt_djwh', 'xt_cxdy', 'xt_bgdy_djwh_zzj')
            ORDER BY s.name, t.name, c.column_id;
            """;

        const string indexSql = """
            SELECT
                s.name AS SchemaName,
                t.name AS TableName,
                i.name AS IndexName,
                i.is_primary_key AS IsPrimaryKey,
                i.is_unique AS IsUnique,
                ic.key_ordinal AS KeyOrdinal,
                c.name AS ColumnName,
                i.type_desc AS IndexType
            FROM sys.tables AS t
            INNER JOIN sys.schemas AS s ON s.schema_id = t.schema_id
            INNER JOIN sys.indexes AS i ON i.object_id = t.object_id
            INNER JOIN sys.index_columns AS ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id
            INNER JOIN sys.columns AS c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
            WHERE t.name IN ('xt_bbdy', 'xt_djwh', 'xt_cxdy', 'xt_bgdy_djwh_zzj')
              AND i.index_id > 0
              AND ic.is_included_column = 0
            ORDER BY s.name, t.name, i.name, ic.key_ordinal;
            """;

        const string countSql = """
            SELECT s.name AS SchemaName, t.name AS TableName, SUM(ps.row_count) AS [RowCount]
            FROM sys.tables AS t
            INNER JOIN sys.schemas AS s ON s.schema_id = t.schema_id
            INNER JOIN sys.dm_db_partition_stats AS ps ON ps.object_id = t.object_id
            WHERE t.name IN ('xt_bbdy', 'xt_djwh', 'xt_cxdy', 'xt_bgdy_djwh_zzj')
              AND ps.index_id IN (0, 1)
            GROUP BY s.name, t.name;
            """;

        var tables = TargetTables.ToDictionary(name => name, name => new MutableTableInspection(name), StringComparer.OrdinalIgnoreCase);
        string? databaseName;
        await using (var databaseCommand = new SqlCommand("SELECT DB_NAME();", connection))
        {
            databaseName = Convert.ToString(await databaseCommand.ExecuteScalarAsync());
        }

        await using (var command = new SqlCommand(schemaSql, connection))
        await using (var reader = await command.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                var table = GetOrCreate(tables, reader.GetString(1));
                table.Schema = reader.GetString(0);
                table.Columns.Add(new ColumnInspection(
                    reader.GetInt32(2), reader.GetString(3), reader.GetString(4), reader.GetInt16(5), reader.GetByte(6), reader.GetByte(7),
                    reader.GetBoolean(8), reader.GetBoolean(9), reader.GetBoolean(10), reader.GetBoolean(11)));
            }
        }

        await using (var command = new SqlCommand(indexSql, connection))
        await using (var reader = await command.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                var table = GetOrCreate(tables, reader.GetString(1));
                table.Schema = reader.GetString(0);
                table.IndexEntries.Add(new IndexColumnEntry(
                    reader.GetString(2), reader.GetBoolean(3), reader.GetBoolean(4), Convert.ToInt32(reader.GetValue(5), System.Globalization.CultureInfo.InvariantCulture), reader.GetString(6), reader.GetString(7)));
            }
        }

        await using (var command = new SqlCommand(countSql, connection))
        await using (var reader = await command.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                var table = GetOrCreate(tables, reader.GetString(1));
                table.Schema = reader.GetString(0);
                table.RowCount = reader.IsDBNull(2) ? 0 : reader.GetInt64(2);
            }
        }

        return new SchemaInspection(
            DateTimeOffset.UtcNow,
            databaseName ?? "(unknown)",
            tables.Values
                .OrderBy(table => table.Name, StringComparer.OrdinalIgnoreCase)
                .Select(table => table.ToImmutable())
                .ToArray());
    }

    private static async Task<ReportSample> InspectReportAsync(SqlConnection connection, InspectorOptions options)
    {
        var target = options.IdType switch
        {
            ReportIdentifierType.Bbid => new ReportTarget("xt_bbdy", "bbid"),
            ReportIdentifierType.Djid => new ReportTarget("xt_djwh", "djid"),
            ReportIdentifierType.Cxid => new ReportTarget("xt_cxdy", "cxid"),
            _ => throw new InspectorUsageException("inspect-report requires --id-type bbid|djid|cxid.")
        };

        var sql = $"SELECT TOP (1) * FROM {QuoteIdentifier(target.TableName)} WHERE {QuoteIdentifier(target.IdentifierColumn)} = @id;";
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add(new SqlParameter("@id", SqlDbType.NVarChar, 256) { Value = options.ReportId! });
        await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SingleRow | CommandBehavior.SequentialAccess);

        if (!await reader.ReadAsync())
        {
            return ReportSample.NotFound(options.IdType!.Value, target.TableName, HashText(options.ReportId!));
        }

        var fields = new List<FieldFingerprint>();
        var templateCandidates = new List<ArtifactCandidate>();
        var sqlCandidates = new List<ArtifactCandidate>();
        string? version = null;
        DateTimeOffset? updatedAtUtc = null;

        for (var ordinal = 0; ordinal < reader.FieldCount; ordinal++)
        {
            var name = reader.GetName(ordinal);
            var typeName = reader.GetDataTypeName(ordinal);
            var isNull = await reader.IsDBNullAsync(ordinal);
            var raw = isNull ? null : reader.GetValue(ordinal);
            var fingerprint = Fingerprint(raw);
            fields.Add(new FieldFingerprint(name, typeName, isNull, fingerprint.Length, fingerprint.Sha256));

            if (!isNull && IsTemplateColumn(name, typeName))
            {
                templateCandidates.Add(new ArtifactCandidate(name, ToText(raw), fingerprint));
            }
            else if (!isNull && IsSqlColumn(name, typeName))
            {
                sqlCandidates.Add(new ArtifactCandidate(name, ToText(raw), fingerprint));
            }

            if (!isNull && version is null && IsVersionColumn(name, typeName))
            {
                version = raw switch
                {
                    byte[] bytes => Convert.ToHexString(bytes),
                    _ => Convert.ToString(raw, System.Globalization.CultureInfo.InvariantCulture)
                };
            }

            if (!isNull && updatedAtUtc is null && IsUpdatedAtColumn(name, raw))
            {
                updatedAtUtc = raw switch
                {
                    DateTimeOffset offset => offset.ToUniversalTime(),
                    DateTime dateTime => new DateTimeOffset(DateTime.SpecifyKind(dateTime, DateTimeKind.Utc)),
                    _ => null
                };
            }
        }

        var template = templateCandidates.FirstOrDefault();
        var reportSql = sqlCandidates.FirstOrDefault();
        return new ReportSample(
            Found: true,
            IdType: options.IdType!.Value,
            SourceTable: target.TableName,
            RequestedIdSha256: HashText(options.ReportId!),
            Version: version,
            UpdatedAtUtc: updatedAtUtc,
            Template: template is null ? null : new ArtifactSummary(template.ColumnName, template.Fingerprint.Length, template.Fingerprint.Sha256),
            Sql: reportSql is null ? null : new ArtifactSummary(reportSql.ColumnName, reportSql.Fingerprint.Length, reportSql.Fingerprint.Sha256),
            Fields: fields,
            TemplateContent: template?.Content,
            SqlContent: reportSql?.Content);
    }

    private static async Task ExportRequestedArtifactsAsync(ReportSample report, InspectorOptions options)
    {
        if (!report.Found)
        {
            throw new InspectorUsageException("No record was found for the requested report identifier; exports were not created.");
        }

        if (options.ExportTemplate)
        {
            if (report.TemplateContent is null)
            {
                throw new InspectorUsageException("No FRX/template-like text column was found in the sampled row.");
            }

            await File.WriteAllTextAsync(Path.Combine(options.OutputDirectory, "report-template.frx"), report.TemplateContent, new UTF8Encoding(false));
        }

        if (options.ExportSql)
        {
            if (report.SqlContent is null)
            {
                throw new InspectorUsageException("No SQL-like text column was found in the sampled row.");
            }

            await File.WriteAllTextAsync(Path.Combine(options.OutputDirectory, "report-query.sql"), report.SqlContent, new UTF8Encoding(false));
        }
    }

    private static MutableTableInspection GetOrCreate(IDictionary<string, MutableTableInspection> tables, string name)
    {
        if (!tables.TryGetValue(name, out var table))
        {
            table = new MutableTableInspection(name);
            tables.Add(name, table);
        }
        return table;
    }

    private static string RenderSchemaMarkdown(SchemaInspection schema)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# Legacy SQL Server Schema Inspection");
        builder.AppendLine();
        builder.AppendLine($"> Read-only evidence collected at `{schema.CollectedAtUtc:O}` from database `{schema.DatabaseName}`. No row values, connection strings, or credentials are included.");
        builder.AppendLine();
        builder.AppendLine("| Table | Rows | Columns | Primary key columns | Indexes | Rowversion |");
        builder.AppendLine("|---|---:|---:|---|---:|---|");
        foreach (var table in schema.Tables)
        {
            builder.AppendLine($"| `{table.Schema}.{table.Name}` | {table.RowCount} | {table.Columns.Count} | {EscapeTable(string.Join(", ", table.PrimaryKeyColumns))} | {table.Indexes.Count} | {(table.HasRowVersion ? "Yes" : "No")} |");
        }

        foreach (var table in schema.Tables)
        {
            builder.AppendLine();
            builder.AppendLine($"## `{table.Schema}.{table.Name}`");
            builder.AppendLine();
            builder.AppendLine("| # | Column | SQL type | Nullable | Identity | Computed | Rowversion |");
            builder.AppendLine("|---:|---|---|---|---|---|---|");
            foreach (var column in table.Columns)
            {
                var displayType = FormatSqlType(column);
                builder.AppendLine($"| {column.Ordinal} | `{column.Name}` | `{displayType}` | {(column.IsNullable ? "Yes" : "No")} | {(column.IsIdentity ? "Yes" : "No")} | {(column.IsComputed ? "Yes" : "No")} | {(column.IsRowVersion ? "Yes" : "No")} |");
            }
        }

        return builder.ToString();
    }

    private static string RenderReportMarkdown(ReportSample report)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# Legacy Report Sample Evidence");
        builder.AppendLine();
        builder.AppendLine($"> The sampled identifier is represented only by SHA-256. Raw field values are intentionally omitted.");
        builder.AppendLine();
        builder.AppendLine("| Property | Value |");
        builder.AppendLine("|---|---|");
        builder.AppendLine($"| Found | {report.Found} |");
        builder.AppendLine($"| Identifier type | `{report.IdType}` |");
        builder.AppendLine($"| Source table | `{report.SourceTable}` |");
        builder.AppendLine($"| Requested ID SHA-256 | `{report.RequestedIdSha256}` |");
        builder.AppendLine($"| Version | `{report.Version ?? "NOT AVAILABLE"}` |");
        builder.AppendLine($"| Updated at UTC | `{report.UpdatedAtUtc?.ToString("O") ?? "NOT AVAILABLE"}` |");
        builder.AppendLine($"| Template | {RenderArtifact(report.Template)} |");
        builder.AppendLine($"| SQL | {RenderArtifact(report.Sql)} |");
        builder.AppendLine();
        builder.AppendLine("| Column | SQL type | NULL | Length | SHA-256 |");
        builder.AppendLine("|---|---|---|---:|---|");
        foreach (var field in report.Fields)
        {
            builder.AppendLine($"| `{field.Name}` | `{field.SqlType}` | {(field.IsNull ? "Yes" : "No")} | {field.Length?.ToString() ?? ""} | `{field.Sha256 ?? ""}` |");
        }
        return builder.ToString();
    }

    private static string RenderArtifact(ArtifactSummary? artifact) => artifact is null
        ? "NOT FOUND"
        : $"`{artifact.ColumnName}`; length `{artifact.Length}`; SHA-256 `{artifact.Sha256}`";

    private static string FormatSqlType(ColumnInspection column)
    {
        if (column.TypeName is "nvarchar" or "nchar") return $"{column.TypeName}({(column.MaxLength == -1 ? "max" : (column.MaxLength / 2).ToString())})";
        if (column.TypeName is "varchar" or "char" or "varbinary" or "binary") return $"{column.TypeName}({(column.MaxLength == -1 ? "max" : column.MaxLength.ToString())})";
        if (column.TypeName is "decimal" or "numeric") return $"{column.TypeName}({column.NumericPrecision},{column.NumericScale})";
        return column.TypeName;
    }

    private static string EscapeTable(string text) => string.IsNullOrWhiteSpace(text) ? "—" : text.Replace("|", "\\|");
    private static bool IsTemplateColumn(string name, string typeName) => IsTextLike(typeName) && (name.Contains("frx", StringComparison.OrdinalIgnoreCase) || name.Contains("template", StringComparison.OrdinalIgnoreCase));
    private static bool IsSqlColumn(string name, string typeName) => IsTextLike(typeName) && (name.Equals("sql", StringComparison.OrdinalIgnoreCase) || name.Contains("_sql", StringComparison.OrdinalIgnoreCase) || name.Contains("sqltext", StringComparison.OrdinalIgnoreCase) || name.Contains("query", StringComparison.OrdinalIgnoreCase));
    private static bool IsVersionColumn(string name, string typeName) => typeName is "timestamp" or "rowversion" || name.Contains("version", StringComparison.OrdinalIgnoreCase) || name.Contains("rowver", StringComparison.OrdinalIgnoreCase);
    private static bool IsUpdatedAtColumn(string name, object? raw) => raw is DateTime or DateTimeOffset && (name.Contains("update", StringComparison.OrdinalIgnoreCase) || name.Contains("modify", StringComparison.OrdinalIgnoreCase) || name.Contains("time", StringComparison.OrdinalIgnoreCase) || name.Contains("date", StringComparison.OrdinalIgnoreCase));
    private static bool IsTextLike(string typeName) => typeName is "nvarchar" or "varchar" or "nchar" or "char" or "ntext" or "text" or "xml";
    private static string ToText(object? raw) => raw switch { null => string.Empty, string text => text, char[] chars => new string(chars), byte[] bytes => Encoding.UTF8.GetString(bytes), _ => Convert.ToString(raw, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty };
    private static ArtifactFingerprint Fingerprint(object? raw) => raw is null ? new(null, null) : raw switch { byte[] bytes => new(bytes.LongLength, Convert.ToHexString(SHA256.HashData(bytes))), string text => new(Encoding.UTF8.GetByteCount(text), HashText(text)), char[] chars => new(Encoding.UTF8.GetByteCount(chars), HashText(new string(chars))), _ => Fingerprint(Convert.ToString(raw, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty) };
    private static string HashText(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    private static string QuoteIdentifier(string identifier) => $"[{identifier.Replace("]", "]]", StringComparison.Ordinal)}]";

    private static async Task WriteJsonAsync<T>(string path, T value)
    {
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase }), new UTF8Encoding(false));
    }

    private static void PrintUsage()
    {
        Console.WriteLine("""
            PEIS.LegacyDbInspector — read-only legacy SQL Server evidence collector

            inspect-schema --connection <connection-string> [--output <directory>]
            inspect-report --connection <connection-string> --id <id> --id-type bbid|djid|cxid [--output <directory>] [--export-template] [--export-sql]

            The tool only runs SELECT statements against sys.* metadata and the requested single record.
            The connection string must target an explicitly approved read-only account. It is never written to output.
            """);
    }
}

internal sealed class InspectorOptions
{
    public required InspectorCommand Command { get; init; }
    public required string ConnectionString { get; init; }
    public required string OutputDirectory { get; init; }
    public string? ReportId { get; init; }
    public ReportIdentifierType? IdType { get; init; }
    public bool ExportTemplate { get; init; }
    public bool ExportSql { get; init; }

    public static InspectorOptions Parse(string[] args)
    {
        var positionals = new List<string>();
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var flags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (var index = 0; index < args.Length; index++)
        {
            var argument = args[index];
            if (!argument.StartsWith("--", StringComparison.Ordinal))
            {
                positionals.Add(argument);
                continue;
            }

            if (argument is "--export-template" or "--export-sql")
            {
                flags.Add(argument);
                continue;
            }

            if (index + 1 >= args.Length || args[index + 1].StartsWith("--", StringComparison.Ordinal))
            {
                throw new InspectorUsageException($"Option '{argument}' requires a value.");
            }
            values[argument] = args[++index];
        }

        var command = positionals.Count switch
        {
            0 => InspectorCommand.InspectSchema,
            1 when string.Equals(positionals[0], "inspect-schema", StringComparison.OrdinalIgnoreCase) => InspectorCommand.InspectSchema,
            1 when string.Equals(positionals[0], "inspect-report", StringComparison.OrdinalIgnoreCase) => InspectorCommand.InspectReport,
            _ => throw new InspectorUsageException("Use inspect-schema or inspect-report as the only command.")
        };

        if (!values.TryGetValue("--connection", out var connection) || string.IsNullOrWhiteSpace(connection))
        {
            throw new InspectorUsageException("--connection is required; do not place credentials in source control or command history.");
        }

        values.TryGetValue("--output", out var output);
        values.TryGetValue("--id", out var reportId);
        values.TryGetValue("--id-type", out var idTypeText);
        ReportIdentifierType? idType = idTypeText?.ToLowerInvariant() switch
        {
            null => null,
            "bbid" => ReportIdentifierType.Bbid,
            "djid" => ReportIdentifierType.Djid,
            "cxid" => ReportIdentifierType.Cxid,
            _ => throw new InspectorUsageException("--id-type must be bbid, djid, or cxid.")
        };

        if (command == InspectorCommand.InspectReport && (string.IsNullOrWhiteSpace(reportId) || idType is null))
        {
            throw new InspectorUsageException("inspect-report requires both --id and --id-type.");
        }

        return new InspectorOptions
        {
            Command = command,
            ConnectionString = connection,
            OutputDirectory = Path.GetFullPath(string.IsNullOrWhiteSpace(output) ? Path.Combine("artifacts", "legacy-inspection") : output),
            ReportId = reportId,
            IdType = idType,
            ExportTemplate = flags.Contains("--export-template"),
            ExportSql = flags.Contains("--export-sql")
        };
    }
}

internal enum InspectorCommand { InspectSchema, InspectReport }
internal enum ReportIdentifierType { Bbid, Djid, Cxid }
internal sealed class InspectorUsageException(string message) : Exception(message);
internal sealed record ReportTarget(string TableName, string IdentifierColumn);
internal sealed record ArtifactFingerprint(long? Length, string? Sha256);
internal sealed record ArtifactCandidate(string ColumnName, string Content, ArtifactFingerprint Fingerprint);
internal sealed record ColumnInspection(int Ordinal, string Name, string TypeName, short MaxLength, byte NumericPrecision, byte NumericScale, bool IsNullable, bool IsIdentity, bool IsComputed, bool IsRowVersion);
internal sealed record IndexColumnEntry(string IndexName, bool IsPrimaryKey, bool IsUnique, int KeyOrdinal, string ColumnName, string IndexType);
internal sealed record IndexInspection(string Name, bool IsPrimaryKey, bool IsUnique, string IndexType, IReadOnlyList<string> KeyColumns);
internal sealed record TableInspection(string Schema, string Name, long RowCount, IReadOnlyList<ColumnInspection> Columns, IReadOnlyList<IndexInspection> Indexes, IReadOnlyList<string> PrimaryKeyColumns, bool HasRowVersion);
internal sealed record SchemaInspection(DateTimeOffset CollectedAtUtc, string DatabaseName, IReadOnlyList<TableInspection> Tables);
internal sealed record TableSummary(string Schema, string Name, long RowCount, int ColumnCount, IReadOnlyList<string> PrimaryKeyColumns, int IndexCount, bool HasRowVersion);
internal sealed record FieldFingerprint(string Name, string SqlType, bool IsNull, long? Length, string? Sha256);
internal sealed record ArtifactSummary(string ColumnName, long? Length, string? Sha256);
internal sealed record ReportSample(bool Found, ReportIdentifierType IdType, string SourceTable, string RequestedIdSha256, string? Version, DateTimeOffset? UpdatedAtUtc, ArtifactSummary? Template, ArtifactSummary? Sql, IReadOnlyList<FieldFingerprint> Fields, [property: JsonIgnore] string? TemplateContent, [property: JsonIgnore] string? SqlContent)
{
    public static ReportSample NotFound(ReportIdentifierType idType, string tableName, string idHash) => new(false, idType, tableName, idHash, null, null, null, null, Array.Empty<FieldFingerprint>(), null, null);
}

internal sealed class MutableTableInspection
{
    public MutableTableInspection(string name) => Name = name;
    public string Schema { get; set; } = "(not found)";
    public string Name { get; }
    public long RowCount { get; set; }
    public List<ColumnInspection> Columns { get; } = [];
    public List<IndexColumnEntry> IndexEntries { get; } = [];

    public TableInspection ToImmutable()
    {
        var indexes = IndexEntries
            .GroupBy(entry => new { entry.IndexName, entry.IsPrimaryKey, entry.IsUnique, entry.IndexType })
            .Select(group => new IndexInspection(group.Key.IndexName, group.Key.IsPrimaryKey, group.Key.IsUnique, group.Key.IndexType, group.OrderBy(item => item.KeyOrdinal).Select(item => item.ColumnName).ToArray()))
            .OrderBy(index => index.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var primaryKeyColumns = indexes.Where(index => index.IsPrimaryKey).SelectMany(index => index.KeyColumns).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        return new TableInspection(Schema, Name, RowCount, Columns, indexes, primaryKeyColumns, Columns.Any(column => column.IsRowVersion));
    }
}
