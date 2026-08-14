# LegacyReal fixtures

This directory establishes the **boundary** for evidence collected from a real legacy PEIS environment. The production database remains the source of truth. These files are only sanitized, non-production test contracts and diagnostics.

> Do not commit connection strings, passwords, access tokens, patient identifiers, patient data, raw legacy request payloads, FRX templates, report SQL, or API response bodies.

## Allowed committed files

`DataSetShape.schema.json` defines the permitted shape-only fixture format. A future sanitized fixture may contain only a returned `DataTable` name, its column names, and a minimum row count. It must contain no values or SQL.

## Ignored real evidence

The accompanying `.gitignore` ignores all real fixture content by default. When the inspection toolkit writes FRX or SQL on explicit request, keep it in a secured, local diagnostic location outside version control, such as `artifacts/legacy-inspection/`.

## Enabling real integration tests

The suite is skipped unless `REPORTPLATFORM_TEST_SQLSERVER=1` (or `true`/`yes`) is set. It then requires an explicitly supplied, read-only `REPORT_DATABASE__CONNECTIONSTRING`; it never discovers a database on its own. The minimum safe invocation is:

```powershell
$env:REPORTPLATFORM_TEST_SQLSERVER = '1'
$env:REPORT_DATABASE__CONNECTIONSTRING = '<approved-read-only-connection-string>'
dotnet test tests/PEIS.Report.Infrastructure.SqlServer.Tests/PEIS.Report.Infrastructure.SqlServer.Tests.csproj
```

To exercise a report definition, set `REPORTPLATFORM_TEST_REPORT_ID` to an approved **non-patient report-definition identifier** and configure mapping variables only when real evidence differs from the documented defaults. To check dataset shape, set `REPORTPLATFORM_TEST_DATASET_SHAPE` to an approved sanitized JSON file conforming to `DataSetShape.schema.json`.
