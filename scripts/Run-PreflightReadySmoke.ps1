$identity = Join-Path $env:TEMP ('peis-preflight-identity-' + [Guid]::NewGuid().ToString('N'))
$agentProductionConfig = Join-Path $PWD 'publish\print-agent\appsettings.Production.json'
$syntheticAgentConfig = @{
    Agent = @{
        ServerUrl = 'http://127.0.0.1:5080'
        AgentId = ''
        RegistrationToken = 'synthetic-local-preflight-token'
        StationId = 'DRYRUN-01'
        PrinterBindings = @{}
        HeartbeatSeconds = 20
        WorkDirectory = '.runtime/print-agent'
        PrintBackend = @{
            Mode = 'DryRun'
            Executable = ''
            ArgumentsTemplate = '{file} {printer} {copies} {duplex}'
            RetryCount = 0
            RetryDelaySeconds = 0
        }
    }
} | ConvertTo-Json -Depth 8

$oldRegistration = $env:PrintAgentSecurity__RegistrationToken
$oldAccess = $env:InternalApiSecurity__AccessToken
$oldSigning = $env:ArtifactAccess__SigningKey
try {
    [IO.File]::WriteAllText($agentProductionConfig, $syntheticAgentConfig, [Text.UTF8Encoding]::new($false))
    $env:PrintAgentSecurity__RegistrationToken = 'synthetic-local-api-registration-token'
    $env:InternalApiSecurity__AccessToken = 'synthetic-local-api-access-token'
    $env:ArtifactAccess__SigningKey = 'synthetic-local-artifact-signing-key'
    $json = & .\scripts\Test-FieldPreflight.ps1 `
        -Mode LocalDryRun `
        -ApiDirectory .\publish\report-api `
        -AgentDirectory .\publish\print-agent `
        -AgentIdentityDirectory $identity `
        -Json
    $result = ($json | Out-String) | ConvertFrom-Json
    if ($result.Final -ne 'READY_FOR_DRY_RUN') {
        $result.Checks | ForEach-Object { Write-Output ('CHECK ' + $_.Name + ': ' + $_.Status) }
        throw ('Expected READY_FOR_DRY_RUN, received ' + $result.Final)
    }
    if ($result.Persistence -ne 'PASS' -or $result.ArtifactSecurity -ne 'PASS' -or $result.PrintBackend -ne 'DRY_RUN') {
        throw 'Synthetic persistence, artifact, or DryRun preflight check did not pass.'
    }
    if (($result.Checks | ConvertTo-Json -Depth 8) -match 'synthetic-local-(api|artifact|preflight)') {
        throw 'Preflight JSON emitted a synthetic secret.'
    }
    Write-Output ('PASS preflight ready smoke: ' + $result.Final)
}
finally {
    Remove-Item -LiteralPath $agentProductionConfig -Force -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath $identity -Recurse -Force -ErrorAction SilentlyContinue
    $env:PrintAgentSecurity__RegistrationToken = $oldRegistration
    $env:InternalApiSecurity__AccessToken = $oldAccess
    $env:ArtifactAccess__SigningKey = $oldSigning
}
