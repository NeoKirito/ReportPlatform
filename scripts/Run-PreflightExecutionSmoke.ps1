$root = Join-Path $env:TEMP ('peis-preflight-execution-smoke-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $root -Force | Out-Null
try {
    $json = & .\scripts\Test-FieldPreflight.ps1 `
        -Mode LocalDryRun `
        -ApiDirectory (Join-Path $root 'api') `
        -AgentDirectory (Join-Path $root 'agent') `
        -AgentIdentityDirectory (Join-Path $root 'identity') `
        -Json 2>&1
    $jsonText = ($json | Out-String).Trim()
    $result = $jsonText | ConvertFrom-Json
    if ($result.Title -ne 'ReportPlatform Field Preflight') { throw 'Unexpected preflight title.' }
    if ($result.Final -ne 'HOLD') { throw 'Expected HOLD without synthetic publish assets.' }
    $secretDetails = @($result.Checks | Where-Object { $_.Name -match 'Token|SigningKey' } | Select-Object -ExpandProperty Detail)
    if ($secretDetails | Where-Object { $_ -notmatch '^(RegistrationToken|AccessToken|SigningKey): (PRESENT|MISSING)$' }) {
        throw 'Preflight JSON emitted an unexpected secret detail.'
    }
    if ($jsonText -match '<REPORT_DATABASE_CONNECTION_STRING>' -or $jsonText -match 'Password=') {
        throw 'Preflight JSON emitted a sensitive configuration value.'
    }
    Write-Output ('PASS preflight execution smoke: ' + $result.Final)
}
finally {
    Remove-Item -LiteralPath $root -Recurse -Force -ErrorAction SilentlyContinue
}
