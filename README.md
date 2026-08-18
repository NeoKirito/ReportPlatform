# PEIS.ReportPlatform

C#/.NET 10 report/PDF and B/S silent-printing platform for PEIS.

## Compatibility first

The new engine is allowed to change internally, but existing PEIS report callers should not have to migrate.
The legacy IIS package exposes `ReportsController.GetReportByJson(object data)` under the conventional route
`api/{controller}/{action}/{id}`. The new API therefore keeps this public compatibility endpoint:

```http
POST /api/Reports/GetReportByJson
Content-Type: application/json
```

**Request body: keep using the exact legacy JSON object.** The compatibility controller accepts raw JSON rather
than forcing callers into a new `ReportId + Parameters` contract. The complete payload is preserved as
`LegacyPayload` and passed into the new report engine so the final FastReport implementation can reproduce the
old parameter semantics exactly.

Response remains a direct PDF response (`application/pdf`), not a JSON wrapper.

The new typed render endpoint exists only for diagnostics/new integrations:

```http
POST /internal/reports/pdf
```

It is not the PEIS migration contract.

## Printing requirement

One B/S business button can automatically print different documents to different physical printers without asking
the operator to choose a printer each time.

Example `REGISTRATION_PRINT`:

- A4 guide sheet -> logical printer role `A4_GUIDE` -> workstation's configured A4 printer.
- Barcode labels -> logical printer role `BARCODE` -> workstation's configured label printer.

Printer selection is installation/configuration data, not normal PEIS business input.

## Projects

- `PEIS.Report.Contracts` — shared report/print contracts.
- `PEIS.Report.Engine` — FastReport rendering boundary and performance pipeline.
- `PEIS.Report.Api` — legacy-compatible PDF API + printing orchestration + SignalR.
- `PEIS.PrintAgent` — Windows resident print agent with printer-role bindings and per-printer queues.

## Legacy-compatible report flow

```text
PEIS existing call
    POST /api/Reports/GetReportByJson
    original JSON body
              |
              v
Legacy compatibility controller
              |
              v
LegacyReportRequestAdapter
  - keeps complete raw JSON
  - does not rename/remove legacy fields
              |
              v
New Report.Engine
  - definition/template cache
  - data query
  - image pipeline
  - FastReport Prepare
  - watermark
  - PDF export
              |
              v
application/pdf stream
```

## B/S silent printing

The printing API is new functionality and is separate from the legacy PDF compatibility contract. A business action
such as `REGISTRATION_PRINT` expands into A4 + barcode documents and routes each document by logical printer role.

```json
POST /api/print/actions
{
  "actionCode": "REGISTRATION_PRINT",
  "stationId": "REG-01",
  "parameters": {
    "tjh": "TJ202608140001"
  }
}
```

The final PEIS integration can reuse the same legacy business parameter object internally; only the print action and
workstation identity are new concepts because the old service did not provide silent multi-device printing.

## Current implementation boundary

The legacy `djwh + bbid` route is now evidenced against the supplied SQL Server source: the service resolves the
confirmed definition table, decodes its Base64 UTF-8 FRX, supplies the `Master` data set, and renders a base PDF with
`FastReport.OpenSource` plus the official PdfSimple exporter. The public compatibility controller remains outside the
renderer so PEIS callers do not need to change their JSON contract.

The current evidence is intentionally narrower than a full production acceptance claim: application-level watermark
behavior, old/new visual PDF equivalence, production-load targets, Windows Service packaging, and physical printer
output still require site approval and on-site validation.

For practical startup, configuration, security, test, printing, deployment, and rollback steps, read
[`docs/USAGE_AND_DEPLOYMENT_GUIDE.md`](docs/USAGE_AND_DEPLOYMENT_GUIDE.md). Supporting evidence remains in
`docs/ARCHITECTURE.md`, `docs/PRINT_ROUTING.md`, `docs/FASTREPORT_SMOKE_TEST_STATUS.md`, and
`docs/LEGACY_DATABASE_CONTRACT.md`.
