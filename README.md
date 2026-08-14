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

The supplied legacy package is a compiled IIS deployment. It confirms the old public method and route, but it does
not include the newest watermark source/current FastReport development reference. `FastReportReportRenderer` is
therefore still the production seam to implement. The architecture deliberately keeps compatibility outside that
engine so performance work does not force PEIS callers to change.

See `docs/ARCHITECTURE.md`, `docs/PRINT_ROUTING.md`, `docs/PERFORMANCE_PLAN.md`, and `docs/MIGRATION_PLAN.md`.
