# PEIS Report Platform Architecture

## Purpose and scope

PEIS.ReportPlatform is a report/PDF and B/S silent-printing boundary for the PEIS medical examination system. The current implementation establishes deterministic rendering seams, legacy-request preservation, SQL Server integration seams, cache invalidation, and workstation print orchestration. It does not claim a validated production FastReport render until approved real database and FRX evidence is supplied.

## Production source of truth

> **The legacy report database is the production source of truth for report definitions, FRX template content, report SQL, version metadata, and selection relationships.**

The filesystem is **not** a production report-definition source. Local FRX files may be used only for unit tests, sanitized fixtures, diagnostics, or development experiments. A production deployment must configure `ReportEngine:DefinitionSource=LegacySqlServer` and an approved `ReportDatabase` connection; it must not silently fall back to a directory of FRX files.

| Layer | Responsibility | Production source | Non-production boundary |
|---|---|---|---|
| API compatibility | Preserves arbitrary legacy JSON and adapts it to `ReportRenderRequest`. | Legacy caller payload. | Sanitized JSON fixtures only. |
| Definition provider | Resolves report identity, template content, SQL and version metadata. | Legacy SQL Server via `LegacyDatabaseReportDefinitionProvider`. | Deterministic provider for tests. |
| Template provider | Returns database-owned FRX text and stable content hash. | Definition returned from legacy SQL Server. | In-memory/sanitized fixture content. |
| Data provider | Executes database-owned report SQL with parameter objects. | Approved read-only SQL Server connection. | Deterministic `DataSet` only. |
| Renderer | Converts a resolved definition and data into a PDF. | Production renderer once licensed assets and FRX fixtures are approved. | Deterministic/stub renderer. |
| Printing | Coordinates idempotent jobs and per-printer queues. | Report API artifact URLs and approved workstations. | Dry-run backend. |

## Report resolution and cache contract

`IReportDefinitionProvider` supplies an immutable `ReportDefinition`; `ITemplateProvider` supplies its immutable template content; and `IReportDataProvider` supplies a fresh data set for each render. A cache key is based on `ReportId` plus `ReportDefinitionVersion.CacheToken`. When a confirmed `VersionColumn` or `UpdatedAtColumn` is configured, the SQL provider retrieves a version token before definition reuse. The internal endpoint `POST /internal/cache/reports/{reportId}/invalidate` removes all versioned entries for the named report.

The cache never retains mutable `FastReport.Report`, `DataSet`, `DataTable`, user payloads, or a shared database connection. When no version/update column is configured, the provider uses a bounded TTL fallback. That fallback is deliberately observable but remains **UNVERIFIED** for legacy production behavior until a real schema fixture confirms it.

## Legacy SQL Server mapping

The SQL Server provider is configuration-driven and validates identifiers before embedding the configured table or column names in a command. The default mapping is only a documented candidate:

```json
{
  "ReportDatabase": {
    "Provider": "SqlServer",
    "ConnectionString": "<approved-read-only-connection-string>",
    "CommandTimeoutSeconds": 30,
    "DefinitionCacheTtlSeconds": 300
  },
  "LegacyReportSchema": {
    "DefinitionTable": "xt_bbdy",
    "ReportIdColumn": "bbid",
    "TemplateColumn": "bb_frx",
    "SqlColumn": "bb_sql",
    "VersionColumn": "<confirmed-column-or-empty>",
    "UpdatedAtColumn": "<confirmed-column-or-empty>",
    "TemplateKeyPrefix": "legacy-db"
  }
}
```

Do not treat these defaults as production schema proof. If `djid` or `cxid` uses a join table or separate definition source, record the relationship with the read-only inspector first and configure or extend the provider only after a sanitized integration fixture establishes the contract.

## Evidence tools and safety boundary

`tools/PEIS.LegacyDbInspector` uses `SELECT` queries against `sys.*` metadata and a controlled `TOP (1)` report sample. By default it writes hashes, lengths, column metadata, and row counts; FRX/SQL content is written only when an operator explicitly requests `--export-template` or `--export-sql`.

`tools/PEIS.LegacyApiProbe` relays a supplied JSON request without parsing it and writes only transport evidence: status, content type, elapsed time, byte length, SHA-256, and the `%PDF-` signature check. It never writes request or response bodies.

The real integration suite is opt-in through `REPORTPLATFORM_TEST_SQLSERVER=1` plus an explicitly supplied read-only `REPORT_DATABASE__CONNECTIONSTRING`. It never scans for a database or mutates the legacy system.

## Current verification boundary

Static provider behavior, binder behavior, cache versioning, API contracts, queue behavior, tool builds, and default integration-test skipping are testable in this repository. Real schema relationships, FRX rendering, historical parameter syntax, production PDF comparison, and physical printer output remain **NOT VERIFIED** until the owner supplies authorized, sanitized evidence.
