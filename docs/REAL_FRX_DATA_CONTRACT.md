# Real FRX–DataSet Contract: `xmtm`

> **Scope.** This contract is derived from one approved real `djwh` definition (`xmtm`), its database-owned FRX and SQL, and a read-only stored-procedure execution. It contains no FRX body, SQL body, request values, patient values, or connection material.

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

The corresponding `djsql` definition executes `dbo.tjxt_fastreportgetTxmxx` with two legacy payload values. A read-only procedure execution returned one result set with zero rows for the supplied instance, but its schema is observable.

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
| FRX `Master.xmmc` reference | SQL Server metadata reports `XMMC`. The relationship differs only by case; deployed FastReport case behavior is not yet smoke-tested. | **PARTIAL** |
| FRX declared `nl` type | FRX declares `System.Int16`; the observed result type is `System.Int32`. | **PARTIAL** |
| Non-empty data execution | The supplied instance returned zero rows, so value-level rendering remains unverified. | **UNVERIFIED** |

The offline fixture at `tests/Fixtures/LegacyReal/expected-shapes/djid-xmtm.shape.json` deliberately locks only non-sensitive table/column names and a minimum row count of zero. It permits the no-row sample while ensuring that `Master` name mapping and observed column names do not regress.

## Implementation consequence

`LegacyReportSchema:FirstResultSetTableName=Master` is an evidence-backed requirement for this definition family. `LegacyDatabaseTemplateProvider` decodes `Base64Utf8` storage before handing template text to a renderer. No FastReport smoke test has been run because a lawful FastReport package/license is not present in this gate.
