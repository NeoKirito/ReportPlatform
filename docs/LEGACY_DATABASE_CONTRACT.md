# Legacy database contract and evidence gate

## Contract status

The legacy report database is the **production source of truth**. The current SQL Server provider is production-oriented infrastructure with configurable mapping; it is not a declaration that the candidate `xt_*` schema is confirmed. All items below remain **UNVERIFIED** unless marked by evidence collected from an approved read-only connection.

| Candidate artifact | Current assumption | Required confirmation |
|---|---|---|
| `xt_bbdy` | Candidate report-definition table; default ID column `bbid`. | Actual columns, key, template field, SQL field, and lookup rule. |
| `xt_djwh` | Candidate registration/guide-sheet definition table with `djid`. | Whether it maps directly to a report or resolves through a relationship. |
| `xt_cxdy` | Candidate query-definition table with `cxid`. | SQL ownership, parameter syntax, and report-selection role. |
| `xt_bgdy_djwh_zzj` | Candidate association table. | Foreign keys and selection precedence. |
| `bb_frx` / `bb_sql` | Candidate FRX and SQL columns. | Exact names, data types, nullability, encoding, and contents for a sanitized sample. |
| rowversion/update time | Candidate cache-version signal. | A usable column and its update behavior after an approved definition change. |

## Configure the production provider only after evidence collection

Set `ReportEngine:DefinitionSource` to `LegacySqlServer` only when the following values are confirmed by the database owner. Store connection credentials in the deployment secret store, never in `appsettings.json`, fixtures, source control, command history, or inspection output.

```json
{
  "ReportEngine": {
    "DefinitionSource": "LegacySqlServer"
  },
  "ReportDatabase": {
    "Provider": "SqlServer",
    "ConnectionString": "<secret:approved-read-only-legacy-report-database>",
    "CommandTimeoutSeconds": 30,
    "DefinitionCacheTtlSeconds": 300
  },
  "LegacyReportSchema": {
    "DefinitionTable": "<confirmed-table>",
    "ReportIdColumn": "<confirmed-report-id-column>",
    "TemplateColumn": "<confirmed-frx-column>",
    "SqlColumn": "<confirmed-sql-column>",
    "VersionColumn": "<confirmed-rowversion-or-version-column-or-empty>",
    "UpdatedAtColumn": "<confirmed-updated-at-column-or-empty>",
    "TemplateKeyPrefix": "legacy-db"
  }
}
```

The provider validates configured identifiers before using them in a command. Report IDs and query inputs are supplied as ADO.NET parameters. The report SQL itself is database-owned definition content; do not accept it from the HTTP request.

## Read-only evidence procedure

1. Obtain written approval for a restricted read-only account and a safe, non-production or approved sanitized report-definition ID. Do not rely on automatic network discovery.
2. Run `tools/sql/inspect-legacy-report-schema.sql` in SSMS, or run `PEIS.LegacyDbInspector inspect-schema --connection <approved-connection>`. Archive `schema.md`, `schema.json`, and `table-summary.json` outside source control when required by the environment owner.
3. Resolve one controlled definition with `inspect-report --id <id> --id-type bbid|djid|cxid`. The default result contains only field names, types, lengths, timestamps, and SHA-256 fingerprints.
4. Export FRX or SQL only with explicit approval using `--export-template` or `--export-sql`. Keep exported content in a secured diagnostic location; it is excluded from the `LegacyReal` fixture boundary.
5. Configure actual mapping values, then run the opt-in integration suite using `REPORTPLATFORM_TEST_SQLSERVER=1` and `REPORT_DATABASE__CONNECTIONSTRING`. Add a shape-only JSON fixture only after sanitization.
6. For cache evidence, arrange a separately approved configuration-only change or a controlled clone. Never use test code to mutate the live legacy system.

## SQL provider behavior

`LegacyDatabaseReportDefinitionProvider` issues parameterized reads for definition/version metadata. `LegacyDatabaseTemplateProvider` returns FRX text embedded in the resolved definition. `SqlServerReportDataProvider` executes the resolved report SQL with `AdoNetLegacyQueryParameterBinder` and produces named `DataTable` values for the rendering boundary.

A database timeout becomes an explicit legacy database timeout error. Connection failures, absent report records, absent FRX content, absent SQL content, and invalid/missing parameters are represented by explicit `LegacyReportDatabaseErrorCode` values. Failures are not cached as report definitions.

## Integration test gate

`tests/PEIS.Report.Infrastructure.SqlServer.Tests` uses discovery-time skips by default. To opt in, set:

```powershell
$env:REPORTPLATFORM_TEST_SQLSERVER = '1'
$env:REPORT_DATABASE__CONNECTIONSTRING = '<approved-read-only-connection-string>'
$env:REPORTPLATFORM_TEST_REPORT_ID = '<approved-non-patient-report-definition-id>'
$env:REPORTPLATFORM_TEST_UNKNOWN_REPORT_ID = '<approved-id-known-not-to-exist>'
```

When actual columns differ from the defaults, set the corresponding `REPORTPLATFORM_TEST_*` mapping variables documented in `tests/Fixtures/LegacyReal/README.md`. The suite uses only `SELECT` operations. Database-rendering success, production PDF parity, and cache refresh following a real update remain **NOT VERIFIED** until real evidence is supplied.
