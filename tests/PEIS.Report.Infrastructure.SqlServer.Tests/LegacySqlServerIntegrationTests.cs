using Xunit;

namespace PEIS.Report.Infrastructure.SqlServer.Tests;

/// <summary>
/// Real integration gate. These tests are deliberately skipped until a read-only legacy SQL Server fixture,
/// confirmed schema mapping, representative FRX, and sanitized legacy JSON are supplied.
/// </summary>
[Trait("Category", "RequiresLegacySqlServer")]
public sealed class LegacySqlServerIntegrationTests
{
    private const string Gate = "Requires REPORT_DATABASE__CONNECTIONSTRING plus confirmed xt_* schema mapping and sanitized legacy fixtures.";

    [Fact(Skip = Gate)]
    public Task Bbid_resolves_definition_template_and_sql() => Task.CompletedTask;

    [Fact(Skip = Gate)]
    public Task Legacy_parameters_execute_sql_with_expected_dataset_shape() => Task.CompletedTask;

    [Fact(Skip = Gate)]
    public Task Dataset_preserves_expected_table_names_columns_and_rows() => Task.CompletedTask;

    [Fact(Skip = Gate)]
    public Task Database_template_version_change_refreshes_definition_cache() => Task.CompletedTask;

    [Fact(Skip = Gate)]
    public Task Unknown_report_id_returns_explicit_report_not_found_error() => Task.CompletedTask;
}
