# FastReport Acquisition Requirements

> **Current gate: `FASTREPORT COMPATIBILITY = DEPENDENCY BLOCKED`.** The data, FRX decoding, and `Master` registration preconditions are ready; the only blocker is the absence of a lawful, .NET-compatible FastReport runtime and its required entitlement.

Provide **one** of the following lawful inputs. Do not add any DLL, package archive, credential, license key, or customer account material to this public repository.

| Option | What to provide | What this unlocks | Safe handling boundary |
|---|---|---|---|
| A. Corporate Fast Reports NuGet access | Confirmation that the company owns FastReport .NET, the entitled account holder or approved administrator contact, the approved package ID/version, and the license mechanism. | A modern .NET-compatible package restore and real smoke test. | Configure the source and credentials only in a user-level `NuGet.Config`, credential provider, or secret environment variable. Never commit the source credentials or a private `NuGet.Config`. |
| B. Legal FastReport .NET installation | An explicitly approved local installation path, plus product edition/version and license mechanism. | Read-only assembly metadata inspection; if the version supports .NET 10, a local conditional integration path can be evaluated. | Keep any copied vendor files only under ignored `.runtime/` during investigation; do not reference a proprietary DLL from tracked project files. |
| C. Company-owned package archive | A company-provided package archive or internal package-feed access with the exact package/version and entitlement confirmation. | Controlled restore through a local, ignored source or approved private feed. | Store the archive outside the repository or under ignored `.runtime/`; do not commit it. |
| D. Old Report-service publish directory | The legacy service publish directory or an approved archive, including `FastReport*.dll`, adjacent `*.deps.json`, `*.config`, `licenses.licx` filename, and export dependencies. | Historical renderer-version evidence: assembly/file/product version, public-key token, target-framework hint, `FastReport.Export.Pdf` and `FastReport.Web` presence. | Read-only inspection only. A .NET Framework-only legacy DLL is evidence, **not** a DLL to reference from .NET 10. |

## Minimum information needed for an executable smoke gate

The fastest path is Option A. The implementation needs the following information before any FastReport package is referenced:

| Required item | Why it is needed |
|---|---|
| Exact commercial package ID and version | Prevents an accidental demo/trial or incompatible product line. |
| Edition and legal entitlement confirmation | Determines whether PDF export is licensed and permitted in the hospital deployment. |
| License activation mechanism | Keeps license configuration out of source control and avoids runtime surprises. |
| Supported target framework / RID | Confirms compatibility with the project’s .NET 10 target rather than an old .NET Framework-only assembly. |
| Package source access method | Enables a restore without embedding account tokens or credentials in the repository. |
| If using an existing install: full approved directory path | Allows a read-only version/dependency inspection before deciding whether it is usable. |

## What will happen after a lawful runtime is available

The integration will remain behind `IFastReportRuntime`. Each real smoke invocation will create a new `FastReport.Report`, load the decoded database FRX unchanged, register the actual `Master` `DataTable` unchanged, enable the required data source, prepare, export PDF, dispose the report, and record timing. The smoke will specifically prove or disprove `XMMC`/`xmmc` lookup and the source `nl: System.Int32` versus FRX `System.Int16` declaration. It will not perform performance tuning, template rewriting, print-agent work, or changes to `main`.

> A legacy FastReport DLL can identify historical renderer behavior, but it does **not** imply that the DLL can or should be referenced by a .NET 10 project.

## References

The related investigation and decision record is in [FASTREPORT_DEPENDENCY_REPORT.md](FASTREPORT_DEPENDENCY_REPORT.md). Fast Reports documents client-account access to unrestricted packages through its private NuGet service.[1]

[1]: https://www.fast-report.com/blogs/private-nuget-server "Fast Reports Private NuGet-server"
