# PEIS Report Benchmarks

This directory defines the performance regression protocol. It does not claim any target has passed until it is executed with the appropriate fixture and records measured values.

| Fixture | Purpose | Required inputs | Verification status |
|---|---|---|---|
| A — guide sheet | Ordinary A4 registration/guide report | Sanitized request and representative FRX | NOT VERIFIED |
| B — barcode | Small label/barcode artifact | Sanitized request and representative FRX | NOT VERIFIED |
| C — image-heavy | At least 20 pages with repeated and large images; historical reference was approximately 20 MB and 50 seconds | Sanitized request, FRX, image corpus, and expected visual output | NOT VERIFIED |
| Synthetic C | Deterministic stress scenario that simulates page count, repeated images, and large images | Generated fixture only | NOT A PRODUCTION BENCHMARK |

## Required measurements

Each run must record `DefinitionLoad`, `TemplateLoad`, `SqlQuery`, `Rows`, `ImageDiscovery`, `ImageResolve`, `ImageCount`, `ImageBytes`, `FrxLoad`, `RegisterData`, `Prepare`, `Pages`, `Watermark`, `PdfExport`, `PdfBytes`, `ArtifactWrite`, and `Total`. The API diagnostics endpoint exposes the per-request observation captured by the renderer; production logging integration should export the same fields structurally.

## Target interpretation

| Workload | Engineering target | Result before FastReport/FRX fixture is supplied |
|---|---:|---|
| Small/simple | < 2 seconds | NOT VERIFIED |
| Normal | < 5 seconds | NOT VERIFIED |
| 20+ page image-heavy | < 10 seconds | NOT VERIFIED |

> Synthetic timings establish repeatability and regressions in the deterministic pipeline only. They must not be represented as FastReport or production performance results.
