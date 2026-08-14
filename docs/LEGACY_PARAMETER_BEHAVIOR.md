# Legacy parameter behavior evidence

This document records what is known about the parameter boundary for `POST /api/Reports/GetReportByJson`. It is an evidence ledger, not a claim of byte-for-byte compatibility with a historical PEIS renderer.

> **Evidence rule.** A label of **CONFIRMED** means that the behavior is covered by the current code or an executed automated test. **STRONG EVIDENCE** means that source, configuration, or a controlled observation supports the conclusion but a real legacy database/FRX fixture has not yet validated it. **UNVERIFIED** means that no approved real fixture has been supplied.

| Status | Statement | Evidence |
|---|---|---|
| **CONFIRMED** | The compatibility adapter accepts an arbitrary JSON object and preserves a cloned copy of the complete object in `ReportRenderRequest.LegacyPayload`. | `src/PEIS.Report.Api/Compatibility/LegacyReportRequestAdapter.cs`; API contract tests. |
| **CONFIRMED** | `bbid`, `djid`, `cxid`, and `reportId` are detected case-insensitively only as a convenience to populate `ReportId`; preserving `LegacyPayload` does not depend on that inference. | `LegacyReportRequestAdapter`; source review. |
| **CONFIRMED** | The SQL Server data provider sends typed `DbParameter` values through `ILegacyQueryParameterBinder`; it does not concatenate request values into report SQL. | `LegacyDatabaseContracts.cs`; unit tests `Binder_*`. |
| **CONFIRMED** | A missing named parameter is reported as an explicit legacy database contract failure rather than silently omitted. | `LegacyDatabaseContractsTests.Binder_reports_missing_parameter_explicitly`. |
| **CONFIRMED** | A complete `LegacyPayload` is preferred over inferred typed values when binding known parameter names. | `LegacyDatabaseContractsTests.Binder_prefers_complete_legacy_payload_without_string_substitution`. |
| **STRONG EVIDENCE** | The legacy report definition can be configured to use normal ADO.NET `@name` parameter syntax because the production provider executes `SqlCommand` with parameter objects. | `SqlServerReportDataProvider` and `AdoNetLegacyQueryParameterBinder`. |
| **UNVERIFIED** | Historical placeholder forms such as `${name}`, `{name}`, positional `?`, FastReport expression substitution, `PrepareQuery`, or custom legacy DLL transformations. | No approved legacy FRX, SQL, DLL, or request fixture was supplied. |
| **UNVERIFIED** | Whether an individual `bbid`, `djid`, or `cxid` selects a definition directly or through a multi-table relationship. | The `xt_*` table names are known candidates only; real keys and relationships are not yet evidenced. |

## Confirmed implementation contract

`ReportRenderRequest` carries two representations of caller input. `Parameters` is a case-insensitive dictionary used by typed application code. `LegacyPayload` is the cloned raw JSON object accepted by the legacy-compatible HTTP endpoint. The latter is intentionally retained so that a confirmed legacy binding strategy can be implemented without discarding fields that the new typed contract does not yet recognize.

The binder produces ADO.NET parameters with explicit values rather than performing string replacement. This preserves the query-plan and injection-safety boundary for the currently supported `@name` model. A report SQL statement remains database-owned configuration; it is not treated as executable caller input.

## Strong evidence and limits

The current database provider is intentionally configured around a SQL Server `SqlCommand`. This supports normal named ADO.NET parameters after `ILegacyQueryParameterBinder` resolves values from the preserved payload. It does **not** prove the historical service used that syntax exclusively. In particular, a FastReport template may have applied its own expression rules after query execution; that behavior cannot be inferred from table names.

No legacy renderer DLL, FastReport package installation, FRX template, or database connection was available on the development workstation. Consequently, this repository contains no reverse-engineered claim about undocumented placeholder processing.

## Evidence collection procedure

Use an explicitly approved read-only account. First run `tools/sql/inspect-legacy-report-schema.sql` in SSMS or `PEIS.LegacyDbInspector inspect-schema` to record actual columns, keys, rowversion/update columns, and table presence. Then run `inspect-report` for one approved **non-patient report-definition identifier** and export FRX/SQL only when the environment owner authorizes the destination.

For each approved sample, retain outside source control: the sanitized original request JSON; the selected table and key path; SQL placeholder tokens; the parameter name, JSON type, and outcome; and the returned `DataSet` table/column shape. Convert only table names, column names, and minimum row counts into the JSON form defined by `tests/Fixtures/LegacyReal/DataSetShape.schema.json`.

## Decision gate

Do not add compatibility transformations, SQL string substitution, or FastReport-specific parameter handling until a sanitized real fixture identifies the required rule and an integration test demonstrates it against a read-only database. Until then, unsupported syntax must remain **UNVERIFIED**, not silently emulated.
