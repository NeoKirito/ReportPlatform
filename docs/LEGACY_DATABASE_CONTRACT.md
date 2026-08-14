# Legacy Database Contract

## Contract status

The legacy report database is the **production source of truth** for definition selection, stored SQL, FRX, parameters, and relationships. Filesystem FRX content is limited to diagnostics and sanitized offline fixtures; it is never a production definition source.

The `djwh` report family below is evidenced by approved read-only observations. Other historical ID families remain deliberately unmodeled until their own real fixture exists.

| Artifact | Status | Confirmed mapping or boundary |
|---|---|---|
| `dbo.xt_bgdy_djwh_zzj` | **CONFIRMED** | 70 rows; `djid` primary key; `djsql` SQL `text`; `dj_frx` `varchar(max)`. |
| `querytype=djwh` + `bbid` | **CONFIRMED** | Resolver selects the `djid` definition key for the observed `xmtm` request family. |
| `dj_frx` storage | **CONFIRMED** | Base64-encoded UTF-8 FRX XML; decoded before rendering. |
| First result-set name | **CONFIRMED** | The observed FRX dictionary expects `Master`; the provider maps result set 0 to `Master`. |
| Version/update signal | **CONFIRMED absent** | No rowversion/timestamp/update/version field was observed on this definition table; TTL fallback is required. |
| `xt_bbdy`, `xt_djwh`, `xt_cxdy` | **UNVERIFIED / exact names not found** | The supplied database did not expose these exact table names. No replacement relationship is inferred. |
| Direct `djid`, `cxid`, multi-ID precedence | **UNVERIFIED** | The one valid old request evidences only the `djwh` + `bbid` path. |

## Production configuration

Set `ReportEngine:DefinitionSource` to `LegacySqlServer` only after storing the approved connection in deployment secret management. The source-controlled configuration intentionally leaves `ReportDatabase:ConnectionString` empty.

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

The provider validates identifier configuration before constructing SQL. Definition IDs and report parameters are passed as ADO.NET parameters. Database-owned `djsql` remains definition content, never caller-supplied text.

## `djwh` parameter contract

The approved `xmtm` sample stores a procedure invocation using `[grtjgcjjgid]` and `[sfxmddid]`. The real procedure declares corresponding `varchar(max)` input parameters. `AdoNetLegacyQueryParameterBinder` converts only bracketed names that appear as scalar values in `LegacyPayload`, including nested JSON such as `djh.grtjgcjjgid`, and binds them as `DbType.AnsiString` parameters. It leaves all other bracketed SQL unchanged.

`@name` remains supported. `${name}`, positional parameters, arbitrary `Regex`/`Replace`, and custom `PrepareQuery` behavior are **UNVERIFIED** and are not emulated.

## Cache contract

`dbo.xt_bgdy_djwh_zzj` has no observed trustworthy version or update column. `LegacyDatabaseReportDefinitionProvider` therefore returns a bounded TTL version token when this mapping is used. It does not read the FRX for a version check, and no test writes to the legacy database to simulate invalidation.

## Read-only integration gate

`tests/PEIS.Report.Infrastructure.SqlServer.Tests` skips by default. To run the confirmed `xmtm` path, set process-local variables only; do not commit them.

```powershell
$env:REPORTPLATFORM_TEST_SQLSERVER = '1'
$env:REPORT_DATABASE__CONNECTIONSTRING = '<approved-read-only-connection-string>'
$env:REPORTPLATFORM_TEST_REPORT_ID = 'xmtm'
$env:REPORTPLATFORM_TEST_LEGACY_PAYLOAD_JSON = '<approved legacy request JSON>'
$env:REPORTPLATFORM_TEST_DATASET_SHAPE = 'tests/Fixtures/LegacyReal/expected-shapes/djid-xmtm.shape.json'
```

The suite performs `SELECT`/metadata reads and stored-procedure execution only. It does not issue INSERT, UPDATE, DELETE, DDL, or cache-mutation commands. See `docs/REAL_LEGACY_DATABASE_EVIDENCE.md` and `docs/REAL_FRX_DATA_CONTRACT.md` for the audit record.
