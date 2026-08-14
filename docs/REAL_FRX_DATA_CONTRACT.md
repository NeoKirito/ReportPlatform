# Real FRX–DataSet Contract: `xmtm`

> **Scope.** This contract is derived from one approved real `djwh` definition (`xmtm`), its database-owned FRX and SQL, and read-only stored-procedure executions. The non-empty validation ran against 16 user-approved sample pairs and exported only row counts, column names, and CLR types. It contains no FRX body, SQL body, request values, patient values, or connection material.

## FRX contract

The `dbo.xt_bgdy_djwh_zzj.dj_frx` field stores Base64-encoded UTF-8 FastReport XML. The decoded dictionary declares the `Master` data source, and the template contains `yhmc` and `servertime` parameters.

| FRX element | Confirmed value |
|---|---|
| Stored template field | `dj_frx` |
| Storage encoding | Base64-encoded UTF-8 XML |
| Decoded data source | `Master` |
| Data band | `Data1` |
| Declared parameters | `yhmc`, `servertime` |
| Referenced columns | `Master.xm`, `Master.nl`, `Master.tmh`, `Master.zxksmc`, `Master.xmmc`, `Master.xb`, `Master.flmc` |

## Database result contract

The corresponding `djsql` definition executes `dbo.tjxt_fastreportgetTxmxx` with two legacy payload values. The initial approved instance returned zero rows, after which 16 additional user-approved sample pairs were executed read-only. All 16 returned one non-empty result with the same seven-column CLR schema; no field values were exported.

| Result set | Required application DataTable name | Observed SQL column | SQL type | Observed CLR type |
|---|---|---|---|---|
| 1 | `Master` | `xm` | `varchar` | `System.String` |
| 1 | `Master` | `nl` | `int` | `System.Int32` |
| 1 | `Master` | `tmh` | `varchar` | `System.String` |
| 1 | `Master` | `zxksmc` | `varchar` | `System.String` |
| 1 | `Master` | `XMMC` | `varchar` | `System.String` |
| 1 | `Master` | `xb` | `varchar` | `System.String` |
| 1 | `Master` | `flmc` | `varchar` | `System.String` |

## Cross-check

| Check | Result | Status |
|---|---|---|
| FRX data source name versus provider table name | `Master` is now explicitly configured as `resultSet:0:tableName`. | **MATCH** |
| FRX `xm`, `nl`, `tmh`, `zxksmc`, `xb`, `flmc` references | Corresponding result-set columns are present. | **MATCH** |
| FRX `Master.xmmc` reference | All 16 non-empty samples expose `XMMC` as `System.String`; the new provider uses .NET `DataTable`, whose column lookup is case-insensitive and is covered by an offline regression. FastReport runtime behavior remains pending a licensed smoke test. | **MATCH at provider boundary; renderer pending** |
| FRX declared `nl` type | All 16 non-empty samples expose `nl` as `System.Int32`, while the FRX declares `System.Int16`. The provider preserves the observed source type. Whether the licensed FastReport runtime coerces it for rendering remains smoke-test pending. | **PARTIAL** |
| Non-empty data execution | 16 of 16 user-approved pairs returned one row with one identical seven-column schema. The integrated data-provider test now requires `Master` to contain at least one row. | **CONFIRMED at data-provider boundary** |

The offline fixture at `tests/Fixtures/LegacyReal/expected-shapes/djid-xmtm.shape.json` deliberately locks only non-sensitive table/column names and now requires a minimum `Master` row count of one. It contains no request identifiers or row values.

## Implementation consequence

`LegacyReportSchema:FirstResultSetTableName=Master` is an evidence-backed requirement for this definition family. `LegacyDatabaseTemplateProvider` decodes `Base64Utf8` storage before handing template text to a renderer. **FASTREPORT_DEPENDENCY_BLOCKED:** no licensed FastReport package or DLL has been supplied or found, so no renderer smoke test is claimed. The remaining renderer-specific question is `System.Int32` source values versus the FRX `System.Int16` declaration.
