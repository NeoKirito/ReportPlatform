# Legacy Request Resolution Contract

> **Purpose.** This document closes only the request-ID evidence gathered from the approved legacy API and the read-only `TJXT3.0` metadata inspection. It is not a claim of byte-for-byte renderer equivalence. No request bodies, credentials, patient rows, stored SQL, or FRX bodies are stored in this document.

## Evidence classification

| Status | Meaning |
|---|---|
| **CONFIRMED** | A controlled runtime observation and an executable regression test support the behavior. |
| **PARTIAL** | A controlled runtime observation exists, but it does not prove every historical branch or a successful public contract. |
| **OUT OF CURRENT PRODUCTION CONTRACT** | Read-only production metadata provides no current report-definition relationship. This can be reopened only with an approved historical request or implementation artifact. |
| **UNVERIFIED** | No approved runtime evidence or old implementation artifact supports the behavior. |

## ID-family decision table

| Incoming payload shape | Observed old-service outcome | Status | New-platform boundary |
|---|---|---:|---|
| `querytype=djwh`, valid `bbid=xmtm` | PDF response. The read-only definition table maps `xmtm` to its `djid` key. | **CONFIRMED** | Resolve `bbid` as the definition key. |
| `querytype=djwh`, valid `bbid=xmtm`, unknown `djid` | PDF response remains successful. | **CONFIRMED** | `bbid` has precedence for the confirmed `djwh` family; the additional `djid` is ignored for selection. |
| Direct `djid=xmtm` on the investigated branch | JSON error rather than a PDF. The original GBK payload decodes to a SQL ambiguity for `grtjgcjjgid`. | **PARTIAL** | Do not add a direct-`djid` resolver branch. The observation indicates downstream execution, not a supported public success contract. |
| `djid=xmtm` without `querytype` | Same non-PDF response class as explicit `querytype=djid`. | **PARTIAL** | Fall back to the typed `ReportId`; do not infer a default direct-ID mode. |
| `querytype=djid`, `djid=xmtm` | Same non-PDF response class as the no-`querytype` case. | **PARTIAL** | Fall back to the typed `ReportId`; do not infer a direct-ID mode. |
| Any `cxid` route | No valid request fixture was supplied. Read-only metadata found no exact `cxid` object or column name, and no `cx`-named report-definition family. | **OUT OF CURRENT PRODUCTION CONTRACT** | No `cxid` resolver rule exists. |

The controlled multi-ID result limits the implementation to a narrow, observable rule: when `querytype` is `djwh` and `bbid` is non-empty, the definition ID is the `bbid` value. It does **not** claim global precedence between all possible legacy fields.

## Executable regressions

| Test | Contract locked |
|---|---|
| `Resolver_prefers_bbid_when_querytype_is_djwh` | The confirmed `djwh`/`bbid` path overrides a conflicting secondary `djid`. |
| `Resolver_uses_request_reportid_when_no_matching_payload_pattern` | Unverified ID families retain the typed-request fallback instead of gaining implicit rules. |
| `Binder_leaves_unresolved_bracket_tokens_unchanged` | Bracketed SQL identifiers without a matching scalar payload value remain untouched. |
| `DataTable_column_lookup_is_case_insensitive` | The `XMMC` versus `xmmc` lookup boundary follows the case-insensitive .NET `DataColumnCollection` behavior. |

## Current closure boundary

The request-resolution implementation intentionally preserves the full legacy payload but recognizes exactly one selection pattern: `querytype=djwh` with `bbid`. This is the smallest evidence-backed implementation and preserves the existing `POST /api/Reports/GetReportByJson` compatibility path without transferring an unproven `djid` or `cxid` interpretation into the new platform.

A successful direct-`djid` PDF fixture, a historical `cxid` request or old implementation artifact, or an approved old publish package could change this boundary. Until then, direct `djid` remains **PARTIAL**, `cxid` is **OUT OF CURRENT PRODUCTION CONTRACT**, and no broad multi-ID precedence rule is claimed.

## Related evidence

The database and FRX evidence ledger is maintained in [REAL_LEGACY_DATABASE_EVIDENCE.md](REAL_LEGACY_DATABASE_EVIDENCE.md). Parameter token behavior is recorded in [LEGACY_PARAMETER_BEHAVIOR.md](LEGACY_PARAMETER_BEHAVIOR.md), while the `Master` result-set contract is tracked in [REAL_FRX_DATA_CONTRACT.md](REAL_FRX_DATA_CONTRACT.md).
