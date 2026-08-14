# FastReport Dependency Report

> **Decision at 2026-08-14: `FOUND_AND_COMPATIBLE` for the user-confirmed free runtime.** The production compatibility path uses official, MIT-licensed FastReport Open Source packages from NuGet.org. No commercial FastReport DLL, license key, account credential, patient identifier, FRX body, or database SQL body is tracked by this public repository.

## Dependency decision

| Item | Selected value | Status |
|---|---|---|
| Renderer | `FastReport.OpenSource` | **FOUND_AND_COMPATIBLE** |
| Core package | `FastReport.OpenSource` `2026.2.3` | **FOUND_AND_COMPATIBLE** |
| PDF package | `FastReport.OpenSource.Export.PdfSimple` `2026.2.3` | **FOUND_AND_COMPATIBLE** |
| Publisher | `FastReports` on NuGet.org, with a reserved package prefix | **CONFIRMED** |
| License | MIT | **CONFIRMED** |
| Target frameworks | `.NET 6.0` or higher; `.NET Framework 4.6.2` or higher | **CONFIRMED** |
| Current project target | `.NET 10` | **PACKAGE-COMPATIBLE** |
| Package source | Public NuGet.org | **CONFIRMED** |
| License mechanism | MIT notice retention; no runtime key or account token | **CONFIRMED** |

The official NuGet pages state that the selected core and PDF Simple packages are compatible with .NET 6 or higher, which includes this project's .NET 10 target.[1] [2] The official upstream repository publishes the MIT license, allowing use, modification, and distribution subject to retaining the copyright and permission notice.[3]

## Legacy-runtime inventory

The earlier read-only local inventory found no historical FastReport assembly, NuGet cache entry, old service publish directory, `licenses.licx`, or deployment dependency manifest. Therefore, historical AssemblyVersion, ProductVersion, public-key token, exact target framework, `FastReport.Export.Pdf`, `FastReport.Web`, and prior license mechanism remain **NOT_FOUND / UNVERIFIED**. The selected free runtime is a compatibility target proven by the real smoke test below, not a claim that it is binary-identical to an unavailable legacy deployment.

## Integration boundary

FastReport types live only in `src/PEIS.Report.FastReport.OpenSource`. That project implements the Engine-owned `IFastReportRuntime` seam. The API composition root selects it only when `ReportEngine:Renderer=FastReportOpenSource`; the default remains `Stub`. Contracts, legacy compatibility controllers, printing code, and `PEIS.PrintAgent` do not reference FastReport types.

Each call to `PrepareAsync` constructs a new `FastReport.Report`, loads the decoded FRX unchanged, registers every supplied table under its original name, enables the matching source, prepares, exports, and disposes the report through a per-request document handle. No mutable `Report` instance is static, singleton, cached, or shared between requests.

## PDF Simple scope

`FastReport.OpenSource.Export.PdfSimple` is the official free PDF plugin. Its official package description positions it as basic PDF export and identifies advanced features such as encryption, digital signatures, and font embedding as capabilities of other FastReport products.[2] This gate proves basic report compatibility and PDF structure only; it does not assert advanced PDF features, exact commercial-renderer parity, or production performance.

## Real xmtm compatibility result

The gated, read-only integration test executed the confirmed current legacy route (`querytype=djwh`, `bbid=xmtm`) with an approved non-empty private sample. It fetched the definition from `dbo.xt_bgdy_djwh_zzj`, decoded the database-owned Base64 UTF-8 FRX in memory, executed the database-owned query, and registered a one-row `Master` table with the seven confirmed columns. It then loaded the untouched FRX in a new free FastReport instance, prepared one page, and exported a non-empty PDF beginning with `%PDF-`.

| Smoke condition | Result |
|---|---|
| Real FRX decoded hash | `99F565209534D02C02FB45AA968A674AB95CC7C50232FDFC554CAEDC488EAB1F` |
| `Master` rows / columns | `1` / `xm`, `nl`, `tmh`, `zxksmc`, `XMMC`, `xb`, `flmc` |
| FRX load | **PASS** |
| RegisterData under `Master` | **PASS** |
| FRX `Master.xmmc` against SQL `XMMC` | **PASS** |
| FRX `nl: Int16` against SQL `nl: Int32` | **PASS**; no coercion was added |
| Prepare | **PASS** |
| Pages | `1` |
| PDF export / signature | **PASS** / `%PDF-` |
| PDF bytes | `45,365` |
| Application watermark | **NOT VERIFIED**; the smoke is an explicit base-PDF run with application watermark disabled. |

## References

[1]: https://www.nuget.org/packages/FastReport.OpenSource "FastReport.OpenSource 2026.2.3 on NuGet.org"
[2]: https://www.nuget.org/packages/FastReport.OpenSource.Export.PdfSimple "FastReport.OpenSource.Export.PdfSimple 2026.2.3 on NuGet.org"
[3]: https://raw.githubusercontent.com/FastReports/FastReport/master/LICENSE.md "FastReport Open Source MIT License"
