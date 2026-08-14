# Integration Blockers and Gates

This document records external inputs that were not present on the development workstation. These items do **not** block static implementation, deterministic tests, API-contract work, queue behavior, or documentation.

| Missing item | Why it is needed | Work that continues without it | Exact artifact required later |
|---|---|---|---|
| .NET SDK 10.0.100 | The solution pins this SDK in `global.json`. | Source review, static checks, test design, and documentation. | Installer or approved SDK policy change. |
| Licensed FastReport package and license | Render a production FRX and export a faithful PDF. | Interfaces, cache, timing, concurrency control, deterministic renderer, and contract tests. | Approved NuGet/package source, license configuration, and version guidance. |
| Representative FRX templates | Validate template loading, data registration, image prefetch behavior, watermark placement, and export profiles. | Template-provider contract and deterministic fixture design. | Sanitized guide-sheet, barcode, and image-heavy FRX templates. |
| Read-only legacy database access and schema | Implement and validate SQL Server definitions/data mappings; Oracle remains optional. | Repository interfaces and deterministic data provider. | Read-only endpoint, credential delivery path, table definitions, and sample report IDs. |
| Legacy production requests and current service access | Confirm all legacy parameter semantics and PDF/watermark behavior. | Raw-payload preservation and compatibility endpoint tests. | Sanitized request/response fixtures and, if permitted, controlled dual-run access. |
| Slow image-heavy PDF fixture | Measure actual size, rendering time, page count, and image behavior. | Synthetic benchmark protocol and telemetry instrumentation. | Source JSON/FRX/images or a reproducible sanitized fixture. |
| Approved physical printer test procedure | Validate printer drivers, paper dimensions, spooler behavior, and barcode output. | DryRun, role mapping, per-printer serialization, retry policy, and queue tests. | Approved A4/barcode test documents, station mapping, and supervised test window. |

> No blocker above is treated as a completed integration. FastReport output, database queries, production performance targets, and physical printer behavior remain **NOT VERIFIED** until the stated artifact is provided.
