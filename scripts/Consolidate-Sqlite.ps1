param(
    [Parameter(Mandatory=$true)][string]$BackupDirectory,
    [switch]$Apply
)

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
$backupRoot = [IO.Path]::GetFullPath((Join-Path $projectRoot $BackupDirectory))
if (-not (Test-Path -LiteralPath $backupRoot)) { throw 'Backup directory does not exist.' }
$manifestPath = Join-Path $backupRoot 'checksums.json'
if (-not (Test-Path -LiteralPath $manifestPath)) { throw 'Backup checksums.json is required.' }
foreach ($entry in (Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json)) {
    $filePath = [IO.Path]::GetFullPath((Join-Path $backupRoot $entry.File))
    if (-not $filePath.StartsWith($backupRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Invalid backup path.'
    }
    if ((Get-FileHash -LiteralPath $filePath -Algorithm SHA256).Hash -ne $entry.Hash) {
        throw "Backup checksum mismatch: $($entry.File)"
    }
}

$containerNames = @(docker ps --format '{{.Names}}')
$activeWriters = $containerNames | Where-Object {
    $_ -match '^tourbooking-distributed-(gateway|identity|catalog|booking|payment|review|support|reporting|notification|booking-platform|customer-experience)-1$'
}
if ($activeWriters) {
    throw "Stop all application containers before consolidating SQLite: $($activeWriters -join ', ')"
}

$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$staging = Join-Path $projectRoot "data\sqlite-consolidation-$stamp"
New-Item -ItemType Directory -Path $staging | Out-Null
& dotnet run --no-restore --project (Join-Path $projectRoot 'tools\SqliteMigration\SqliteMigration.csproj') -- --consolidate $projectRoot $staging
if ($LASTEXITCODE -ne 0) { throw 'SQLite consolidation failed; original databases were not changed.' }

$targets = @(
    @{ Name='identity'; Folder='Identity'; File='identity.db' },
    @{ Name='catalog'; Folder='Catalog'; File='catalog.db' },
    @{ Name='booking-platform'; Folder='booking'; File='booking-platform.db' },
    @{ Name='customer-experience'; Folder='customer'; File='customer-experience.db' },
    @{ Name='notification'; Folder='Notification'; File='notification.db' }
)
foreach ($target in $targets) {
    $source = Join-Path $staging "$($target.Name)\$($target.File)"
    if (-not (Test-Path -LiteralPath $source)) { throw "Staged database is missing: $($target.Name)" }
    if ($Apply -and $target.Name -in @('booking-platform','customer-experience')) {
        $targetDirectory = Join-Path $projectRoot "services\$($target.Folder)\data"
        if ((Test-Path -LiteralPath $targetDirectory) -and (Get-ChildItem -LiteralPath $targetDirectory -Force | Select-Object -First 1)) {
            throw "Target data directory is not empty: services\$($target.Folder)\data"
        }
    }
}

if (-not $Apply) {
    Write-Host "Staging verified. No active database was changed. Review: $staging\consolidation-report.json"
    Write-Host 'Rerun with -Apply and the same backup to install these databases and start the consolidated services.'
    return
}

$composeServices = @('gateway','identity','catalog','booking-platform','customer-experience','notification')
& "$PSScriptRoot\Compose.ps1" stop @composeServices
foreach ($target in $targets) {
    if ($target.Name -notin @('booking-platform','customer-experience')) { continue }
    $targetDirectory = Join-Path $projectRoot "services\$($target.Folder)\data"
    New-Item -ItemType Directory -Path $targetDirectory -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $staging "$($target.Name)\$($target.File)") -Destination $targetDirectory
}

& "$PSScriptRoot\Compose.ps1" up -d --build --remove-orphans
if ($LASTEXITCODE -ne 0) { throw 'Consolidated database files are installed, but Compose failed to start. Keep the backup and staging directory.' }
Write-Host "Consolidation installed. Verified staging retained at $staging"
