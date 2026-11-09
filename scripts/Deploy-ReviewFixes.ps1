# Run after building/testing the current source and all six application images.
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$compose = Join-Path $PSScriptRoot 'Compose.ps1'
$files = @('-f','compose.hybrid.yaml','-f','compose.admin.yaml','-f','compose.sqlexpress.yaml')
$backup = Join-Path $root ('backups/review-fixes-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
Push-Location $root
$stopped = $false
$healthy = $false
$images = @()
try {
    if (-not (Select-String -LiteralPath '.env' -Pattern '^SQL_DEPLOYMENT=sqlexpress$' -Quiet)) { throw 'This rollout requires the existing SQL Express deployment.' }
    New-Item -ItemType Directory -Path $backup -Force | Out-Null
    & $compose @files config --quiet
    foreach ($service in @('identity','catalog','booking','customer','notification','gateway')) {
        $container = & $compose @files ps -q $service
        if (-not $container) { throw "Missing running container: $service" }
        $image = docker inspect $container --format '{{.Image}}'
        $tag = docker inspect $container --format '{{.Config.Image}}'
        $images += @{ service=$service; image=$image; tag=$tag }
    }
    $images | ConvertTo-Json | Set-Content -Encoding UTF8 (Join-Path $backup 'previous-images.json')
    & $compose @files stop kong gateway identity catalog booking customer notification
    $stopped = $true
    $ErrorActionPreference = 'Continue'
    dotnet run --no-build --project tools/HybridMigration/HybridMigration.csproj -- enable-reservations $root $backup
    $migrationResult = $LASTEXITCODE
    $ErrorActionPreference = 'Stop'
    if ($migrationResult -ne 0) { throw 'Reservation migration failed. Existing booking values were not cancelled or repriced.' }
    & $compose @files up -d --no-build --wait --wait-timeout 180
    & $compose @files restart kong
    $apiReady = $false
    for ($attempt=0; $attempt -lt 20; $attempt++) {
        try { $catalog=Invoke-RestMethod 'http://localhost:8080/api/v1/catalog'; if ($catalog.success) { $apiReady=$true; break } } catch { }
        Start-Sleep -Seconds 1
    }
    if (-not $apiReady) { throw 'Public API did not recover.' }
    $ErrorActionPreference = 'Continue'
    dotnet run --no-build --project tools/HybridMigration/HybridMigration.csproj -- runtime-check $root (Join-Path $backup 'runtime-verification.json')
    $runtimeResult = $LASTEXITCODE
    $ErrorActionPreference = 'Stop'
    if ($runtimeResult -ne 0) { throw 'Bearer runtime verification failed.' }
    $healthy = $true
    # Only prune old read tables after the new report route and authentication work.
    $ErrorActionPreference = 'Continue'
    dotnet run --no-build --project tools/HybridMigration/HybridMigration.csproj -- native-prune $root (Join-Path $backup 'read-model-cleanup')
    $pruneResult = $LASTEXITCODE
    $ErrorActionPreference = 'Stop'
    if ($pruneResult -ne 0) { throw 'Application is healthy, but old read-table cleanup needs inspection.' }
    Write-Output "Review fixes deployed and verified. Backup/report: $backup"
} catch {
    if ($stopped -and -not $healthy) {
        foreach ($previous in $images) {
            docker image tag $previous.image $previous.tag
            if ($LASTEXITCODE -ne 0) { throw 'Cannot restore an old application image. Inspect previous-images.json.' }
        }
        & $compose @files up -d --no-build --wait --wait-timeout 180
        & $compose @files restart kong
        Write-Warning 'Restored previous application images; additive schema and verified backups retained.'
    }
    throw
} finally { Pop-Location }
