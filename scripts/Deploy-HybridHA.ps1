# Deploy already built and tested application images; never migrate or prune data.
param([string]$Project = 'tourbooking-live', [string]$BackupDirectory)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$compose = Join-Path $PSScriptRoot 'Compose.ps1'
$services = @('identity','catalog','booking','customer','notification','gateway')
$dlls = @{identity='Identity.dll';catalog='Catalog.dll';booking='BookingPlatform.dll';customer='CustomerExperience.dll';notification='Notification.dll';gateway='Gateway.dll'}
$oldFiles = @('-f','compose.hybrid.yaml','-f','compose.admin.yaml','-f','compose.sqlexpress.yaml')
$files = $oldFiles + @('-f','compose.ha.yaml')
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$backup = Join-Path $root "backups/ha-deploy-$stamp"
if ($BackupDirectory) {
    $backup = [IO.Path]::GetFullPath($BackupDirectory)
    $boundary = [IO.Path]::GetFullPath((Join-Path $root 'backups')) + [IO.Path]::DirectorySeparatorChar
    if (-not $backup.StartsWith($boundary,[StringComparison]::OrdinalIgnoreCase) -or -not (Test-Path -LiteralPath (Join-Path $backup 'rollback-images.json'))) { throw 'Invalid prepared backup directory.' }
}
$previous = @()
$changed = $false
function Get-BusinessCounts {
    $server = 'localhost\SQLEXPRESS'
    $setting = Get-Content -LiteralPath (Join-Path $root '.env') | Where-Object { $_.StartsWith('SQL_EXPRESS_SERVER=') } | Select-Object -Last 1
    if ($setting) { $server = $setting.Split('=',2)[1].Trim() }
    $builder = [Data.SqlClient.SqlConnectionStringBuilder]::new()
    $builder['Data Source'] = $server
    $builder['Initial Catalog'] = 'TourBookingDB'
    $builder['Integrated Security'] = $true
    $builder['TrustServerCertificate'] = $true
    $connection = [Data.SqlClient.SqlConnection]::new($builder.ConnectionString)
    try {
        $connection.Open()
        $command = $connection.CreateCommand()
        $command.CommandText = "SELECT 'users',COUNT_BIG(*) FROM [identity].[AspNetUsers] UNION ALL SELECT 'tours',COUNT_BIG(*) FROM [catalog].[Tours] UNION ALL SELECT 'bookings',COUNT_BIG(*) FROM [booking].[Bookings] UNION ALL SELECT 'payments',COUNT_BIG(*) FROM [booking].[Payments] UNION ALL SELECT 'reviews',COUNT_BIG(*) FROM [customer].[TourReviews]"
        $reader = $command.ExecuteReader()
        $counts = [ordered]@{}
        while ($reader.Read()) { $counts[$reader.GetString(0)] = $reader.GetInt64(1) }
        $reader.Dispose()
        return $counts
    } finally { $connection.Dispose() }
}
Push-Location $root
try {
    if ($Project -ne 'tourbooking-live') { throw 'This deployment script only targets the established tourbooking-live project.' }
    if (-not (Select-String -LiteralPath '.env' -Pattern '^SQL_DEPLOYMENT=sqlexpress$' -Quiet)) { throw 'Expected the existing SQL Express deployment; refusing to change database mode.' }
    & $compose -p $Project @files config --quiet
    New-Item -ItemType Directory -Path $backup -Force | Out-Null
    $beforeCounts = Get-BusinessCounts
    $beforeCounts | ConvertTo-Json | Set-Content -Encoding UTF8 (Join-Path $backup 'business-counts-before.json')
    $availableImages = @(docker image ls --no-trunc -q)
    foreach ($service in $services) {
        $container = "$Project-$service-1"
        $info = (docker inspect $container | ConvertFrom-Json)[0]
        if ($LASTEXITCODE -ne 0 -or $info.Config.Labels.'com.docker.compose.project' -ne $Project) { throw "Unexpected container for $service" }
        $tag = "tourbooking-rollback-$service`:$stamp"
        $archive = $null
        if ($info.Image -in $availableImages) {
            docker image tag $info.Image $tag
            if ($LASTEXITCODE -ne 0) { throw "Cannot preserve $service image" }
        } else {
            # Export excludes mounted data and image environment metadata/secrets.
            $archive = Join-Path $backup "$service-rootfs.tar"
            if (-not (Test-Path -LiteralPath $archive)) {
                docker export --output $archive $container
                if ($LASTEXITCODE -ne 0) { throw "Cannot preserve $service filesystem" }
            }
        }
        $previous += @{service=$service;tag=$tag;archive=$archive;dll=$dlls[$service]}
    }
    $previous | ConvertTo-Json | Set-Content -Encoding UTF8 (Join-Path $backup 'rollback-images.json')
    Copy-Item -LiteralPath '.env' -Destination (Join-Path $backup 'deployment.env')
    dotnet run --project tools/HybridMigration/HybridMigration.csproj -- native-backup $root $backup
    if ($LASTEXITCODE -ne 0) { throw 'SQL backup or verification failed; rollout not started.' }
    $mongo = "$Project-mongodb-1"
    $ErrorActionPreference = 'Continue'
    docker exec $mongo sh -c 'mongodump --archive=/tmp/tourbooking-pre-ha.archive.gz --gzip --username "$MONGO_INITDB_ROOT_USERNAME" --password "$MONGO_INITDB_ROOT_PASSWORD" --authenticationDatabase admin' 2>&1 | Out-Null
    $dumpResult = $LASTEXITCODE
    docker exec $mongo sh -c 'mongorestore --dryRun --archive=/tmp/tourbooking-pre-ha.archive.gz --gzip --username "$MONGO_INITDB_ROOT_USERNAME" --password "$MONGO_INITDB_ROOT_PASSWORD" --authenticationDatabase admin' 2>&1 | Out-Null
    $verifyResult = $LASTEXITCODE
    $ErrorActionPreference = 'Stop'
    if ($dumpResult -ne 0 -or $verifyResult -ne 0) { throw 'Mongo backup/verification failed; rollout not started.' }
    docker cp "${mongo}:/tmp/tourbooking-pre-ha.archive.gz" (Join-Path $backup 'mongo.archive.gz')
    if ($LASTEXITCODE -ne 0) { throw 'Cannot copy Mongo backup' }
    foreach ($item in @(@{service='identity';path='/keys';file='keys.tar.gz'},@{service='catalog';path='/app/wwwroot/images';file='images.tar.gz'})) {
        docker run --rm --user 0 --volumes-from "$Project-$($item.service)-1:ro" --mount "type=bind,source=$backup,target=/backup" --entrypoint tar haproxy:3.0.12-alpine -czf "/backup/$($item.file)" -C $item.path .
        if ($LASTEXITCODE -ne 0) { throw 'Cannot back up keys/images' }
    }
    Get-ChildItem -LiteralPath $backup -File | Get-FileHash -Algorithm SHA256 |
        Select-Object Path,Hash | ConvertTo-Json | Set-Content -Encoding UTF8 (Join-Path $backup 'sha256.json')
    Write-Output 'SQL, MongoDB, keys, images and rollback application snapshots are preserved.'

    $changed = $true
    & $compose -p $Project @oldFiles stop kong
    & $compose -p $Project @files up -d --no-build --no-deps --wait --wait-timeout 240 edge identity catalog booking customer notification gateway kong
    $ready = $false
    for ($attempt=0; $attempt -lt 30; $attempt++) {
        try {
            $catalog = Invoke-RestMethod -TimeoutSec 10 'http://127.0.0.1:8080/api/v1/catalog'
            if ($catalog.success) { $ready = $true; break }
        } catch { }
        Start-Sleep -Seconds 1
    }
    if (-not $ready) { throw 'Public catalog did not recover after deployment.' }
    $afterCounts = Get-BusinessCounts
    $afterCounts | ConvertTo-Json | Set-Content -Encoding UTF8 (Join-Path $backup 'business-counts-after.json')
    foreach ($table in $beforeCounts.Keys) {
        if ($afterCounts[$table] -lt $beforeCounts[$table]) { throw "Business row count decreased for $table; rolling back application images." }
    }
    & $compose -p $Project @files ps
    Write-Output "Deployed balanced application containers. Backup: $backup"
} catch {
    if ($changed) {
        Write-Warning 'Rollout failed; restoring application images, keeping all current databases.'
        foreach ($item in $previous) {
            if ($item.archive) {
                docker import --change 'WORKDIR /app' --change 'USER app' --change 'ENV ASPNETCORE_HTTP_PORTS=8080' --change "ENTRYPOINT dotnet $($item.dll)" $item.archive $item.tag | Out-Null
                if ($LASTEXITCODE -ne 0) { throw "Cannot import rollback image for $($item.service). Backup: $backup" }
            }
            docker image tag $item.tag "tourbooking-hybrid-$($item.service):latest"
            if ($LASTEXITCODE -ne 0) { throw "Cannot restore rollback tag for $($item.service)" }
        }
        & $compose -p $Project @files stop edge
        & $compose -p $Project @oldFiles up -d --no-build --no-deps --wait --wait-timeout 240 identity catalog booking customer notification gateway kong
    }
    throw
} finally { Pop-Location }
