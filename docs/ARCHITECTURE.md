# PEIS Report Platform — Architecture

## Goal

One C#/.NET report and printing platform serves PEIS desktop and PEIS B/S:

- Input: report/template id + business parameters.
- Output: PDF stream.
- Keep existing FRX assets and the database-driven report-definition model.
- Support watermarking in the production renderer.
- B/S supports **one-click business printing without selecting printers every time**.
- One business action may generate multiple different documents, such as an A4 guide sheet and barcode labels.
- Each document is automatically routed to a logical printer role on the workstation.

## Core model: action -> documents -> printer roles

Example:

```text
B/S: REGISTRATION_PRINT
        │
        ├── guide-sheet
        │     ReportId: GUIDE_A4
        │     Role:     A4_GUIDE
        │     └──────────────> HP LaserJet (configured on REG-01)
        │
        └── barcode
              ReportId: REG_BARCODE
              Role:     BARCODE
              └──────────────> TSC/Zebra (configured on REG-01)
```

PEIS business code knows only `REGISTRATION_PRINT` and `REG-01`. It does not know Windows printer names.

## Components

```text
PEIS Desktop ------------------------------------┐
                                                │
PEIS B/S -- POST /api/print/actions ------------+----> PEIS.Report.Api
         actionCode + stationId + parameters     │          │
                                                 │          ├─ PrintScenarioCatalog
                                                 │          │    action -> document list
                                                 │          │
                                                 │          ├─ Report.Engine
                                                 │          │    ├─ FRX/SQL/cache
                                                 │          │    ├─ FastReport
                                                 │          │    └─ PDF/watermark
                                                 │          │
                                                 │          └─ artifact(s)
                                                 │                  │
                                                 │               SignalR
                                                 │                  ▼
                                                 └--------> Windows PrintAgent
                                                              station REG-01
                                                               │       │
                                                        A4_GUIDE       BARCODE
                                                               │       │
                                                               ▼       ▼
                                                            A4 printer label printer
```

## Why this matches the real PEIS requirement

The user action is not “print this PDF to several printers”. It is “perform one business print operation”.
That operation can produce several **different** outputs:

1. A4 guide sheet.
2. Barcode labels.
3. Later: receipt, consent form, wristband, report cover, etc.

The action definition is configurable. PEIS therefore does not gain hard-coded printer logic as requirements grow.

## Printer binding

PrintAgent is configured once per workstation:

```json
{
  "StationId": "REG-01",
  "PrinterBindings": {
    "A4_GUIDE": "HP LaserJet Pro M404",
    "BARCODE": "TSC TE244"
  }
}
```

Changing a physical printer only changes this binding. No PEIS deployment and no report-template modification is required.

Recommended production ownership:

- **Scenario definition** (which documents to print): central server/database configuration.
- **Physical printer binding** (which device handles each role): local PrintAgent configuration or a central admin page pushed to the agent.

## Workstation identity

The normal B/S request includes a stable `StationId` such as `REG-01`.

Preferred ways to provide it, in order:

1. PEIS workstation/terminal configuration injected into the logged-in session.
2. One-time browser/workstation pairing stored locally and managed by administrators.
3. IP-based inference only as a fallback; proxies/NAT make it less reliable.

Users never choose the printer for each print operation.

## Parallelism semantics

For `REGISTRATION_PRINT`:

1. Resolve both document definitions.
2. Render guide sheet and barcode concurrently when safe.
3. Store each resulting artifact once.
4. Dispatch one batch to the workstation PrintAgent.
5. Agent downloads each distinct artifact once.
6. A4 printer queue and barcode printer queue run independently, so both can start at the same time.
7. Jobs targeting the **same** physical printer remain serialized to avoid driver/spooler contention.

For heavy FastReport production workloads, the renderer will also use a global bounded concurrency gate.

## Browser boundary

A normal browser should not be responsible for enumerating Windows printers or silently selecting physical devices.
The local Windows PrintAgent is the boundary to Windows printing and keeps an outbound SignalR connection to the API.

This avoids browser print dialogs and avoids coupling PEIS pages to local printer APIs.

## Desktop integration

Desktop and B/S share the same central report engine.

- Preview/download: call `/api/reports/pdf`.
- Business printing: call `/api/print/actions`.
- Desktop does not embed a second copy of the report engine.

## Production report engine seam

`IReportRenderer` deliberately hides FastReport from the web/printing layers. The production adapter should implement:

- report definition cache (FRX/SQL/parameters/version hash),
- thin SQL Server/Oracle data provider,
- bounded concurrent image resolver + URL/content deduplication,
- fresh FastReport `Report` instance per render,
- watermark during report rendering where possible,
- one `Prepare`, one PDF export,
- A4/label/screen/archive export profiles,
- per-stage performance telemetry.
