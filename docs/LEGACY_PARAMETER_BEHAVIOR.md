# Legacy Parameter Behavior

> **Evidence rule.** **CONFIRMED** is backed by an approved real database/API observation and an executable regression test. **PARTIAL** has a controlled observation but cannot yet isolate all historical branches. **UNVERIFIED** has no approved real fixture or old implementation evidence. This ledger does not claim byte-for-byte renderer compatibility.

## Current status

| Behavior | Status | Real evidence | Implemented boundary |
|---|---|---|---|
| `@name` ADO.NET parameter | **CONFIRMED** | The new provider executes parameter objects, and pre-existing unit tests cover `@tjh`. | Typed `SqlParameter`; no caller value concatenation. |
| `[name]` legacy placeholder | **CONFIRMED** | Real `xmtm.djsql` uses `[grtjgcjjgid]` and `[sfxmddid]`; the underlying procedure declares matching `varchar(max)` parameters. | `ILegacyQueryParameterBinder` changes matched bracket tokens to `@name` and supplies ANSI parameters. |
| Nested JSON lookup | **CONFIRMED** | The supplied successful old API request keeps both required names under `djh`. | Binder recursively indexes scalar members of preserved `LegacyPayload`. |
| `querytype=djwh` + `bbid` resolution | **CONFIRMED** | The supplied `bbid=xmtm` old request returns a PDF, while `xmtm` is a real `dbo.xt_bgdy_djwh_zzj.djid`. | `LegacyPayloadReportResolver` maps this confirmed payload family to the `djid` definition key. |
| `djid` only | **PARTIAL** | The controlled `djid=xmtm` request reaches a legacy JSON error whose GBK-decoded message is `列名 'grtjgcjjgid' 不明确` (column name is ambiguous), rather than returning a PDF. This is evidence of a downstream SQL ambiguity, not proof of a public direct-ID success path. Requests without `querytype` and with `querytype=djid` both produce the same non-PDF response class. | No direct `djid` resolver rule is claimed. |
| `cxid` | **OUT OF CURRENT PRODUCTION CONTRACT** | A read-only `sys.objects`/`sys.columns` search found zero exact `cxid` object or column names. The 46 broader `cx` name matches are examination-item tables, constraints, and maintenance procedures; none is a report-definition family. | No resolver rule. Re-open only with an approved historical request or old implementation artifact. |
| `${name}` placeholder | **UNVERIFIED** | No observed real SQL contained this syntax. | Not transformed. |
| `PrepareQuery`, `Regex`, arbitrary `Replace` | **UNVERIFIED** | No old DLL, PDB, XML documentation, or approved publish package was supplied for static analysis. | Not emulated. |
| Positional `?`, `{name}`, FastReport expression substitution | **UNVERIFIED** | No real SQL/FRX evidence in the approved sample requires these behaviors. | Not emulated. |

## Confirmed `xmtm` procedure boundary

The database-owned `djsql` definition invokes `dbo.tjxt_fastreportgetTxmxx` with two bracketed tokens. The compatible binder transforms only these recognized scalar tokens into ADO.NET parameter placeholders. The result remains a parameterized command; it does not replace a payload value into SQL text.

| Legacy payload path | Stored-procedure parameter | Database type | Binding type |
|---|---|---|---|
| `djh.grtjgcjjgid` | `@grtjgcjjgid` | `varchar(max)` | `DbType.AnsiString` |
| `djh.sfxmddid` | `@sfxmddid` | `varchar(max)` | `DbType.AnsiString` |

The real read-only integration test loads the `xmtm` definition, converts its two placeholders, executes the configured SQL, and checks the sanitized `Master` table shape. The fixture and tests contain no request values or patient rows.

## Safety constraints

The binder is intentionally isolated in `ILegacyQueryParameterBinder`. It is responsible for token syntax and parameter values; `SqlServerReportDataProvider` remains responsible for command execution and data-set materialization. A bracketed token is changed only when the preserved payload supplies a same-named scalar. Other bracketed SQL remains untouched, avoiding a broad string-replacement rule.

## Required next evidence

A valid direct `djid` request that returns a PDF, a historical `cxid` request or implementation artifact if that family must be restored, and a licensed FastReport runtime remain necessary before claiming full legacy closure. The approved non-empty `Master` data-provider contract is now verified by a read-only integration test. Until the remaining independent gates are satisfied, the Real Legacy Compatibility Gate remains **NOT VERIFIED** rather than PASS.
