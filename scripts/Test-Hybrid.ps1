param([switch]$SkipBuild, [switch]$KeepRunning, [switch]$Monitoring, [switch]$Cluster)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$compose = Join-Path $PSScriptRoot 'Compose.ps1'
$project = 'tourbooking-hybrid-verification'
$directory = Join-Path $root 'artifacts/hybrid-verification'
$testEnv = Join-Path $directory 'test.env'
$files = @('--env-file',$testEnv,'-p',$project,'-f','compose.hybrid.yaml','-f','tests/compose.hybrid-verification.yaml')
if ($Monitoring) { $files += @('-f','compose.monitoring.yaml') }
if ($Cluster) { $files += @('-f','compose.replication-test.yaml','-f','compose.cluster-apps-test.yaml') }
$oldOptIn = $env:TOURBOOKING_ISOLATED_TESTS
Push-Location $root
try {
    New-Item -ItemType Directory -Path $directory -Force | Out-Null
    if (-not (Test-Path -LiteralPath $testEnv)) {
        $values = @("HYBRID_PROJECT_NAME=$project",'HTTP_PORT=18083','ADMIN_EMAIL=admin@hybrid-verification.example.test','HYBRID_SEED_DEMO=true')
        foreach ($key in @('SQL_PASSWORD','IDENTITY_SQL_PASSWORD','CATALOG_SQL_PASSWORD','BOOKING_SQL_PASSWORD','CUSTOMER_SQL_PASSWORD','MONGO_ROOT_PASSWORD','MONGO_CHAT_PASSWORD','MONGO_NOTIFICATION_PASSWORD','RABBITMQ_PASSWORD','INTERNAL_SERVICE_KEY','API_JWT_SIGNING_KEY','HYBRID_ADMIN_PASSWORD')) {
            $values += $key + '=' + [Guid]::NewGuid().ToString('N') + 'Aa!123'
        }
        [IO.File]::WriteAllLines($testEnv, $values, [Text.UTF8Encoding]::new($false))
    }
    if (-not (Select-String -LiteralPath $testEnv -Pattern '^HYBRID_PROJECT_NAME=tourbooking-hybrid-verification$' -Quiet)) { throw 'Invalid isolated project in test env file.' }
    # Older env files used HA_HTTP_PORT for the removed HAProxy edge; the test stack must never bind live port 8080.
    if (-not (Select-String -LiteralPath $testEnv -Pattern '^HTTP_PORT=' -Quiet)) { Add-Content -LiteralPath $testEnv -Value 'HTTP_PORT=18083' -Encoding utf8 }
    if ($Cluster) {
        foreach ($key in @('MONGO_KEYFILE','RABBITMQ_COOKIE')) {
            if (-not (Select-String -LiteralPath $testEnv -Pattern "^${key}=" -Quiet)) {
                Add-Content -LiteralPath $testEnv -Value ($key + '=' + [Guid]::NewGuid().ToString('N') + [Guid]::NewGuid().ToString('N')) -Encoding utf8
            }
        }
    }
    if ($Monitoring) {
        foreach ($entry in @('PROMETHEUS_PORT=19090', 'GRAFANA_PORT=13000', ('GRAFANA_ADMIN_PASSWORD=' + [Guid]::NewGuid().ToString('N')))) {
            $key = $entry.Split('=')[0]
            if (-not (Select-String -LiteralPath $testEnv -Pattern "^${key}=" -Quiet)) {
                Add-Content -LiteralPath $testEnv -Value $entry -Encoding utf8
            }
        }
    }
    & $compose @files config --quiet
    if (-not $SkipBuild) { & $compose @files build identity-init catalog-init booking-init customer-init notification web-router tests }
    if ($Cluster) {
        & $compose @files stop mongodb rabbitmq
        foreach ($node in @('rabbitmq-a','rabbitmq-b','rabbitmq-c')) { & $compose @files stop $node }
        # Wait for the last-shutdown broker before joining peers; avoid concurrent Mnesia recovery.
        foreach ($node in @('rabbitmq-c','rabbitmq-b','rabbitmq-a')) {
            & $compose @files up -d --no-deps --wait --wait-timeout 120 $node
        }
    }
    & $compose @files up -d --no-build --wait --wait-timeout 240
    if ($Cluster) {
        # Cluster membership and queue replication membership are separate facts.
        $deadline = [DateTime]::UtcNow.AddSeconds(90)
        do {
            $queues = & $compose @files exec -T rabbitmq-a rabbitmqctl list_queues name type members --formatter json
            $queueStates = @((($queues -join "`n") | ConvertFrom-Json) | Where-Object { $_.name -like 'tourbooking.notifications*' })
            $ready = $queueStates.Count -eq 3 -and @($queueStates | Where-Object { $_.type -ne 'quorum' -or $_.members.Count -ne 3 }).Count -eq 0
            if ($ready) { break }
            Start-Sleep -Seconds 2
        } while ([DateTime]::UtcNow -lt $deadline)
        if (-not $ready) { throw 'All three notification queues must have three quorum members' }
        $queues | Set-Content -Encoding utf8 (Join-Path $directory 'quorum-members.json')
    }
    $env:TOURBOOKING_ISOLATED_TESTS = '1'
    if ($Cluster) { node tests/cluster-resilience.mjs }
    else { node tests/hybrid-resilience.mjs }
    if ($LASTEXITCODE -ne 0) { throw 'Hybrid service/database/broker resilience checks failed.' }
    & $compose @files --profile test run --rm --no-deps tests | Tee-Object -FilePath (Join-Path $directory 'dotnet-tests.log')
    if ($Cluster) {
        & $compose @files stop rabbitmq-a
        try {
            & $compose @files --profile test run --rm --no-deps -e RABBITMQ_HOST=rabbitmq-b tests dotnet test tests/Tourbooking.Tests/Tourbooking.Tests.csproj --no-restore --filter FullyQualifiedName~SupportReplyNotifiesTicketOwnerAsynchronously --logger 'trx;LogFileName=broker-failover.trx' --results-directory /test-results --verbosity quiet |
                Tee-Object -FilePath (Join-Path $directory 'broker-failover.log')
        } finally { & $compose @files start rabbitmq-a }
    }
} finally {
    if (-not $KeepRunning -and (Test-Path -LiteralPath $testEnv)) {
        if ($Cluster) {
            foreach ($node in @('rabbitmq-a','rabbitmq-b','rabbitmq-c')) { & $compose @files stop $node }
        }
        # Explicit literal project + dedicated compose files: these volumes are test-only.
        & $compose @files down --volumes
    }
    $env:TOURBOOKING_ISOLATED_TESTS = $oldOptIn
    Pop-Location
}
