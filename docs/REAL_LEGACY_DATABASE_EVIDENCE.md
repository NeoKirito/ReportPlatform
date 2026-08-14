# Real Legacy Database Evidence

> **Collection boundary.** All observations in this document were collected on 2026-08-14 through an explicitly supplied legacy configuration and read-only SQL operations. No connection string, credential, patient row value, FRX body, or SQL body is committed. Raw diagnostic material remains under ignored `artifacts/` or `.runtime/` paths.

## Evidence status

| Area | Status | Observed result | Evidence artifact |
|---|---|---|---|
| Database target | **CONFIRMED** | The supplied configuration connects to database `TJXT3.0`; the server requires a legacy TLS negotiation and an internal certificate trust override for the inspection process. | `artifacts/legacy-evidence/schema/` |
| `djwh` definition table | **CONFIRMED** | `dbo.xt_bgdy_djwh_zzj` exists with 70 rows and primary key `djid`. | `schema/schema.md` |
| `xt_bbdy`, `xt_djwh`, `xt_cxdy` | **UNVERIFIED / not found** | The supplied database does not expose tables with these exact names. No alternate relationship is inferred from their absence. | `schema/schema.md` |
| Sample definition lookup | **CONFIRMED** | The supplied `bbid=xmtm` request succeeds through the old API, and `xmtm` exists as `dbo.xt_bgdy_djwh_zzj.djid`. | `definitions/djid-xmtm.json`; API probe evidence |
| FRX storage | **CONFIRMED** | `dj_frx` is nullable `varchar(max)` and the observed field is Base64-encoded UTF-8 FRX XML. | `schema/schema.md`; `frx/djid-xmtm-structure.json` |
| SQL storage | **CONFIRMED** | `djsql` is nullable SQL `text`; the observed field contains the database-owned query definition. | `schema/schema.md`; `definitions/djid-xmtm.json` |
| Version field | **CONFIRMED absent for this table** | The observed 14-column table has no rowversion/timestamp, update-time, or explicit version field. Cache refresh therefore requires the documented TTL fallback until another reliable source is evidenced. | `schema/schema.md` |
| Non-empty `Master` contract | **CONFIRMED at data-provider boundary** | 16 user-approved read-only samples each returned one row with an identical seven-column CLR schema; no values or request identifiers were exported. | Ignored `.runtime/private-legacy-evidence/approved-master-sample-scan.json`; committed shape-only fixture |

## Confirmed `djwh` mapping

| Mapping item | Confirmed value |
|---|---|
| Schema/table | `dbo.xt_bgdy_djwh_zzj` |
| Definition key | `djid varchar(20) NOT NULL` |
| Primary key | `djid` |
| SQL column | `djsql text NULL` |
| FRX column | `dj_frx varchar(max) NULL` |
| Template storage encoding | Base64-encoded UTF-8 XML |
| Configured DataTable name for the observed report | `Master` |
| Version strategy | TTL fallback; no reliable version column observed |

The supported application configuration now uses this mapping only when `ReportEngine:DefinitionSource` is explicitly set to `LegacySqlServer`. It does not place the approved runtime connection string in `appsettings.json`.

## Approved sample: `xmtm`

The legacy POST request supplied for this gate uses `querytype=djwh` and `bbid=xmtm`. The old API returned HTTP 200 with a PDF signature and 154,852 bytes. The definition table has a record with `djid=xmtm`; its stored SQL and FRX were observed without copying their bodies into source control.

| Artifact | Status | Non-sensitive observation |
|---|---|---|
| SQL | **CONFIRMED** | 54 UTF-8 bytes; SHA-256 `3B4BB29EF9E2FB843B954665626B40EBBE2CDA9DBA74E6E986CE28DDA17E3D37` |
| Stored template field | **CONFIRMED** | 3,256 UTF-8 bytes; SHA-256 `180E153E819FAD9A5385FE00D0DBD5350F7B004899BA84CA9D92043A51D438C6` |
| Decoded FRX XML | **CONFIRMED** | SHA-256 `99F565209534D02C02FB45AA968A674AB95CC7C50232FDFC554CAEDC488EAB1F` |
| FRX data source | **CONFIRMED** | `Master` |
| FRX parameters | **CONFIRMED** | `yhmc`, `servertime` |
| Stored procedure | **CONFIRMED** | `dbo.tjxt_fastreportgetTxmxx` with `@grtjgcjjgid varchar(max)` and `@sfxmddid varchar(max)` |

## ID resolution boundary

| ID family | Status | Evidence-backed conclusion |
|---|---|---|
| `bbid` with `querytype=djwh` | **CONFIRMED** | The supplied old request succeeds for `bbid=xmtm`; the compatible resolver maps that payload form to `dbo.xt_bgdy_djwh_zzj.djid`. |
| `djid` only | **PARTIAL** | The controlled `djid=xmtm` variant returns old API JSON rather than a PDF. Its GBK-decoded message identifies an ambiguous `grtjgcjjgid` column, which is a downstream SQL ambiguity and does not prove a direct public lookup rule. The no-`querytype` and `querytype=djid` variants also return the same non-PDF response class. |
| `cxid` | **OUT OF CURRENT PRODUCTION CONTRACT** | A read-only system-catalog query found zero exact `cxid` object or column names. The broader `cx` matches are examination-item objects, not report-definition objects. |
| Multiple IDs | **CONFIRMED for `querytype=djwh` + `bbid` only** | A controlled request with valid `bbid=xmtm` and an additional unknown `djid` continues to return a PDF. This confirms `bbid` selection precedence within the observed `djwh` family only; it does not claim a global rule. |

## Cache implication

The sample table has no observed `rowversion`, `timestamp`, update-time, or version column. The `LegacyDatabaseReportDefinitionProvider` therefore uses a bounded TTL token for this confirmed `djwh` mapping. No update was issued to the database to test invalidation.

## Remaining evidence required

The Real Legacy Compatibility Gate is **not yet PASS**. To complete it, provide an approved valid direct-`djid` PDF fixture if that route is required in production and a licensed FastReport runtime. A historical `cxid` request or implementation artifact is needed only if `cxid` must be restored beyond the current production contract. The non-empty `Master` data-provider boundary is now confirmed; the remaining renderer-specific questions require the licensed FastReport runtime.
