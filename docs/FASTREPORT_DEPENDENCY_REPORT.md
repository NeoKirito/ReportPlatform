# FastReport Dependency Report

> **Decision at 2026-08-14: `LICENSE_REQUIRED` for a modern production runtime; `NOT_FOUND` for a locally available legacy runtime.** No FastReport DLL, license, commercial NuGet credential, or package has been copied into this public repository.

## Investigation boundary

The investigation is read-only. It inspected only candidate local deployment locations, GAC, the current user's NuGet cache, known workspaces, and known local archives by filename. When a DLL would be found, the intended inspection boundary is assembly metadata only: `AssemblyVersion`, `FileVersion`, `ProductVersion`, public-key token, target-framework hint, and sibling dependency names. No assembly is loaded into the project and no license content is opened.

| Source | Files checked | Result | Status |
|---|---|---|---|
| Candidate old Report-service locations | Program Files, Program Files (x86), IIS roots, GAC, common local publish roots, known workspaces | No `FastReport*.dll`, `FastReport*.xml`, `FastReport*.deps.json`, or `licenses.licx` was found. | **NOT_FOUND** |
| Current user's NuGet cache | Direct `fastreport*` package roots | No FastReport package root was found. | **NOT_FOUND** |
| Known local archives | `fix-web-peis-ui-v2-31ccde7.zip`, `PEIS.ReportPlatform.compat.zip` | No FastReport binary, license, dependency manifest, or configuration file was found. The compatibility archive contains only the current repository's renderer-seam source by name. | **NOT_FOUND** |
| Corporate Fast Reports account / private feed | Not supplied to this task | Official full packages require an entitled Fast Reports client account. | **LICENSE_REQUIRED** |
| Public/demo packages | Publicly discoverable but not adopted | A demo/trial package is not evidence of the company's production entitlement and may alter output; it is unsuitable for this compatibility gate. | **NOT USED** |

## Legacy runtime evidence

No old Report-service publish directory or FastReport assembly was available on the connected computer. Accordingly, the following fields cannot be inferred and must remain unclaimed.

| Field | Result |
|---|---|
| Legacy FastReport assembly version | **NOT_FOUND** |
| File / product version | **NOT_FOUND** |
| Public-key token | **NOT_FOUND** |
| Target framework | **NOT_FOUND** |
| `FastReport.Export.Pdf` availability | **NOT_FOUND** |
| `FastReport.Web` availability | **NOT_FOUND** |
| License mechanism | **UNVERIFIED** |
| Direct reuse in .NET 10 | **UNVERIFIED**; no legacy DLL exists to assess, and a .NET Framework-only DLL must not be forced into this project. |

## Modern runtime compatibility

Fast Reports currently states that **FastReport .NET** is compatible with .NET 10 and can be installed using NuGet packages or official downloads.[1] Its official private NuGet documentation states that unrestricted commercial packages are available to Fast Reports clients through an authenticated account; public NuGet listings may expose demo packages rather than unrestricted production packages.[2] The vendor also states that current FastReport product lines have a .NET 6 minimum baseline, reinforcing that the platform must remain on modern .NET rather than be downgraded to .NET Framework.[3]

The correct target is therefore a **company-entitled, modern FastReport .NET package version explicitly compatible with .NET 10**, plus its PDF export dependencies. The exact package ID, version, license mechanism, target frameworks, and transitive DLLs remain **UNVERIFIED** until an entitled source or legal installation path is provided.

## Integration decision

The repository must not reference an old DLL speculatively and must not add a trial/demo dependency to make the gate appear to pass. If an entitled modern runtime is provided, its types will be isolated in the existing renderer boundary or a dedicated `PEIS.Report.FastReport` project. `PEIS.Report.Contracts`, compatibility controllers, printing code, and `PEIS.PrintAgent` remain free of FastReport references.

No FastReport smoke test has run in this state. The current gate is therefore **`FASTREPORT COMPATIBILITY = DEPENDENCY BLOCKED`**, while the separate legacy request/database compatibility gate remains **PASS FOR CURRENT PRODUCTION CONTRACT**.

## References

[1]: https://www.fast-report.com/products/fast-report-net "FastReport .NET product page"
[2]: https://www.fast-report.com/blogs/private-nuget-server "Fast Reports Private NuGet-server"
[3]: https://www.fast-report.com/news/support-dotnet-5 "Fast Reports support policy for older .NET versions"
