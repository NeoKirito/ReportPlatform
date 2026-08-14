# FastReport Smoke-Test Status

> **Status: `FASTREPORT_DEPENDENCY_BLOCKED`.** This gate does not download, restore, copy, decompile, or otherwise introduce FastReport without a user-supplied lawful package and license entitlement.

## Dependency inventory

A filename-only inventory was run against the local FastReport-relevant program directories, the current user's NuGet cache, the ReportPlatform workspace, the adjacent legacy Java workspace, and the preserved `fix-web-peis-ui-v2-31ccde7.zip` archive. It found **zero** FastReport DLL or NuGet-package matches and **zero** FastReport filename matches in the archive. The raw inventory is stored only in the ignored private evidence directory.

| Requirement | Current result | Gate decision |
|---|---|---|
| Licensed FastReport package/DLL | Not supplied and not found in the inventoried locations. | **BLOCKED** |
| Legal entitlement / license material | Not supplied. | **BLOCKED** |
| Non-empty `Master` data-provider fixture | Confirmed by a read-only integration test with a minimum row count of one. | Ready when runtime is lawful and available. |
| Base64 UTF-8 FRX decoding | Covered by offline contract test. | Ready when runtime is lawful and available. |
| Renderer-specific `nl` coercion (`Int32` source versus FRX `Int16`) | Cannot be asserted without FastReport runtime. | **PENDING** |
| Renderer-specific `XMMC`/`xmmc` field resolution | The provider boundary is covered by case-insensitive `DataTable` behavior; FastReport behavior needs smoke verification. | **PENDING** |

## Smoke-test scope once unblocked

The future smoke test will be intentionally narrow. It will construct a **new Report instance per request**, load the decoded `xmtm` FRX, register the read-only `Master` `DataTable` under exactly that name, set only the existing non-sensitive template parameters, prepare the report, and export a disposable PDF artifact for structural validation. It will not test throughput, caching, printing, or multi-request reuse. FastReport types must remain isolated from Controller, Compatibility, Printing, and Contracts layers.

No dependency or smoke-test code is committed while this status is blocked.

## Current real-smoke record

| Item | Result |
|---|---|
| Fixture route | `querytype=djwh`, `bbid=xmtm` — **READY** at the resolver, definition, template-decoding, and read-only SQL-data-provider boundaries. |
| Input data | An approved private fixture returns `Master` with at least one row — **READY**. Its identifiers remain only in ignored runtime files. |
| FRX source | The database-owned Base64 UTF-8 template is decoded at runtime — **READY**. Neither its body nor a copy is committed. |
| FastReport `Report` construction | **NOT RUN** — no lawful runtime assembly is available. |
| FRX load / `RegisterData` / `Prepare` | **NOT RUN** — no lawful runtime assembly is available. |
| `XMMC`/`xmmc` and `nl` compatibility | **NOT RUN** at the renderer boundary. |
| PDF export / `%PDF-` / pages / bytes | **NOT RUN** — no PDF was produced by the new renderer. |
| Timing baseline | **NOT RUN** — renderer stages cannot be measured without the runtime. |
| Old API comparison / watermark pipeline | **NOT RUN** for this gate. The base database and template contract is documented separately. |
