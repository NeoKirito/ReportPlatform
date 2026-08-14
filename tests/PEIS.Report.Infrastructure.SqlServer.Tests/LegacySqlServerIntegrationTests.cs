using System.Data;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using PEIS.Report.Contracts;
using PEIS.Report.Engine;
using PEIS.Report.Infrastructure.SqlServer;
using Xunit;

namespace PEIS.Report.Infrastructure.SqlServer.Tests;

/// <summary>
/// Read-only integration gate for a confirmed legacy SQL Server source of truth.
/// Tests are dynamically skipped unless REPORTPLATFORM_TEST_SQLSERVER is explicitly enabled.
/// No test performs INSERT, UPDATE, DELETE, DDL, or any mutation of the legacy system.
/// </summary>
[Trait("Category", "RequiresLegacySqlServer")]
public sealed class LegacySqlServerIntegrationTests
{
    [LegacySqlServerFact]
    public async Task Database_connection_and_required_schema_objects_are_observable_read_only()
    {
        var context = LegacySqlServerTestContext.RequireEnabled();
        await using var connection = new SqlConnection(context.ConnectionString);
        await connection.OpenAsync();

        const string sql = """
            SELECT t.name
            FROM sys.tables AS t
            WHERE t.name IN (@definitionTable, N'xt_djwh', N'xt_cxdy', N'xt_bgdy_djwh_zzj');
            """;
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.AddWithValue("@definitionTable", context.Mapping.DefinitionTable);
        await using var reader = await command.ExecuteReaderAsync();

        var observed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (await reader.ReadAsync()) observed.Add(reader.GetString(0));

        var mappedTableName = context.Mapping.DefinitionTable.Split('.', StringSplitOptions.RemoveEmptyEntries).Last();
        Assert.Contains(mappedTableName, observed);
    }

    [LegacySqlServerReportFixtureFact]
    public async Task Bbid_resolves_definition_template_and_sql()
    {
        var context = LegacySqlServerTestContext.RequireReportFixture();
        var provider = context.CreateDefinitionProvider();
        var request = context.CreateRequest();

        var definition = await provider.GetRequiredAsync(request, CancellationToken.None);

        Assert.Equal(context.ReportId, definition.ReportId);
        Assert.False(string.IsNullOrWhiteSpace(definition.TemplateContent));
        Assert.False(string.IsNullOrWhiteSpace(definition.SqlText));
        Assert.Equal("legacy-sql-server", definition.Source);
    }

    [LegacySqlServerReportFixtureFact]
    public async Task Legacy_parameters_execute_sql_with_expected_dataset_shape()
    {
        var context = LegacySqlServerTestContext.RequireReportFixture();
        var definition = await context.CreateDefinitionProvider().GetRequiredAsync(context.CreateRequest(), CancellationToken.None);
        var dataProvider = new SqlServerReportDataProvider(
            Options.Create(context.DatabaseOptions),
            new AdoNetLegacyQueryParameterBinder());

        var result = await dataProvider.QueryAsync(definition, context.CreateRequest(), CancellationToken.None);

        Assert.NotNull(result.DataSet);
        Assert.NotEmpty(result.Tables);
        if (context.DataSetShapeFixturePath is not null)
        {
            var expected = await DataSetShapeFixture.LoadAsync(context.DataSetShapeFixturePath);
            expected.AssertMatches(result);
        }
    }

    [LegacySqlServerReportFixtureFact]
    public async Task Database_template_version_is_observable_without_mutation()
    {
        var context = LegacySqlServerTestContext.RequireReportFixture();
        var provider = context.CreateDefinitionProvider();
        var request = context.CreateRequest();

        var first = await provider.GetVersionAsync(request, CancellationToken.None);
        var second = await provider.GetVersionAsync(request, CancellationToken.None);

        Assert.False(string.IsNullOrWhiteSpace(first.CacheToken));
        Assert.Equal(first.CacheToken, second.CacheToken);
        Assert.Equal(first.IsDatabaseVersion, second.IsDatabaseVersion);
        Assert.Equal(first.Source, second.Source);
    }

    [LegacySqlServerUnknownReportFixtureFact]
    public async Task Unknown_report_id_returns_explicit_report_not_found_error()
    {
        var context = LegacySqlServerTestContext.RequireUnknownReportFixture();
        var provider = context.CreateDefinitionProvider();
        var request = context.CreateRequest(context.UnknownReportId!);

        var error = await Assert.ThrowsAsync<LegacyReportDatabaseException>(() => provider.GetRequiredAsync(request, CancellationToken.None));

        Assert.Equal(LegacyReportDatabaseErrorCode.ReportNotFound, error.Code);
    }
}

[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
internal sealed class LegacySqlServerFactAttribute : FactAttribute
{
    public LegacySqlServerFactAttribute()
    {
        if (!LegacySqlServerTestContext.IsGateEnabled())
        {
            Skip = "Set REPORTPLATFORM_TEST_SQLSERVER=1 together with an explicitly approved read-only REPORT_DATABASE__CONNECTIONSTRING to run this integration suite.";
        }
        else if (!LegacySqlServerTestContext.HasConnectionString())
        {
            Skip = "REPORTPLATFORM_TEST_SQLSERVER is enabled but REPORT_DATABASE__CONNECTIONSTRING is not supplied; no database is discovered or connected automatically.";
        }
    }
}

[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
internal sealed class LegacySqlServerReportFixtureFactAttribute : FactAttribute
{
    public LegacySqlServerReportFixtureFactAttribute()
    {
        if (!LegacySqlServerTestContext.IsGateEnabled())
        {
            Skip = "Set REPORTPLATFORM_TEST_SQLSERVER=1 to enable real legacy integration tests.";
        }
        else if (!LegacySqlServerTestContext.HasConnectionString())
        {
            Skip = "Set an explicitly approved read-only REPORT_DATABASE__CONNECTIONSTRING; the suite will not discover a database automatically.";
        }
        else if (!LegacySqlServerTestContext.HasReportFixture())
        {
            Skip = "Set REPORTPLATFORM_TEST_REPORT_ID to an approved non-patient report-definition identifier to enable report-fixture assertions.";
        }
    }
}

[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
internal sealed class LegacySqlServerUnknownReportFixtureFactAttribute : FactAttribute
{
    public LegacySqlServerUnknownReportFixtureFactAttribute()
    {
        if (!LegacySqlServerTestContext.IsGateEnabled())
        {
            Skip = "Set REPORTPLATFORM_TEST_SQLSERVER=1 to enable real legacy integration tests.";
        }
        else if (!LegacySqlServerTestContext.HasConnectionString())
        {
            Skip = "Set an explicitly approved read-only REPORT_DATABASE__CONNECTIONSTRING; the suite will not discover a database automatically.";
        }
        else if (!LegacySqlServerTestContext.HasUnknownReportFixture())
        {
            Skip = "Set REPORTPLATFORM_TEST_UNKNOWN_REPORT_ID to an approved identifier known not to exist to enable explicit-not-found assertions.";
        }
    }
}

internal sealed class LegacySqlServerTestContext
{
    private const string GateVariable = "REPORTPLATFORM_TEST_SQLSERVER";
    private const string ConnectionVariable = "REPORT_DATABASE__CONNECTIONSTRING";

    private LegacySqlServerTestContext(string connectionString, LegacyReportSchemaMapping mapping, string? reportId, string? unknownReportId, string? dataSetShapeFixturePath)
    {
        ConnectionString = connectionString;
        Mapping = mapping;
        ReportId = reportId;
        UnknownReportId = unknownReportId;
        DataSetShapeFixturePath = dataSetShapeFixturePath;
        DatabaseOptions = new ReportDatabaseOptions
        {
            Provider = "SqlServer",
            ConnectionString = connectionString,
            CommandTimeoutSeconds = ReadInt("REPORTPLATFORM_TEST_COMMAND_TIMEOUT_SECONDS", 30, 1, 300),
            DefinitionCacheTtlSeconds = 1
        };
    }

    public string ConnectionString { get; }
    public LegacyReportSchemaMapping Mapping { get; }
    public string? ReportId { get; }
    public string? UnknownReportId { get; }
    public string? DataSetShapeFixturePath { get; }
    public ReportDatabaseOptions DatabaseOptions { get; }

    public static LegacySqlServerTestContext RequireEnabled()
    {
        if (!IsGateEnabled())
        {
            throw new InvalidOperationException($"{GateVariable} must be enabled before integration test execution.");
        }

        var connectionString = Environment.GetEnvironmentVariable(ConnectionVariable);
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException($"{GateVariable} is enabled but {ConnectionVariable} is not set. The suite will not discover or connect to a database automatically.");
        }

        var mapping = new LegacyReportSchemaMapping
        {
            DefinitionTable = ReadString("REPORTPLATFORM_TEST_DEFINITION_TABLE", "dbo.xt_bgdy_djwh_zzj"),
            ReportIdColumn = ReadString("REPORTPLATFORM_TEST_REPORT_ID_COLUMN", "djid"),
            TemplateColumn = ReadString("REPORTPLATFORM_TEST_TEMPLATE_COLUMN", "dj_frx"),
            SqlColumn = ReadString("REPORTPLATFORM_TEST_SQL_COLUMN", "djsql"),
            VersionColumn = ReadOptionalString("REPORTPLATFORM_TEST_VERSION_COLUMN"),
            UpdatedAtColumn = ReadOptionalString("REPORTPLATFORM_TEST_UPDATED_AT_COLUMN"),
            TemplateContentEncoding = ReadString("REPORTPLATFORM_TEST_TEMPLATE_CONTENT_ENCODING", "Base64Utf8"),
            FirstResultSetTableName = ReadString("REPORTPLATFORM_TEST_FIRST_RESULT_SET_TABLE_NAME", "Master"),
            TemplateKeyPrefix = ReadString("REPORTPLATFORM_TEST_TEMPLATE_KEY_PREFIX", "legacy-djwh")
        };
        mapping.Validate();

        var dataSetShape = ReadOptionalString("REPORTPLATFORM_TEST_DATASET_SHAPE");
        if (dataSetShape is not null && !File.Exists(dataSetShape))
        {
            throw new InvalidOperationException($"Configured dataset-shape fixture does not exist: '{dataSetShape}'.");
        }

        return new LegacySqlServerTestContext(
            connectionString,
            mapping,
            ReadOptionalString("REPORTPLATFORM_TEST_REPORT_ID"),
            ReadOptionalString("REPORTPLATFORM_TEST_UNKNOWN_REPORT_ID"),
            dataSetShape);
    }

    public static LegacySqlServerTestContext RequireReportFixture()
    {
        var context = RequireEnabled();
        if (string.IsNullOrWhiteSpace(context.ReportId))
        {
            throw new InvalidOperationException("REPORTPLATFORM_TEST_REPORT_ID is required by this report-fixture test.");
        }
        return context;
    }

    public static LegacySqlServerTestContext RequireUnknownReportFixture()
    {
        var context = RequireEnabled();
        if (string.IsNullOrWhiteSpace(context.UnknownReportId))
        {
            throw new InvalidOperationException("REPORTPLATFORM_TEST_UNKNOWN_REPORT_ID is required by this unknown-report test.");
        }
        return context;
    }

    public LegacyDatabaseReportDefinitionProvider CreateDefinitionProvider() => new(
        Options.Create(DatabaseOptions),
        Options.Create(Mapping),
        TimeProvider.System);

    public ReportRenderRequest CreateRequest(string? reportId = null)
    {
        var parameters = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        var parameterName = ReadOptionalString("REPORTPLATFORM_TEST_PARAMETER_NAME");
        var parameterJson = ReadOptionalString("REPORTPLATFORM_TEST_PARAMETER_JSON");
        if (parameterName is not null && parameterJson is not null)
        {
            using var document = JsonDocument.Parse(parameterJson);
            parameters[parameterName] = document.RootElement.Clone();
        }

        var legacyPayloadJson = ReadOptionalString("REPORTPLATFORM_TEST_LEGACY_PAYLOAD_JSON") ?? "{}";
        using var payload = JsonDocument.Parse(legacyPayloadJson);
        return new ReportRenderRequest(reportId ?? ReportId!, parameters, "legacy", null, null, payload.RootElement.Clone());
    }

    public static bool IsGateEnabled() => IsEnabled(Environment.GetEnvironmentVariable(GateVariable));
    public static bool HasConnectionString() => !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(ConnectionVariable));
    public static bool HasReportFixture() => !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("REPORTPLATFORM_TEST_REPORT_ID"));
    public static bool HasUnknownReportFixture() => !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("REPORTPLATFORM_TEST_UNKNOWN_REPORT_ID"));
    private static bool IsEnabled(string? value) => value is not null && (value.Equals("1", StringComparison.OrdinalIgnoreCase) || value.Equals("true", StringComparison.OrdinalIgnoreCase) || value.Equals("yes", StringComparison.OrdinalIgnoreCase));
    private static string ReadString(string variable, string fallback) => ReadOptionalString(variable) ?? fallback;
    private static string? ReadOptionalString(string variable) => string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(variable)) ? null : Environment.GetEnvironmentVariable(variable);
    private static int ReadInt(string variable, int fallback, int minimum, int maximum)
    {
        var raw = ReadOptionalString(variable);
        return raw is null ? fallback : int.TryParse(raw, out var value) && value >= minimum && value <= maximum
            ? value
            : throw new InvalidOperationException($"{variable} must be an integer from {minimum} to {maximum}.");
    }
}

internal sealed class DataSetShapeFixture
{
    public required IReadOnlyList<ExpectedTableShape> Tables { get; init; }

    public static async Task<DataSetShapeFixture> LoadAsync(string path)
    {
        await using var stream = File.OpenRead(path);
        var fixture = await JsonSerializer.DeserializeAsync<DataSetShapeFixture>(stream, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        return fixture is null || fixture.Tables.Count == 0
            ? throw new InvalidOperationException("Dataset shape fixture must contain at least one expected table.")
            : fixture;
    }

    public void AssertMatches(ReportDataSet actual)
    {
        foreach (var table in Tables)
        {
            Assert.True(actual.Tables.TryGetValue(table.Name, out var actualTable), $"Expected DataSet table '{table.Name}' was not returned.");
            Assert.True(actualTable.Rows.Count >= table.MinimumRows, $"Table '{table.Name}' returned {actualTable.Rows.Count} rows, below the fixture minimum of {table.MinimumRows}.");
            foreach (var expectedColumn in table.Columns)
            {
                Assert.True(actualTable.Columns.Contains(expectedColumn), $"Expected column '{expectedColumn}' was not found in table '{table.Name}'.");
            }
            if (table.ColumnTypes is not null)
            {
                foreach (var (columnName, expectedType) in table.ColumnTypes)
                {
                    Assert.True(actualTable.Columns.Contains(columnName), $"Expected typed column '{columnName}' was not found in table '{table.Name}'.");
                    var actualType = actualTable.Columns[columnName]!.DataType.FullName;
                    Assert.Equal(expectedType, actualType);
                }
            }
        }
    }
}

internal sealed class ExpectedTableShape
{
    public required string Name { get; init; }
    public int MinimumRows { get; init; }
    public required IReadOnlyList<string> Columns { get; init; }
    public IReadOnlyDictionary<string, string>? ColumnTypes { get; init; }
}
