# Legacy Parameter Behavior

> **Evidence rule.** **CONFIRMED** is backed by an approved real database/API observation and an executable regression test. **PARTIAL** has a controlled observation but cannot yet isolate all historical branches. **UNVERIFIED** has no approved real fixture or old implementation evidence. This ledger does not claim byte-for-byte renderer compatibility.

## Current status

| Behavior | Status | Real evidence | Implemented boundary |
|---|---|---|---|
| `@name` ADO.NET parameter | **CONFIRMED** | The new provider executes parameter objects, and pre-existing unit tests cover `@tjh`. | Typed `SqlParameter`; no caller value concatenation. |
| `[name]` legacy placeholder | **CONFIRMED** | Real `xmtm.djsql` uses `[grtjgcjjgid]` and `[sfxmddid]`; the underlying procedure declares matching `varchar(max)` parameters. | `ILegacyQueryParameterBinder` changes matched bracket tokens to `@name` and supplies ANSI parameters. |
| Nested JSON lookup | **CONFIRMED** | The supplied successful old API request keeps both required names under `djh`. | Binder recursively indexes scalar members of preserved `LegacyPayload`. |
| `querytype=djwh` + `bbid` resolution | **CONFIRMED** | The supplied `bbid=xmtm` old request returns a PDF, while `xmtm` is a real `dbo.xt_bgdy_djwh_zzj.djid`. | `LegacyPayloadReportResolver` maps this confirmed payload family to the `djid` definition key. |
| `djid` only | **PARTIAL** | The supplied `djid=xmtm` variant returns a legacy JSON error, not a PDF. The response does not isolate whether definition lookup or downstream SQL behavior caused it. | No direct `djid` resolver rule is claimed. |
| `cxid` | **UNVERIFIED** | No approved valid `cxid` request or table relationship is available. | No resolver rule. |
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

A valid `cxid` request, a valid direct `djid` request, and an old-service DLL or approved publish package remain necessary before expanding resolver precedence or adding `${...}`/Regex behavior. Until that evidence is collected, the Real Legacy Compatibility Gate remains **NOT VERIFIED** rather than PASS.
