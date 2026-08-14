# NuGet restore diagnostic report

## Incident summary

The solution initially failed before normal package restore/build/test evaluation. The exception originated in NuGet configuration default-path construction with the message:

```text
Value cannot be null. (Parameter 'path1')
```

The failure was environmental, not a package version, project SDK, or repository NuGet configuration defect.

## Confirmed root cause

The automated Windows session did not provide the `ProgramFiles(x86)` environment variable. NuGet's configuration default resolution calls the equivalent of `Path.Combine(Environment.GetFolderPath(SpecialFolder.ProgramFilesX86), ...)`. With the special-folder path absent, that composition throws before normal restore work completes.

Isolation tests established the following result:

| Case | Environment adjustment | Result |
|---|---|---|
| E | Only `ProgramFiles(x86)=C:\Program Files (x86)` | Restore exited successfully. |
| G | Only `HOME`, `DOTNET_CLI_HOME`, and `NUGET_PACKAGES` configured | Restore continued to fail with the null-path exception. |

Therefore `ProgramFiles(x86)` is the smallest proven correction for this session type.

## Applied process fix

The verification script sets the variable for its child process only, then runs restore, build, test, and whitespace verification:

```powershell
Set-Item -Path 'Env:ProgramFiles(x86)' -Value 'C:\Program Files (x86)'
dotnet restore PEIS.ReportPlatform.sln
dotnet build PEIS.ReportPlatform.sln --no-restore
dotnet test PEIS.ReportPlatform.sln --no-build
git diff --check
```

The repository's `global.json` remains unchanged. No NuGet configuration file was deleted or rewritten, no package was downgraded, and no global machine setting was required. This follows the minimum-fix principle.

## Code errors subsequently exposed

Once restore could proceed, normal compilation exposed independent source/project reference issues. These were corrected and committed separately from the environmental diagnosis:

| Area | Correction |
|---|---|
| PrintAgent host/services | Added the required .NET hosting and HTTP client package references plus explicit namespace imports. |
| SQL Server infrastructure | Added the Options package reference and corrected the `DateTime`/`DateTimeOffset` conversion used in definition metadata. |

These are code/build corrections; they are not part of the NuGet root cause.

## Reproducible safe invocation

From the repository root in the affected automated Windows session:

```powershell
Set-Item -Path 'Env:ProgramFiles(x86)' -Value 'C:\Program Files (x86)'
dotnet restore PEIS.ReportPlatform.sln
dotnet build PEIS.ReportPlatform.sln --no-restore
dotnet test PEIS.ReportPlatform.sln --no-build
```

Use `Set-Item -Path 'Env:ProgramFiles(x86)' ...` exactly as shown; PowerShell requires that form because the environment-variable name contains parentheses.

## Verification status

The full solution verification was executed after the environment correction and source fixes. Restore and build completed successfully; the test result is reported in the final task verification record. Tests requiring a real SQL Server were skipped by their explicit environment gate, not reported as a production database pass.
