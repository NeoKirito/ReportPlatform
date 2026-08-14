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

        // The binder receives the original request and can therefore use the untouched LegacyPayload when legacy
        // SQL semantics cannot be inferred from the typed parameter dictionary alone.
        var binding = _binder.Bind(definition, request);
        try
        {
            await using var connection = new SqlConnection(_database.ConnectionString);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = binding.CommandText;
            command.CommandTimeout = Math.Clamp(_database.CommandTimeoutSeconds, 1, 300);
            foreach (var parameter in binding.Parameters)
            {
                var sqlParameter = new SqlParameter("@" + parameter.Name, parameter.Value ?? DBNull.Value)
                {
                    DbType = parameter.DbType
                };
                command.Parameters.Add(sqlParameter);
            }

            await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess, cancellationToken).ConfigureAwait(false);
            var dataSet = new DataSet($"Report_{definition.ReportId}");
            var tables = new Dictionary<string, DataTable>(StringComparer.OrdinalIgnoreCase);
            var totalRows = 0;
            var resultSet = 0;
            do
            {
                var tableName = ResolveTableName(definition, resultSet);
                if (tables.ContainsKey(tableName))
                    tableName = $"{tableName}_{resultSet + 1}";
                var table = new DataTable(tableName);
                for (var ordinal = 0; ordinal < reader.FieldCount; ordinal++)
                {
                    var type = reader.GetFieldType(ordinal) ?? typeof(object);
                    table.Columns.Add(reader.GetName(ordinal), type);
                }
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    var values = new object[reader.FieldCount];
                    reader.GetValues(values);
                    table.Rows.Add(values);
                    totalRows++;
                }
                tables.Add(table.TableName, table);
                dataSet.Tables.Add(table);
                resultSet++;
            }
            while (await reader.NextResultAsync(cancellationToken).ConfigureAwait(false));

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

    private static string ResolveTableName(ReportDefinition definition, int resultSet)
    {
        var metadataKey = resultSet == 0 ? "resultSet:0:tableName" : $"resultSet:{resultSet}:tableName";
        if (definition.ParameterMetadata.TryGetValue(metadataKey, out var configured) && !string.IsNullOrWhiteSpace(configured))
            return configured;
        return resultSet == 0 ? definition.ReportId : $"{definition.ReportId}_{resultSet + 1}";
    }
}
