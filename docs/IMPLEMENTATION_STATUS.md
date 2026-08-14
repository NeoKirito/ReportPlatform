# Production Increment Status

This status is intentionally evidence-based. A feature is marked **DONE** only when its code-level boundary and deterministic behavior are implemented; it is marked **BLOCKED** or **PARTIAL** where a missing external artifact prevents a truthful integration claim.

| Area | Status | Delivered increment | Verification boundary |
|---|---|---|---|
| Legacy API compatibility | DONE | `POST /api/Reports/GetReportByJson` remains controller/action routed, accepts an arbitrary JSON object, preserves `LegacyPayload`, and returns direct `application/pdf`. | Exact legacy parameter semantics and visual PDF equivalence require sanitized production fixtures. |
| Report pipeline | DONE | Definition, template, data-provider, export-profile, image-resolver, metrics, and render-gate boundaries are present. | Real FRX and FastReport execution are blocked. |
| Definition/template cache | DONE | Immutable metadata cache is single-flight, observable, and invalidatable; it never shares a mutable report object. | Database change-token or `UpdatedAt` invalidation requires the legacy schema. |
| Data provider | PARTIAL | Clear `IReportDataProvider` boundary and deterministic provider are supplied. | SQL Server schema/connection and optional Oracle provider are blocked. |
| Image pipeline | DONE | `ImageResolver` reuses `HttpClient`, deduplicates URLs, bounds concurrency, applies timeouts, and returns cache/failure/byte metrics. | FRX image discovery and real output validation are blocked. |
| FastReport integration | BLOCKED | An isolated `IFastReportRuntime` adapter contract and an explicit unavailable-runtime gate prevent accidental replacement of FRX technology. | Approved package, license, FRX, and data registration rules are required. |
| Watermark | PARTIAL | `WatermarkOptions` and render-stage boundary are retained. | Current legacy watermark source/behavior is required before enabling real behavior. |
| PDF export profiles | DONE | `legacy`, `screen`, `print-a4`, `label`, and `archive` profiles are normalized with documented intent. | FastReport-specific JPEG/font/streaming knobs require its approved API and real fixtures. |
| Performance metrics | DONE | Required stage timings and scalar counters are captured per render and exposed by an internal diagnostic endpoint. | Real timing and target compliance are NOT VERIFIED. |
| Render concurrency | DONE | A process-wide configurable semaphore gate exposes active/queued state and defaults to two renders. | Production tuning requires real load testing. |
| Print action | DONE | `REGISTRATION_PRINT` maps independent guide and barcode documents to logical roles, with no physical printer name in normal B/S input. | Live dispatch needs a connected agent with approved printer bindings. |
| PrintAgent | PARTIAL | SignalR registration/heartbeat/reconnect remains in place; DryRun and command backends are available. | Windows service packaging and supervised endpoint testing remain pending. |
| Printer mapping/queue | DONE | Role bindings stay workstation-local; each physical printer is serial, different printers may run concurrently, and retries are bounded. | Physical A4 and barcode output is NOT VERIFIED. |
| Automated tests | DONE | Engine, API compatibility/routing, idempotency, agent queue serialization, parallelism, and retry test projects are included in the solution. | `dotnet test` is blocked locally by the missing target SDK. |

## Explicit non-claims

No real FastReport PDF, legacy report equivalence, database query, production performance target, Windows service installation, or physical print output is claimed as passed. The associated inputs and next steps are listed in [BLOCKERS.md](BLOCKERS.md) and [ENVIRONMENT_REPORT.md](ENVIRONMENT_REPORT.md).
