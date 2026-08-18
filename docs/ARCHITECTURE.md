# PEIS Report Platform Architecture

## Purpose and scope

PEIS.ReportPlatform is a report/PDF and B/S silent-printing boundary for the PEIS medical examination system. The current implementation establishes legacy-request preservation, SQL Server definition/data integration, cache invalidation, a free FastReport rendering boundary, and workstation print orchestration. The confirmed `djwh + bbid`/`xmtm` route has completed a real database-to-PDF smoke; this is a base-PDF compatibility result rather than a claim of universal template coverage, visual equivalence, watermark equivalence, or physical-print acceptance.

## Production source of truth

> **The legacy report database is the production source of truth for report definitions, FRX template content, report SQL, version metadata, and selection relationships.**

The filesystem is **not** a production report-definition source. Local FRX files may be used only for unit tests, sanitized fixtures, diagnostics, or development experiments. A production deployment must configure `ReportEngine:DefinitionSource=LegacySqlServer` and an approved `ReportDatabase` connection; it must not silently fall back to a directory of FRX files.

| Layer | Responsibility | Production source | Non-production boundary |
|---|---|---|---|
| API compatibility | Preserves arbitrary legacy JSON and adapts it to `ReportRenderRequest`. | Legacy caller payload. | Sanitized JSON fixtures only. |
| Definition provider | Resolves report identity, template content, SQL and version metadata. | Legacy SQL Server via `LegacyDatabaseReportDefinitionProvider`. | Deterministic provider for tests. |
| Template provider | Returns database-owned FRX text and stable content hash. | Definition returned from legacy SQL Server. | In-memory/sanitized fixture content. |
| Data provider | Executes database-owned report SQL with parameter objects. | Approved read-only SQL Server connection. | Deterministic `DataSet` only. |
| Renderer | Converts a resolved definition and data into a PDF. | Isolated `FastReport.OpenSource` + official PdfSimple runtime, selected explicitly by configuration. | Deterministic/stub renderer. |
| Printing | Coordinates idempotent jobs and per-printer queues. | Report API artifact URLs and approved workstations. | Dry-run backend. |

## Report resolution and cache contract

`IReportDefinitionProvider` supplies an immutable `ReportDefinition`; `ITemplateProvider` supplies its immutable template content; and `IReportDataProvider` supplies a fresh data set for each render. A cache key is based on `ReportId` plus `ReportDefinitionVersion.CacheToken`. When a confirmed `VersionColumn` or `UpdatedAtColumn` is configured, the SQL provider retrieves a version token before definition reuse. The internal endpoint `POST /internal/cache/reports/{reportId}/invalidate` removes all versioned entries for the named report.

The cache never retains mutable `FastReport.Report`, `DataSet`, `DataTable`, user payloads, or a shared database connection. When no version/update column is configured, the provider uses a bounded TTL fallback. That fallback is deliberately observable but remains **UNVERIFIED** for legacy production behavior until a real schema fixture confirms it.

## Legacy SQL Server mapping

The SQL Server provider is configuration-driven and validates identifiers before embedding configured table or column names in a command. The following mapping is **confirmed for the observed `djwh + bbid` / `xmtm` production path**; its connection string must remain outside source control:

```json
{
  "ReportEngine": {
    "DefinitionSource": "LegacySqlServer",
    "Renderer": "FastReportOpenSource"
  },
  "ReportDatabase": {
    "Provider": "SqlServer",
    "ConnectionString": "<approved-read-only-connection-string>",
    "CommandTimeoutSeconds": 120,
    "DefinitionCacheTtlSeconds": 300
  },
  "LegacyReportSchema": {
    "DefinitionTable": "dbo.xt_bgdy_djwh_zzj",
    "ReportIdColumn": "djid",
    "TemplateColumn": "dj_frx",
    "SqlColumn": "djsql",
    "VersionColumn": "",
    "UpdatedAtColumn": "",
    "TemplateContentEncoding": "Base64Utf8",
    "FirstResultSetTableName": "Master",
    "TemplateKeyPrefix": "legacy-djwh"
  }
}
```

This mapping must not be generalized to unverified definition families. direct `djid` success semantics and `cxid` remain outside the current production contract; extend the resolver only after a read-only fixture establishes a new contract.

## Evidence tools and safety boundary

`tools/PEIS.LegacyDbInspector` uses `SELECT` queries against `sys.*` metadata and a controlled `TOP (1)` report sample. By default it writes hashes, lengths, column metadata, and row counts; FRX/SQL content is written only when an operator explicitly requests `--export-template` or `--export-sql`.

`tools/PEIS.LegacyApiProbe` relays a supplied JSON request without parsing it and writes only transport evidence: status, content type, elapsed time, byte length, SHA-256, and the `%PDF-` signature check. It never writes request or response bodies.

The real SQL Server integration suite is opt-in through `REPORTPLATFORM_TEST_SQLSERVER=1` plus an explicitly supplied read-only `REPORT_DATABASE__CONNECTIONSTRING`. The real FastReport PDF smoke additionally requires `REPORTPLATFORM_TEST_FASTREPORT=1`. Neither path scans for a database or mutates the legacy system.

## Current verification boundary

Static provider behavior, binder behavior, cache versioning, API contracts, queue behavior, tool builds, and default integration-test skipping are testable in this repository. The confirmed `xmtm` route has additionally validated real definition lookup, Base64 FRX decoding, non-empty `Master` data, free FastReport Load/RegisterData/Prepare/PDF export, and the original compatibility endpoint. Other schema relationships, historical ID paths, application watermarks, production PDF visual comparison, production load targets, and physical printer output remain **NOT VERIFIED** until the owner supplies authorized evidence.
