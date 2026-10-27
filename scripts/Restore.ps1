param([Parameter(Mandatory=$true)][string]$BackupDirectory, [switch]$Apply)

$ErrorActionPreference = 'Stop'
if (-not $Apply) { throw 'Pass -Apply to replace current SQLite data after taking a backup.' }
$projectRoot = Split-Path $PSScriptRoot -Parent
$backupRoot = (Resolve-Path -LiteralPath $BackupDirectory).Path
$manifest = Get-Content -LiteralPath (Join-Path $backupRoot 'checksums.json') -Raw | ConvertFrom-Json
foreach ($entry in $manifest) {
    $filePath = [IO.Path]::GetFullPath((Join-Path $backupRoot $entry.File))
    if (-not $filePath.StartsWith($backupRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Invalid backup path.' }
    if ((Get-FileHash -LiteralPath $filePath -Algorithm SHA256).Hash -ne $entry.Hash) { throw "Checksum mismatch: $($entry.File)" }
}

$backends = @(
    @{ Name='identity'; Folder='Identity'; Database='identity' },
    @{ Name='catalog'; Folder='Catalog'; Database='catalog' },
    @{ Name='booking'; Folder='booking'; Database='booking' },
    @{ Name='customer'; Folder='customer'; Database='customer' },
    @{ Name='notification'; Folder='Notification'; Database='notification' }
)
$legacyDatabases = @('identity','catalog','booking','payment','review','support','reporting','notification')
$oldGrouped = (Test-Path -LiteralPath (Join-Path $backupRoot 'sqlite/booking-platform/booking-platform.db')) `
    -and (Test-Path -LiteralPath (Join-Path $backupRoot 'sqlite/customer-experience/customer-experience.db'))
$grouped = $oldGrouped -or ((Test-Path -LiteralPath (Join-Path $backupRoot 'sqlite/booking/booking.db')) `
    -and (Test-Path -LiteralPath (Join-Path $backupRoot 'sqlite/customer/customer.db')))
$sourceRoot = Join-Path $backupRoot 'sqlite'
$conversion = $null
if (-not $grouped) {
    foreach ($service in $legacyDatabases) {
        if (-not (Test-Path -LiteralPath (Join-Path $sourceRoot "$service/$service.db"))) { throw "Missing $service database." }
    }
    $conversion = Join-Path $projectRoot ('data/sqlite-restore-conversion-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
    New-Item -ItemType Directory -Path $conversion | Out-Null
    & dotnet run --no-restore --project (Join-Path $projectRoot 'tools\SqliteMigration\SqliteMigration.csproj') -- --consolidate $backupRoot $conversion
    if ($LASTEXITCODE -ne 0) { throw 'Legacy backup conversion failed. Current databases have not been changed.' }
    $sourceRoot = $conversion
}
foreach ($backend in $backends) {
    $backend.SourceName = $backend.Name
    $backend.SourceDatabase = $backend.Database
    if ($oldGrouped -and $backend.Name -eq 'booking') {
        $backend.SourceName = 'booking-platform'
        $backend.SourceDatabase = 'booking-platform'
    }
    if ($oldGrouped -and $backend.Name -eq 'customer') {
        $backend.SourceName = 'customer-experience'
        $backend.SourceDatabase = 'customer-experience'
    }
    if (-not (Test-Path -LiteralPath (Join-Path $sourceRoot "$($backend.SourceName)/$($backend.SourceDatabase).db"))) {
        throw "Missing $($backend.Name) database in backup."
    }
}

$dataRoot = Join-Path $projectRoot 'data'
$previous = Join-Path $dataRoot ('sqlite-before-restore-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
$stagedRestore = Join-Path $dataRoot ('sqlite-restore-staging-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
if (Test-Path -LiteralPath $stagedRestore) { throw 'Restore staging directory already exists.' }
if (Test-Path -LiteralPath $previous) { throw 'Previous-data destination already exists.' }
New-Item -ItemType Directory -Path $stagedRestore | Out-Null
foreach ($backend in $backends) {
    $stagedFolder = Join-Path $stagedRestore $backend.Folder
    Copy-Item -LiteralPath (Join-Path $sourceRoot $backend.SourceName) -Destination $stagedFolder -Recurse
    if ($backend.SourceDatabase -ne $backend.Database) {
        foreach ($suffix in @('.db','.db-wal','.db-shm')) {
            $oldFile = Join-Path $stagedFolder ($backend.SourceDatabase + $suffix)
            if (Test-Path -LiteralPath $oldFile) {
                Rename-Item -LiteralPath $oldFile -NewName ($backend.Database + $suffix)
            }
        }
    }
}
New-Item -ItemType Directory -Path $previous | Out-Null

$composeServices = @('gateway','identity','catalog','booking','customer','notification')
$installed = @()
& "$PSScriptRoot\Compose.ps1" stop @composeServices
try {
    foreach ($backend in $backends) {
        $target = [IO.Path]::GetFullPath((Join-Path $projectRoot "services\$($backend.Folder)\data"))
        $allowed = [IO.Path]::GetFullPath((Join-Path $projectRoot "services\$($backend.Folder)")) + [IO.Path]::DirectorySeparatorChar
        if (-not $target.StartsWith($allowed, [StringComparison]::OrdinalIgnoreCase)) { throw 'Unexpected database target.' }
        $savedPath = Join-Path $previous $backend.Folder
        if (Test-Path -LiteralPath $target) { Move-Item -LiteralPath $target -Destination $savedPath }
        $installed += $backend.Folder
        Move-Item -LiteralPath (Join-Path $stagedRestore $backend.Folder) -Destination $target
    }
    & "$PSScriptRoot\Compose.ps1" cp (Join-Path $backupRoot 'auth-keys/.') identity:/keys/
    & "$PSScriptRoot\Compose.ps1" cp (Join-Path $backupRoot 'tour-images/.') catalog:/app/wwwroot/images/
    & "$PSScriptRoot\Compose.ps1" cp (Join-Path $backupRoot 'rabbitmq-definitions.json') rabbitmq:/tmp/tourbooking-definitions.json
    & "$PSScriptRoot\Compose.ps1" exec -T rabbitmq rabbitmqctl import_definitions /tmp/tourbooking-definitions.json
} catch {
    foreach ($folder in $installed) {
        $target = Join-Path $projectRoot "services\$folder\data"
        if (Test-Path -LiteralPath $target) { Move-Item -LiteralPath $target -Destination (Join-Path $stagedRestore "failed-$folder") }
        $savedPath = Join-Path $previous $folder
        if (Test-Path -LiteralPath $savedPath) { Move-Item -LiteralPath $savedPath -Destination $target }
    }
    throw
} finally {
    & "$PSScriptRoot\Compose.ps1" up -d --build --remove-orphans --force-recreate @composeServices
}
Write-Host "Restore complete; previous SQLite files retained at $previous"
if ($conversion) { Write-Host "Legacy backup conversion retained at $conversion" }
