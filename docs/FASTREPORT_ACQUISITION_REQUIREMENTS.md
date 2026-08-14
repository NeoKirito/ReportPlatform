# FastReport Open Source Acquisition Requirements

> **Current gate: no commercial FastReport acquisition is required.** The user confirmed the free FastReport version, and the project now uses official MIT-licensed packages from NuGet.org. This document defines the reproducible and safe dependency boundary for that choice.

## Required packages

| Package | Version | Source | Purpose |
|---|---:|---|---|
| `FastReport.OpenSource` | `2026.2.3` | NuGet.org, owner `FastReports` | Free report runtime and FRX load/prepare support. |
| `FastReport.OpenSource.Export.PdfSimple` | `2026.2.3` | NuGet.org, owner `FastReports` | Official basic free PDF export plugin. |

The versions are pinned together in `PEIS.Report.FastReport.OpenSource.csproj` to avoid an accidental API mismatch. Both packages publicly declare support for .NET 6 or higher, while this repository targets .NET 10.[1] [2]

## License and repository boundary

The official upstream license is MIT. The required copyright and permission notice must remain in any distributed copy or substantial portion of the software.[3] This repository references packages; it does not commit a DLL, package archive, license key, account token, trial bypass, or private NuGet configuration.

Normal public NuGet restore is sufficient. No credentials or user-specific feed configuration are needed for this free path. The commercial FastReport product line may be evaluated later only if a future requirement needs its separate advanced PDF capabilities; it is not a prerequisite for the current compatibility smoke.

## Activation requirements for real smoke

The real database smoke remains intentionally opt-in because it accesses private infrastructure and approved sample data. Enable it only in a controlled environment with all of the following process-local values:

| Setting | Required value / source | Never commit |
|---|---|---|
| `REPORTPLATFORM_TEST_SQLSERVER` | `1` | No secret itself, but do not set it in public CI. |
| `REPORTPLATFORM_TEST_FASTREPORT` | `1` | No secret itself, but do not set it in public CI. |
| `REPORT_DATABASE__CONNECTIONSTRING` | Explicitly approved read-only connection | **Yes** |
| `REPORTPLATFORM_TEST_REPORT_ID` | `xmtm` | Keep runtime-only with the test setup. |
| `REPORTPLATFORM_TEST_LEGACY_PAYLOAD_JSON` | Approved private request body | **Yes** |
| `REPORTPLATFORM_TEST_FASTREPORT_EVIDENCE_PATH` | Ignored `.runtime/private-legacy-evidence/` path | The output is private. |

The provided local runtime script loads these values only from ignored files and writes a metadata-only evidence summary. It does not save a PDF, FRX, SQL, raw payload, patient row, or connection string.

## Non-goals of the free-runtime smoke

The selected PDF Simple plugin proves FRX loading, `Master` data registration, Prepare, page creation, and basic `%PDF-` export. It does not assert commercial FastReport feature parity, encryption, digital signing, font embedding, pixel-identical output, large-report performance, print-agent integration, or watermarks.

## References

[1]: https://www.nuget.org/packages/FastReport.OpenSource "FastReport.OpenSource 2026.2.3 on NuGet.org"
[2]: https://www.nuget.org/packages/FastReport.OpenSource.Export.PdfSimple "FastReport.OpenSource.Export.PdfSimple 2026.2.3 on NuGet.org"
[3]: https://raw.githubusercontent.com/FastReports/FastReport/master/LICENSE.md "FastReport Open Source MIT License"
