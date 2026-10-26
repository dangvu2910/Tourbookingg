param([string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $projectRoot ('backups/sqlite-' + (Get-Date -Format 'yyyyMMdd-HHmmss')) }
if ((Test-Path -LiteralPath $OutputDirectory) -and (Get-ChildItem -LiteralPath $OutputDirectory -Force | Select-Object -First 1)) { throw 'Backup directory must be empty.' }
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$backupRoot = (Resolve-Path -LiteralPath $OutputDirectory).Path
$groupedBackends = @(
    @{ Name='identity'; Folder='Identity'; Database='identity' },
    @{ Name='catalog'; Folder='Catalog'; Database='catalog' },
    @{ Name='booking-platform'; Folder='BookingPlatform'; Database='booking-platform' },
    @{ Name='customer-experience'; Folder='CustomerExperience'; Database='customer-experience' },
    @{ Name='notification'; Folder='Notification'; Database='notification' }
)
$legacyBackends = @(
    @{ Name='identity'; Folder='Identity'; Database='identity' },
    @{ Name='catalog'; Folder='Catalog'; Database='catalog' },
    @{ Name='booking'; Folder='Booking'; Database='booking' },
    @{ Name='payment'; Folder='Payment'; Database='payment' },
    @{ Name='review'; Folder='Review'; Database='review' },
    @{ Name='support'; Folder='Support'; Database='support' },
    @{ Name='reporting'; Folder='Reporting'; Database='reporting' },
    @{ Name='notification'; Folder='Notification'; Database='notification' }
)
$groupedReady = (Test-Path -LiteralPath (Join-Path $projectRoot 'services\BookingPlatform\data\booking-platform.db')) `
    -and (Test-Path -LiteralPath (Join-Path $projectRoot 'services\CustomerExperience\data\customer-experience.db'))
$backends = if ($groupedReady) { $groupedBackends } else { $legacyBackends }
foreach ($backend in $backends) {
    if (-not (Test-Path -LiteralPath (Join-Path $projectRoot "services\$($backend.Folder)\data\$($backend.Database).db"))) {
        throw "Missing database: $($backend.Name)"
    }
}
$containerIds = @(docker ps --format '{{.ID}}|{{.Names}}' | ForEach-Object {
    $parts = $_ -split '\|', 2
    if ($parts[1] -match '^tourbooking-distributed-(gateway|identity|catalog|booking|payment|review|support|reporting|notification|booking-platform|customer-experience)-1$') { $parts[0] }
})
$stopped = $false
try {
    if ($containerIds.Count -gt 0) {
        docker stop $containerIds | Out-Null
        if ($LASTEXITCODE -ne 0) { throw 'Could not stop all application containers.' }
    }
    $stopped = $true
    New-Item -ItemType Directory -Path (Join-Path $backupRoot 'sqlite') | Out-Null
    foreach ($backend in $backends) {
        Copy-Item -LiteralPath (Join-Path $projectRoot "services\$($backend.Folder)\data") -Destination (Join-Path $backupRoot "sqlite\$($backend.Name)") -Recurse
    }
    & "$PSScriptRoot\Compose.ps1" cp identity:/keys (Join-Path $backupRoot 'auth-keys')
    & "$PSScriptRoot\Compose.ps1" cp catalog:/app/wwwroot/images (Join-Path $backupRoot 'tour-images')
    & "$PSScriptRoot\Compose.ps1" exec -T rabbitmq rabbitmqctl export_definitions /tmp/tourbooking-definitions.json
    & "$PSScriptRoot\Compose.ps1" cp rabbitmq:/tmp/tourbooking-definitions.json (Join-Path $backupRoot 'rabbitmq-definitions.json')
    $manifest = @(Get-ChildItem -LiteralPath $backupRoot -Recurse -File | ForEach-Object {
        [PSCustomObject]@{ File = $_.FullName.Substring($backupRoot.Length + 1); Hash = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash }
    })
    ConvertTo-Json -InputObject $manifest | Set-Content -LiteralPath (Join-Path $backupRoot 'checksums.json') -Encoding utf8
} finally {
    if ($stopped -and $containerIds.Count -gt 0) {
        docker start $containerIds | Out-Null
        if ($LASTEXITCODE -ne 0) { throw 'Backup completed, but one or more previously running app containers did not restart.' }
    }
}
Write-Host "SQLite, keys, images and broker definitions backed up to $backupRoot"
