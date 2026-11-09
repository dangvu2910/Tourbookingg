param([string]$BackupDirectory, [switch]$SkipBuild)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$compose = Join-Path $PSScriptRoot 'Compose.ps1'
$envPath = Join-Path $root '.env'
Push-Location $root
$stopped = $false
try {
    if (-not $BackupDirectory) { $BackupDirectory = Join-Path $root ('backups/sql-to-sqlexpress-' + (Get-Date -Format 'yyyyMMdd-HHmmss')) }
    $BackupDirectory = [IO.Path]::GetFullPath($BackupDirectory)
    $boundary = [IO.Path]::GetFullPath((Join-Path $root 'backups')) + [IO.Path]::DirectorySeparatorChar
    if (-not $BackupDirectory.StartsWith($boundary, [StringComparison]::OrdinalIgnoreCase)) { throw 'Backup must stay inside repository backups.' }
    New-Item -ItemType Directory -Path $BackupDirectory -Force | Out-Null
    Copy-Item -LiteralPath $envPath -Destination (Join-Path $BackupDirectory 'environment-before.env')
    $settings=@{}
    foreach ($line in [IO.File]::ReadAllLines($envPath)) { if ($line -match '^([^#=]+)=(.*)$') { $settings[$matches[1]]=$matches[2] } }
    $nativePort=if ($settings['SQL_EXPRESS_PORT']) { [int]$settings['SQL_EXPRESS_PORT'] } else { 14335 }
    if ($nativePort -lt 1 -or $nativePort -gt 65535) { throw 'Invalid SQL Express port.' }
    $project=if ($settings['HYBRID_PROJECT_NAME']) { $settings['HYBRID_PROJECT_NAME'] } else { 'tourbooking-hybrid' }
    $BackupDirectory | Set-Content (Join-Path $root 'artifacts/sqlexpress-backup-path.txt')
    if (-not $SkipBuild) { & $compose -f compose.hybrid.yaml --profile docker-sql build identity-init catalog-init booking-init customer-init }
    & $compose -f compose.hybrid.yaml -f compose.admin.yaml stop kong gateway identity catalog booking customer
    $stopped = $true
    dotnet run --no-build --project tools/HybridMigration/HybridMigration.csproj -- native-copy $root $BackupDirectory
    if ($LASTEXITCODE -ne 0) { throw 'Native SQL migration or content verification failed.' }
    $migrationReport = Get-Content -Raw (Join-Path $BackupDirectory 'native-migration-report.json') | ConvertFrom-Json
    $expectedTourCount = $migrationReport.schemas.catalog.Tours.count
    dotnet run --no-build --project tools/HybridMigration/HybridMigration.csproj -- native-check $root (Join-Path $BackupDirectory 'native-verification.json')
    if ($LASTEXITCODE -ne 0) { throw 'Native login/schema permissions verification failed.' }
    $network=docker network ls --filter "label=com.docker.compose.project=$project" --filter 'label=com.docker.compose.network=default' --format '{{.Name}}'
    if (@($network).Count -ne 1) { throw 'Cannot identify the project default network.' }
    docker run --rm --network $network mcr.microsoft.com/dotnet/aspnet:10.0 bash -c ("exec 3<>/dev/tcp/host.docker.internal/"+$nativePort)
    if ($LASTEXITCODE -ne 0) { throw 'Docker cannot reach SQLEXPRESS TCP port.' }
    $lines = @([IO.File]::ReadAllLines($envPath) | Where-Object { $_ -notmatch '^SQL_DEPLOYMENT=|^SQL_EXPRESS_PORT=' })
    $lines += 'SQL_DEPLOYMENT=sqlexpress'
    $lines += ('SQL_EXPRESS_PORT='+$nativePort)
    [IO.File]::WriteAllLines($envPath, $lines, [Text.UTF8Encoding]::new($false))
    & $compose -f compose.hybrid.yaml -f compose.admin.yaml -f compose.sqlexpress.yaml up -d --no-build --wait --wait-timeout 180
    & $compose -f compose.hybrid.yaml -f compose.admin.yaml -f compose.sqlexpress.yaml restart kong
    $ok = $false
    for ($attempt=0; $attempt -lt 15; $attempt++) {
        try { $catalog = Invoke-RestMethod 'http://localhost:8080/api/v1/catalog'; if ($catalog.success -and $catalog.meta.pagination.total_items -eq $expectedTourCount) { $ok=$true; break } } catch { }
        Start-Sleep -Seconds 1
    }
    if (-not $ok) { throw 'Kong catalog did not recover after native SQL switch.' }
    dotnet run --no-build --project tools/HybridMigration/HybridMigration.csproj -- native-check $root (Join-Path $BackupDirectory 'native-verification.json')
    if ($LASTEXITCODE -ne 0) { throw 'Post-start native SQL content verification failed.' }
    & $compose -f compose.hybrid.yaml stop sqlserver
    Write-Host 'SQL switched to localhost\SQLEXPRESS, TourBookingDB. Docker source volume retained.'
    Write-Host "Verified backup: $BackupDirectory"
} catch {
    if ($stopped) {
        Copy-Item -LiteralPath (Join-Path $BackupDirectory 'environment-before.env') -Destination $envPath -Force
        & $compose -f compose.hybrid.yaml -f compose.admin.yaml up -d --no-build --wait --wait-timeout 180
        & $compose -f compose.hybrid.yaml restart kong
        Write-Warning 'Restored Docker SQL connections. Native copies and backups retained for inspection.'
    }
    throw
} finally { Pop-Location }
