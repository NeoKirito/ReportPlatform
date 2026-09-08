$paths = @(
    'scripts\Test-FieldPreflight.ps1',
    'scripts\Install-PrintAgentAutoStart.ps1',
    'scripts\Uninstall-PrintAgentAutoStart.ps1',
    'scripts\New-FieldRc1Package.ps1'
)

$failed = $false
foreach ($path in $paths) {
    $tokens = $null
    $errors = $null
    [void][System.Management.Automation.Language.Parser]::ParseFile((Resolve-Path $path), [ref]$tokens, [ref]$errors)
    if ($errors.Count -gt 0) {
        Write-Output ('FAIL ' + $path)
        $errors | ForEach-Object { Write-Output $_.Message }
        $failed = $true
    }
    else {
        Write-Output ('PASS ' + $path)
    }
}

if ($failed) { exit 1 }
