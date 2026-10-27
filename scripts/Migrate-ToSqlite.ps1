param([string]$Destination = 'data/sqlite', [switch]$ExportOnly)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
Push-Location $projectRoot
try {
    $destinationPath = [IO.Path]::GetFullPath((Join-Path $projectRoot $Destination))
    $allowedRoot = [IO.Path]::GetFullPath((Join-Path $projectRoot 'data')) + [IO.Path]::DirectorySeparatorChar
    if (-not $destinationPath.StartsWith($allowedRoot, [StringComparison]::OrdinalIgnoreCase)) { throw 'Destination must be within the project data directory.' }
    if ((Test-Path -LiteralPath $destinationPath) -and (Get-ChildItem -LiteralPath $destinationPath -Force | Select-Object -First 1)) {
        throw 'Destination is not empty. Existing SQLite data will not be overwritten. Choose a new destination.'
    }
    $legacyServices = @('gateway','identity','catalog','booking','payment','review','support','reporting','notification')
    $backends = @(
        @{ Name='identity'; Folder='Identity' },
        @{ Name='catalog'; Folder='Catalog' },
        @{ Name='booking'; Folder='booking' },
        @{ Name='customer'; Folder='customer' },
        @{ Name='notification'; Folder='Notification' }
    )
    if (-not $ExportOnly) {
        foreach ($backend in $backends) {
            if (Test-Path -LiteralPath (Join-Path $projectRoot "services/$($backend.Folder)/data")) { throw "Target already exists: $($backend.Folder)/data. Use -ExportOnly for an isolated export." }
        }
    }
    & "$PSScriptRoot/Initialize-Environment.ps1"
    & docker build -f Dockerfile.migration -t tourbooking-sqlite-migration .
    if ($LASTEXITCODE -ne 0) { throw 'Migration image build failed; source applications have not been stopped.' }
    # Quiesce writers and consumers before taking a consistent source snapshot.
    & "$PSScriptRoot/Compose.ps1" -f compose.sqlserver.yaml stop @legacyServices
    $sqlContainer = 'tourbooking-distributed-sqlserver-1'
    & docker exec --user 0 $sqlContainer mkdir -p /var/opt/mssql/backup
    & docker exec --user 0 $sqlContainer chown 10001:0 /var/opt/mssql/backup
    $backupStamp = Get-Date -Format 'yyyyMMdd-HHmmss'
    foreach ($databaseName in @('TourBookingDb', 'TourBookingNotifications')) {
        $backupFile = "/var/opt/mssql/backup/$databaseName-$backupStamp.bak"
        $query = "BACKUP DATABASE [$databaseName] TO DISK = N'$backupFile' WITH COPY_ONLY, INIT"
        # sqlcmd reads its password inside the container; never print source credentials.
        $backupCommand = '/opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -P "$MSSQL_SA_PASSWORD" -C -b'
        $query | & docker exec -i $sqlContainer bash -c $backupCommand
        if ($LASTEXITCODE -ne 0) { throw "Backup failed for $databaseName. Source volumes remain intact." }
    }
    $stagingPath = $destinationPath + '.staging-' + (Get-Date -Format 'yyyyMMdd-HHmmss')
    New-Item -ItemType Directory -Path $stagingPath -Force | Out-Null
    & docker run --rm --network tourbooking-distributed_default --env-file .env --mount "type=bind,source=$stagingPath,target=/output" tourbooking-sqlite-migration /output
    if ($LASTEXITCODE -ne 0) { throw 'Migration failed. Keep source SQL Server volumes; inspect errors before starting SQLite.' }
    if (Test-Path -LiteralPath $destinationPath) { throw 'Destination appeared during migration. Verified output remains in staging; nothing was overwritten.' }
    Move-Item -LiteralPath $stagingPath -Destination $destinationPath
    if (-not $ExportOnly) {
        foreach ($backend in $backends) {
            $sourcePath = [IO.Path]::GetFullPath((Join-Path $destinationPath $backend.Name))
            $targetPath = [IO.Path]::GetFullPath((Join-Path $projectRoot "services/$($backend.Folder)/data"))
            $serviceRoot = [IO.Path]::GetFullPath((Join-Path $projectRoot "services/$($backend.Folder)")) + [IO.Path]::DirectorySeparatorChar
            if (-not $sourcePath.StartsWith($destinationPath + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -or -not $targetPath.StartsWith($serviceRoot, [StringComparison]::OrdinalIgnoreCase)) { throw 'Unexpected install path.' }
            Move-Item -LiteralPath $sourcePath -Destination $targetPath
        }
    }
    Write-Host "Verified migration report: $destinationPath/migration-report.json"
    Write-Host 'Start: .\scripts\Compose.ps1 up -d --build'
    Write-Host 'Source SQL Server and backup volumes have been preserved.'
} finally { Pop-Location }
