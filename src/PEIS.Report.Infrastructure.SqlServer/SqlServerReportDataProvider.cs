using System.Data;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using PEIS.Report.Contracts;
using PEIS.Report.Engine;

namespace PEIS.Report.Infrastructure.SqlServer;

/// <summary>
/// Executes the SQL stored in a database report definition. It uses parameterized ADO.NET commands, returns a
/// distinct DataTable for every result set, and preserves the configured table-name convention where known.
/// </summary>
public sealed class SqlServerReportDataProvider : IReportDataProvider
{
    private readonly ReportDatabaseOptions _database;
    private readonly ILegacyQueryParameterBinder _binder;

    public SqlServerReportDataProvider(
        IOptions<ReportDatabaseOptions> database,
        ILegacyQueryParameterBinder binder)
    {
        _database = database.Value;
        _binder = binder;
    }

    public async Task<ReportDataSet> QueryAsync(
        ReportDefinition definition,
        ReportRenderRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_database.ConnectionString))
            throw new LegacyReportDatabaseException(LegacyReportDatabaseErrorCode.DatabaseConnectionFailed, "ReportDatabase connection string is not configured.");

        try
        {
            var connectionString = EnsureConnectionPooling(_database.ConnectionString);
            await using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

            try
            {
                // Prevent read lock contention with concurrent write transactions (registration, billing, etc.)
                await using var setCmd = connection.CreateCommand();
                setCmd.CommandText = "SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED;";
                await setCmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                // Non-fatal if isolation level change is disallowed
            }

            var dataSet = new DataSet($"Report_{definition.ReportId}");
            var tables = new Dictionary<string, DataTable>(StringComparer.OrdinalIgnoreCase);
            var totalRows = 0;
            var resultSet = 0;

            var queries = new List<ReportDataQuery>
            {
                new(ResolveTableName(definition, 0), definition.SqlText!)
            };
            if (definition.SupplementalQueries is { Count: > 0 })
                queries.AddRange(definition.SupplementalQueries);

            for (var queryIndex = 0; queryIndex < queries.Count; queryIndex++)
            {
                var query = queries[queryIndex];
                // The binder receives the original request and can therefore use the untouched LegacyPayload when
                // legacy SQL semantics cannot be inferred from the typed parameter dictionary alone.
                var binding = _binder.Bind(definition with { SqlText = query.SqlText }, request);
                await using var command = connection.CreateCommand();
                command.CommandText = binding.CommandText;
                command.CommandTimeout = Math.Clamp(_database.CommandTimeoutSeconds, 1, 300);
                foreach (var parameter in binding.Parameters)
                {
                    command.Parameters.Add(new SqlParameter("@" + parameter.Name, parameter.Value ?? DBNull.Value)
                    {
                        DbType = parameter.DbType
                    });
                }

                await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess, cancellationToken).ConfigureAwait(false);
                var queryResultSet = 0;
                do
                {
                    var primaryTableName = queryIndex == 0
                        ? (queryResultSet == 0 ? ResolveTableName(definition, 0) : $"Master_{queryResultSet + 1}")
                        : (queryResultSet == 0 ? (string.IsNullOrWhiteSpace(query.TableName) ? $"Master{queryIndex + 1}" : query.TableName) : $"{query.TableName}_{queryResultSet + 1}");

                    if (tables.ContainsKey(primaryTableName))
                        primaryTableName = $"{primaryTableName}_{resultSet + 1}";

                    var table = new DataTable(primaryTableName);
                    var columnNames = GetUniqueColumnNames(
                        Enumerable.Range(0, reader.FieldCount).Select(reader.GetName));
                    for (var ordinal = 0; ordinal < reader.FieldCount; ordinal++)
                    {
                        var type = reader.GetFieldType(ordinal) ?? typeof(object);
                        table.Columns.Add(columnNames[ordinal], type);
                    }
                    while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                    {
                        var values = new object[reader.FieldCount];
                        reader.GetValues(values);
                        table.Rows.Add(values);
                        totalRows++;
                    }

                    // Register primary table name
                    tables[table.TableName] = table;
                    dataSet.Tables.Add(table);

                    // Universal aliases registration:
                    if (queryIndex == 0 && queryResultSet == 0)
                    {
                        tables["Master"] = table;
                        tables[definition.ReportId] = table;
                        if (!tables.ContainsKey("Master1"))
                            tables["Master1"] = table;
                    }
                    else if (queryIndex > 0 && queryResultSet == 0)
                    {
                        if (!string.IsNullOrWhiteSpace(query.SubReportId))
                            tables[query.SubReportId] = table;

                        // 1-based index (e.g. jktjbbd: sub-query 1 -> Master1)
                        var index1 = $"Master{queryIndex}";
                        tables[index1] = table;

                        // 2-based index (e.g. tjdj: sub-query 1 -> Master2)
                        var index2 = $"Master{queryIndex + 1}";
                        if (!tables.ContainsKey(index2))
                            tables[index2] = table;
                    }

                    resultSet++;
                    queryResultSet++;
                }
                while (await reader.NextResultAsync(cancellationToken).ConfigureAwait(false));
            }

            return new ReportDataSet(tables, totalRows, dataSet);
        }
        catch (LegacyReportDatabaseException)
        {
            throw;
        }
        catch (SqlException exception) when (exception.Number == -2)
        {
            throw new LegacyReportDatabaseException(LegacyReportDatabaseErrorCode.DatabaseTimeout, $"Timed out executing SQL for report '{definition.ReportId}'.", exception);
        }
        catch (SqlException exception)
        {
            throw new LegacyReportDatabaseException(LegacyReportDatabaseErrorCode.QueryExecutionFailed, $"SQL execution failed for report '{definition.ReportId}'.", exception);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidCastException or DataException)
        {
            throw new LegacyReportDatabaseException(LegacyReportDatabaseErrorCode.DataSetMappingFailed, $"Failed to map SQL results to DataTables for report '{definition.ReportId}'.", exception);
        }
    }

    private static string EnsureConnectionPooling(string connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString)) return connectionString;
        try
        {
            var builder = new SqlConnectionStringBuilder(connectionString);
            if (!connectionString.Contains("Max Pool Size", StringComparison.OrdinalIgnoreCase))
                builder.MaxPoolSize = 128;
            if (!connectionString.Contains("Pooling", StringComparison.OrdinalIgnoreCase))
                builder.Pooling = true;
            if (!connectionString.Contains("Connect Timeout", StringComparison.OrdinalIgnoreCase) &&
                !connectionString.Contains("Connection Timeout", StringComparison.OrdinalIgnoreCase))
                builder.ConnectTimeout = 15;
            return builder.ConnectionString;
        }
        catch
        {
            return connectionString;
        }
    }

    internal static string[] GetUniqueColumnNames(IEnumerable<string> sourceNames)
    {
        var names = sourceNames.ToArray();
        // Legacy SELECTs may return duplicate aliases (e.g. a.* plus another zjhm).
        // Keep the first alias for FRX bindings and retain every value by ordinal.
        // Reserve explicit aliases so generated suffixes cannot steal a later column's name.
        var reserved = new HashSet<string>(names.Where(name => !string.IsNullOrEmpty(name)), StringComparer.OrdinalIgnoreCase);
        var assigned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var ordinal = 0; ordinal < names.Length; ordinal++)
        {
            var original = names[ordinal];
            if (!string.IsNullOrEmpty(original) && assigned.Add(original))
                continue;

            var prefix = string.IsNullOrEmpty(original) ? "Column" : original;
            var suffix = 1;
            string candidate;
            do { candidate = prefix + suffix++; }
            while (reserved.Contains(candidate) || !assigned.Add(candidate));
            names[ordinal] = candidate;
        }
        return names;
    }

    private static string ResolveTableName(ReportDefinition definition, int resultSet)
    {
        var metadataKey = resultSet == 0 ? "resultSet:0:tableName" : $"resultSet:{resultSet}:tableName";
        if (definition.ParameterMetadata.TryGetValue(metadataKey, out var configured) && !string.IsNullOrWhiteSpace(configured))
            return configured;
        return resultSet == 0 ? definition.ReportId : $"{definition.ReportId}_{resultSet + 1}";
    }
}
