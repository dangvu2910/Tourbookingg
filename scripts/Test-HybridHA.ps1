param([switch]$SkipBuild, [switch]$KeepRunning)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$compose = Join-Path $PSScriptRoot 'Compose.ps1'
$project = 'tourbooking-hybrid-verification'
$directory = Join-Path $root 'artifacts/hybrid-verification'
$testEnv = Join-Path $directory 'test.env'
$files = @('--env-file',$testEnv,'-p',$project,'-f','compose.hybrid.yaml','-f','compose.ha.yaml','-f','tests/compose.hybrid-verification.yaml')
$oldOptIn = $env:TOURBOOKING_ISOLATED_TESTS
Push-Location $root
try {
    New-Item -ItemType Directory -Path $directory -Force | Out-Null
    if (-not (Test-Path -LiteralPath $testEnv)) {
        $values = @("HYBRID_PROJECT_NAME=$project",'HA_HTTP_PORT=18083','ADMIN_EMAIL=admin@hybrid-verification.example.test','HYBRID_SEED_DEMO=true')
        foreach ($key in @('SQL_PASSWORD','IDENTITY_SQL_PASSWORD','CATALOG_SQL_PASSWORD','BOOKING_SQL_PASSWORD','CUSTOMER_SQL_PASSWORD','MONGO_ROOT_PASSWORD','MONGO_CHAT_PASSWORD','MONGO_NOTIFICATION_PASSWORD','RABBITMQ_PASSWORD','INTERNAL_SERVICE_KEY','API_JWT_SIGNING_KEY','HYBRID_ADMIN_PASSWORD')) {
            $values += $key + '=' + [Guid]::NewGuid().ToString('N') + 'Aa!123'
        }
        [IO.File]::WriteAllLines($testEnv, $values, [Text.UTF8Encoding]::new($false))
    }
    if (-not (Select-String -LiteralPath $testEnv -Pattern '^HYBRID_PROJECT_NAME=tourbooking-hybrid-verification$' -Quiet)) { throw 'Invalid isolated project in test env file.' }
    & $compose @files config --quiet
    if (-not $SkipBuild) { & $compose @files build identity-init catalog-init booking-init customer-init notification gateway tests }
    & $compose @files up -d --no-build --wait --wait-timeout 240
    $env:TOURBOOKING_ISOLATED_TESTS = '1'
    node tests/hybrid-resilience.mjs
    if ($LASTEXITCODE -ne 0) { throw 'Hybrid replica/database/broker resilience checks failed.' }
    & $compose @files --profile test run --rm --no-deps tests | Tee-Object -FilePath (Join-Path $directory 'dotnet-tests.log')
} finally {
    if (-not $KeepRunning -and (Test-Path -LiteralPath $testEnv)) {
        # Explicit literal project + dedicated compose files: these volumes are test-only.
        & $compose @files down --volumes
    }
    $env:TOURBOOKING_ISOLATED_TESTS = $oldOptIn
    Pop-Location
}
