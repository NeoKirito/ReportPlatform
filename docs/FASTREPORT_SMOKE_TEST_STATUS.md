# FastReport Smoke Test Status

> **Status: `FASTREPORT COMPATIBILITY = SMOKE PASS` for the user-confirmed free FastReport Open Source runtime.** The smoke uses only official MIT-licensed NuGet packages and does not introduce commercial DLLs, license keys, trial bypasses, credentials, FRX bodies, SQL bodies, or patient identifiers into the repository.

## Runtime and isolation

| Concern | Result |
|---|---|
| Runtime | `FastReport.OpenSource` `2026.2.3` |
| PDF exporter | `FastReport.OpenSource.Export.PdfSimple` `2026.2.3` |
| Renderer isolation | `src/PEIS.Report.FastReport.OpenSource` implements Engine-owned `IFastReportRuntime`. |
| Composition | API selects it only with `ReportEngine:Renderer=FastReportOpenSource`; default remains `Stub`. |
| Report lifetime | A new `FastReport.Report` is created, prepared, exported, and disposed for every request. |
| CI gate | The real smoke is skipped unless **both** `REPORTPLATFORM_TEST_SQLSERVER=1` and `REPORTPLATFORM_TEST_FASTREPORT=1` are explicitly set with an approved private connection and fixture. |

## Real xmtm base-PDF smoke

The smoke calls the existing legacy path, not a recreated template: `LegacyPayloadReportResolver` → `LegacyDatabaseReportDefinitionProvider` → `LegacyDatabaseTemplateProvider` → `SqlServerReportDataProvider` → `OpenSourceFastReportRuntime`. The test uses the private approved sample only at runtime and retains no request identifiers or business-row values.

| Requirement | Result |
|---|---|
| `querytype=djwh`, `bbid=xmtm` resolution | **PASS** |
| `dbo.xt_bgdy_djwh_zzj` definition lookup | **PASS** |
| Base64 UTF-8 FRX decode | **PASS** |
| Non-empty `Master` registration | **PASS**: 1 row, 7 columns |
| `Master` exact data source name | **PASS** |
| `XMMC` SQL column / `Master.xmmc` FRX expression | **PASS** |
| `nl: System.Int32` data / `Int16` FRX dictionary declaration | **PASS** without conversion |
| FRX Load / Prepare | **PASS** |
| PDF pages / bytes / header | **1 / 45,365 / `%PDF-`** |
| Per-request report lifetime | **PASS** by implementation boundary; no static or singleton `Report` exists. |

## Measured compatibility baseline

This is a single one-page compatibility run, **not** a performance claim for a large medical report. Values below are the final measured run and are retained as a renderer baseline only.

| Stage | Elapsed |
|---|---:|
| DefinitionVersionCheck | 1 ms |
| DefinitionLoad | 6 ms |
| TemplateDecode | 0 ms |
| SqlQuery | 148 ms |
| RegisterData | 2 ms |
| FrxLoad | 45 ms |
| Prepare | 77 ms |
| PdfExport | 56 ms |
| Total | 383 ms |

## Watermark and comparison boundary

The smoke explicitly sets `WatermarkEnabled=false` for a base-PDF verification. The database FRX did not yield a committed watermark assertion, and no application overlay has been added. Therefore **`WATERMARK PIPELINE = UNVERIFIED`**. The old API PDF remains a transport-level compatibility reference; page-level or pixel-level old/new visual comparison was **NOT RUN** because no old PDF body is retained in the repository and this gate does not require byte-identical output.

## Artifact handling

The test writes only a private, ignored metadata file when the smoke script supplies `REPORTPLATFORM_TEST_FASTREPORT_EVIDENCE_PATH`. It records report id, definition table, decoded FRX hash, row count, fixed column names, page count, PDF size, PDF signature, watermark status, and stage timings. It never writes a PDF artifact, FRX content, SQL body, connection string, request payload, patient data, or license information.
