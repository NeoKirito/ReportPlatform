# Legacy Database Report Contract

This document separates known development evidence from unverified legacy database behavior. Database configuration remains the intended production source of truth; filesystem FRX files are restricted to fixtures, diagnostics, or development fallback work.

## Evidence status

No database connection, schema export, current old-service source, representative FRX, or sanitized production request fixture has been supplied in this environment. Consequently, no table relationship or field meaning below is claimed as production-confirmed.

| Table | Known from supplied task context | Expected role | Unknown / UNVERIFIED |
|---|---|---|---|
| `xt_bbdy` | Name is known; concepts such as `bb_frx` have been mentioned | Candidate report-definition source containing report identity, FRX, SQL, and metadata | Primary key, actual SQL column, version field, update timestamp, encoding, and whether one record maps to multiple data sets |
| `xt_djwh` | Name is known; concepts such as `djsql` and `dj_frx` have been mentioned | Candidate registration/guide-sheet definition source | Identifier semantics, relationship to `bbid`, query/template field types, and versioning mechanism |
| `xt_cxdy` | Name is known | Candidate query-definition source | Key, SQL field, parameter metadata, and whether it is selected by `cxid` directly or via another table |
| `xt_bgdy_djwh_zzj` | Name is known | Candidate relationship/association table between a report and registration/guide definitions | Both foreign keys, cardinality, ordering semantics, and whether it participates in production resolution |

## Report ID resolution

The compatibility API still preserves the complete JSON object in `LegacyPayload`. The current adapter recognises candidate names `bbid`, `djid`, `cxid`, and `reportId` case-insensitively only to supply an engine identifier. The following points are **UNVERIFIED** and must be proven by old-service behavior or fixtures before production routing is enabled:

| Question | Required evidence |
|---|---|
| Which identifier selects which table | Sanitized JSON requests and query traces or old-service source |
| Priority if several IDs are present | Requests containing multiple IDs with corresponding legacy output |
| String/number/case behavior | Fixtures covering each representation |
| Relationship traversal between `xt_bbdy`, `xt_djwh`, `xt_cxdy`, and `xt_bgdy_djwh_zzj` | Schema DDL, read-only sample rows, and old-service query behavior |

## Production mapping configuration

`LegacySqlServer` is deliberately disabled by default. When evidence exists, configure only through deployment configuration or environment variables; do not commit a password.

```json
{
  "ReportEngine": { "DefinitionSource": "LegacySqlServer" },
  "ReportDatabase": {
    "Provider": "SqlServer",
    "ConnectionString": "",
    "CommandTimeoutSeconds": 30,
    "DefinitionCacheTtlSeconds": 300
  },
  "LegacyReportSchema": {
    "DefinitionTable": "<confirmed table>",
    "ReportIdColumn": "<confirmed ID column>",
    "TemplateColumn": "<confirmed FRX column>",
    "SqlColumn": "<confirmed SQL column>",
    "VersionColumn": "<confirmed version column or empty>",
    "UpdatedAtColumn": "<confirmed timestamp column or empty>"
  }
}
```

Use `ReportDatabase__ConnectionString` to override the blank connection string. The implementation validates mapping identifiers and fails with an explicit schema-mapping error when required values remain unresolved.

## Parameter binding contract

`ILegacyQueryParameterBinder` receives the full `ReportRenderRequest`, including `LegacyPayload`. The baseline binder uses parameterized ADO.NET `@name` tokens and binds values without SQL text substitution. It does **not** claim compatibility with `${name}`, regex-based replacement, `PrepareQuery`, or other historical syntax until a real legacy fixture identifies that behavior.

## Cache contract

When a confirmed `VersionColumn` or `UpdatedAtColumn` is configured, the provider performs a lightweight version check and creates a cache key from `ReportId + version token`. When neither exists, it uses a bounded TTL token and marks the behavior as fallback/unverified. The internal invalidation endpoint accepts `POST /internal/cache/reports/{reportId}/invalidate`; it removes all versioned immutable definition entries for that report and never caches mutable `FastReport.Report`, `DataSet`, `DataTable`, or user data.

## Exact artifacts required for the real integration gate

1. A read-only SQL Server connection and connectivity instructions for the legacy report database.
2. DDL or a precise column list for `xt_bbdy`, `xt_djwh`, `xt_cxdy`, and `xt_bgdy_djwh_zzj`, including keys and any `version`, `rowversion`, or update-time column.
3. At least one sanitized legacy JSON request for each ID path (`bbid`, `djid`, `cxid`) and the corresponding expected definition selection.
4. A sanitized real FRX stored in the database plus the expected data-source/table names used by that FRX.
5. A sanitized SQL definition and representative parameter values, including any non-`@name` placeholder syntax.
6. A fixture that demonstrates database definition/template update behavior so cache refresh can be verified.

> Until these artifacts are available, the code is an implementation of a configurable integration boundary and synthetic fixture, not proof that it matches a production PEIS schema.
