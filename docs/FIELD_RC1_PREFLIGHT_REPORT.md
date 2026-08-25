# ReportPlatform Field RC1 Preflight Report

**Repository:** `NeoKirito/ReportPlatform`

**Base main:** `760593dcc06f871bbf9c716d28a746100ffe9f59`

**Branch:** `release/field-rc1`

**RC1 implementation commit:** `6fa646a4fcef142ef7bcc42ff63ef6aaabd40b8b`

**Report scope:** controlled field RC1 packaging and preflight readiness. This report does not repeat the prior production-readiness architecture work, and it does not certify patient data, production SQL writes, physical printing, historical-report equivalence, hospital network behavior, or visual acceptance.

## Conclusion

> **READY_FOR_FIELD_RC1**

The RC1 code gates, release build, package hygiene, controlled DryRun component path, and Field Preflight implementation are ready for a Windows pilot under the supplied deployment procedure. The delivered package begins with `PrintBackend:Mode = DryRun`; no command-print backend, spooler, printer driver, physical printer, hospital network scan, production database write, patient artifact, or real SQL/FastReport connection was used in this work.

## Git State

| Item | Result |
|---|---|
| Baseline remote `main` | `760593dcc06f871bbf9c716d28a746100ffe9f59` |
| Working branch | `release/field-rc1` |
| RC1 implementation commit | `6fa646a4fcef142ef7bcc42ff63ef6aaabd40b8b` |
| Ahead / behind before report commit | `1 / 0` relative to `origin/main` |
| Worktree before report creation | Clean after implementation commit; generated `publish/` and `release/` outputs are ignored |
| RC1 change | `feat: prepare controlled field rc1` |

## Automated Gates

| Gate | Result | Evidence and boundary |
|---|---|---|
| Restore | PASS | `dotnet restore PEIS.ReportPlatform.sln` completed. |
| Release build | PASS | `dotnet build PEIS.ReportPlatform.sln -c Release --no-restore` completed. |
| Tests | PASS | **46 passed, 0 failed, 6 skipped**; the six skips are approved-gated real legacy SQL/FastReport tests and were not replaced with guessed connections. |
| Local DryRun E2E | PASS | New cross-platform test uses the unmodified PrintAgent `DryRunPrintBackend` and `PrinterQueueManager` sources together with the real SQLite `PrintJobStateStore` and idempotency store. It verifies synthetic job persistence, `Queued → Dispatched → Downloading → Printing → Completed`, restart recovery, same-key replay to the same JobId, and no duplicate target. **NO PHYSICAL PRINTING OCCURRED.** |
| SQLite persistence smoke | PASS | The test creates, writes, disposes, reopens and reads a temporary SQLite database only. |
| Artifact security smoke | PASS | The Field Preflight implements synthetic minimal-PDF storage, GUID names, cleanup retaining active artifacts, agent-bound signature validation, wrong-agent rejection, and expired-signature rejection. Existing artifact lifecycle/security tests remain green. |
| Field Preflight | PASS | `Test-FieldPreflight.ps1` supports text and `-Json`, does not emit secret values, validates Production secret presence, HTTPS, paths, AgentId preservation, StationId, bindings, backend constraints, temporary SQLite and synthetic artifact behavior. Windows PowerShell parser validation passed for preflight, install, uninstall and package scripts. A no-asset execution smoke safely returned structured `HOLD`, as designed; full production execution is deliberately deferred to the field machine after its approved local configuration and printer installation are present. |
| Publish | PASS | `publish/report-api/` was generated as `win-x64` framework-dependent; `publish/print-agent/` was generated as `win-x64` self-contained. Both primary binaries were verified. |
| RC ZIP | PASS | `release/ReportPlatform_RC1_6fa646a_win-x64.zip` was generated and inspected for required public assets and prohibited runtime artifacts. |
| PDF comparison tool | PASS | `tools/compare_pdf_visual.py` was exercised with synthetic PDFs, producing page count, dimensions, rendered hashes, pixel-difference JSON and an optional difference image. This is a **tool smoke only**, not an old/new PDF visual-equivalence conclusion. |
| Dependency scan | PASS | `dotnet list PEIS.ReportPlatform.sln package --vulnerable --include-transitive` reported no vulnerable packages. |
| Secret and artifact scan | PASS | Production paths and package contents contained no high-confidence secret, production connection string, PHI, PDF/FRX, SQLite runtime database, `publish/`, `release/`, `.runtime/`, `bin/` or `obj/` tracked artifact. |
| `git diff --check` | PASS | No whitespace errors at final code gate. |

## Field Preflight Coverage

The preflight script performs only configuration inspection, controlled directory probes, and temporary synthetic data checks. It does not install software, modify production configuration, modify system networking or firewall settings, change security policy, invoke a printer command, write a database, or create/rotate AgentId.

| Preflight area | Behavior |
|---|---|
| API runtime | Verifies published API DLL, JSON format, writable runtime and artifact paths, SQLite parent path, valid paths and minimum free disk space. |
| Production security | Requires non-empty, non-placeholder `PrintAgentSecurity:RegistrationToken`, `InternalApiSecurity:AccessToken`, and `ArtifactAccess:SigningKey`; reports only `PRESENT` or `MISSING`. |
| HTTPS | Requires HTTPS API and Agent URLs in Production mode; HTTP is permitted only for `Development` or `LocalDryRun`. |
| Agent identity | Verifies `%ProgramData%\PEIS\PrintAgent` write capability; existing `agent-id.txt` is reported as `PRESENT` without reading or changing it. |
| Station and bindings | Validates StationId syntax; checks installed Windows printers by logical role without printing. |
| Print backend | Reports `SAFE_FOR_PREFLIGHT` for DryRun; validates Command executable path, existence, shell/script-host exclusion and placeholders without execution. |
| Persistence and artifacts | Uses only temporary database and synthetic PDF roots; cleans them after the check. |
| JSON | Emits safe status, detail and final fields without tokens, connection strings or patient values. |

## RC1 Assets

The generated directory is `release/ReportPlatform_RC1_6fa646a/`, with `report-api/`, `print-agent/`, `scripts/`, `docs/` and `config-templates/`. Its ZIP is `release/ReportPlatform_RC1_6fa646a_win-x64.zip`. The package contains only software dependencies, scripts, placeholder-only templates and public documentation.

The new `config-templates/appsettings.Production.template.json` and `config-templates/print-agent.appsettings.Production.template.json` use placeholders only. The new `scripts/Uninstall-PrintAgentAutoStart.ps1` removes the scheduled task and stops the agent while preserving AgentId/local state by default; `-RemoveLocalState` is the explicit opt-in deletion path. It does not remove printers, drivers, PEIS files, database content, network settings or security policy.

## Capability Result

| Capability | Result |
|---|---|
| Report Engine | READY |
| Print Persistence | READY |
| PrintAgent | READY |
| Artifact Security | READY |
| RC Package | READY |
| Local DryRun | READY |
| Physical Printing | FIELD_REQUIRED |
| Legacy Full Compatibility | FIELD_REQUIRED |

## Remaining Field Gates

The following must remain **FIELD_REQUIRED** and cannot be promoted by this RC1: real A4 printing, real barcode printing, barcode scanning, Chinese font output, watermark appearance, all historical FRX cases, old/new PDF human equivalence, large real-report load, hospital TLS/network behavior, paper jams, and paper-out handling. The real legacy SQL/FastReport suite remains **SKIPPED — APPROVED CONNECTION NOT PRESENT** because `REPORTPLATFORM_TEST_SQLSERVER=1`, `REPORTPLATFORM_TEST_FASTREPORT=1`, and an approved read-only connection were not supplied.

Follow [FIELD_RC1_DEPLOYMENT_GUIDE.md](FIELD_RC1_DEPLOYMENT_GUIDE.md) in order: package deployment, approved local production configuration, `Test-FieldPreflight.ps1`, API `/health`, Agent installation and online confirmation, synthetic DryRun, then the separately authorized physical-printing field gate. Use [FIELD_ACCEPTANCE_CHECKLIST.md](FIELD_ACCEPTANCE_CHECKLIST.md), [REPORT_COMPATIBILITY_MATRIX.md](REPORT_COMPATIBILITY_MATRIX.md), and [PDF_VISUAL_ACCEPTANCE.md](PDF_VISUAL_ACCEPTANCE.md) for retained field evidence and rollback records.
