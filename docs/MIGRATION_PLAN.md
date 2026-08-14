# Migration plan

## Phase 0 — Freeze the old HTTP contract

Treat the existing PEIS call as a compatibility contract, not something to redesign during the engine rewrite.
Confirmed from the supplied legacy deployment:

- conventional route: `api/{controller}/{action}/{id}`;
- controller/action: `ReportsController.GetReportByJson(object data)`;
- compatibility URL: `POST /api/Reports/GetReportByJson`;
- request body: arbitrary legacy JSON object;
- result: direct PDF response/stream.

Capture several real production request bodies before final cutover. The new endpoint already accepts raw JSON so
unknown/legacy fields do not need to be renamed or discarded.

## Phase 1 — Implement exact legacy parameter semantics in the engine

Use `ReportRenderRequest.LegacyPayload` as the source of truth. Reproduce how the current code resolves `bbid`,
document/report definitions and template parameters. Do not make PEIS adopt a new typed request shape.

## Phase 2 — Engine instrumentation

Add timings for definition lookup, SQL, image loading, FRX load, Prepare, watermark, PDF export and response-ready.
Use the 20+ page / 20+ MB / ~50 s report as the main benchmark.

## Phase 3 — Performance work

Template cache, bounded parallel image resolution, image deduplication/downsampling, one-pass watermark/export,
render concurrency gate, reduced intermediate copies and large-report streaming.

## Phase 4 — PrintAgent

Deploy the Windows agent on selected terminals. Bind stable logical roles such as `A4_GUIDE` and `BARCODE` to the
installed physical printers. Different printer queues run in parallel; each single printer queue remains ordered.

## Phase 5 — B/S one-click business printing

Add a new business print operation (for example `REGISTRATION_PRINT`) that uses the same registration/report
parameters already available on the PEIS page, but automatically expands them into the guide-sheet and barcode
render jobs. Operators do not select printers on every click.

## Phase 6 — Dual run and cutover

For selected report IDs, send identical legacy request JSON to the old and new engines, compare PDF content/page
count/watermark, then switch the service address without changing PEIS request construction.
