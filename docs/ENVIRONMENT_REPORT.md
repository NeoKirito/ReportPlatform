# Environment Report

**Generated:** 2026-08-14  
**Scope:** Local development workstation baseline; no hardware printing was performed.

## Available

| Area | Observation |
|---|---|
| Operating system | Windows 10 build 19041.985, x64 |
| Git | 2.50.0.windows.2 |
| .NET | SDK 8.0.414; .NET 7 and .NET 8 runtimes installed |
| SQL Server client | `SQLCMD.EXE` installed |
| Windows printing | Print Spooler running; DASCOM DL-620Z and DASCOM DL-620E are installed, alongside virtual devices |
| Service administration | Current `Administrator` session is elevated |
| NuGet connectivity | HTTPS request reached `api.nuget.org` and received a redirect to the configured regional endpoint |

## Missing or not verified

| Area | Constraint |
|---|---|
| Target SDK | `global.json` requires .NET SDK `10.0.100`; it is not installed, so restore, build and test cannot run on this workstation |
| Build tools | Visual Studio or Build Tools could not be detected through `vswhere` |
| FastReport | No FastReport package, DLL, or license was found |
| Oracle | Oracle client tools were not found |
| Database | No connection string, reachable database, schema fixture, or SQL Server endpoint was supplied |
| Report artifacts | No FRX template, old Report service binary, PDF performance fixture, or legacy API fixture was supplied |
| Hardware validation | No real A4 or barcode print validation has been performed |

## Consequences

FastReport rendering, real SQL or Oracle queries, PDF visual validation, and measured production performance are **Integration Gates**. The codebase must retain deterministic stubs and interfaces so CI, contract tests, print routing, and DryRun queue behavior remain verifiable without the missing dependencies. As requested, physical printing remains unverified until approved validation instructions are available.

## Required later artifacts

| Needed artifact | Why it is needed |
|---|---|
| .NET SDK 10.0.100, or an approved `global.json` change | Restore, build, and test the target solution |
| Licensed FastReport package, license, and representative FRX files | Implement and validate the production renderer without replacing FRX compatibility |
| Read-only SQL Server connection details and schema fixtures | Validate definition/data providers and SQL contracts; Oracle details only if that provider is required |
| Legacy request/response fixtures and old service access | Lock down exact compatibility semantics |
| Image-heavy slow-report fixture | Measure performance against a real workload |
| Approved printer test procedure | Complete the physical A4 and barcode hardware gate |

> This report records environment conditions only. It does not claim compilation, FastReport integration, database connectivity, performance targets, or physical printer output have passed.
