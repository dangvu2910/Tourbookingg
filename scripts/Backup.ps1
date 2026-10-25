param([string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $projectRoot ('backups/sqlite-' + (Get-Date -Format 'yyyyMMdd-HHmmss')) }
if ((Test-Path -LiteralPath $OutputDirectory) -and (Get-ChildItem -LiteralPath $OutputDirectory -Force | Select-Object -First 1)) { throw 'Backup directory must be empty.' }
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$backupRoot = (Resolve-Path -LiteralPath $OutputDirectory).Path
$serviceNames = @('Identity','Catalog','Booking','Payment','Review','Support','Reporting','Notification')
foreach ($serviceName in $serviceNames) {
    if (-not (Test-Path -LiteralPath (Join-Path $projectRoot "services/$serviceName/data/$($serviceName.ToLowerInvariant()).db"))) { throw "Missing database: $serviceName" }
}
& "$PSScriptRoot/Compose.ps1" stop gateway identity catalog booking payment review support reporting notification
try {
    New-Item -ItemType Directory -Path (Join-Path $backupRoot 'sqlite') | Out-Null
    foreach ($serviceName in $serviceNames) {
        Copy-Item -LiteralPath (Join-Path $projectRoot "services/$serviceName/data") -Destination (Join-Path $backupRoot "sqlite/$($serviceName.ToLowerInvariant())") -Recurse
    }
    & "$PSScriptRoot/Compose.ps1" cp identity:/keys (Join-Path $backupRoot 'auth-keys')
    & "$PSScriptRoot/Compose.ps1" cp catalog:/app/wwwroot/images (Join-Path $backupRoot 'tour-images')
    & "$PSScriptRoot/Compose.ps1" exec -T rabbitmq rabbitmqctl export_definitions /tmp/tourbooking-definitions.json
    & "$PSScriptRoot/Compose.ps1" cp rabbitmq:/tmp/tourbooking-definitions.json (Join-Path $backupRoot 'rabbitmq-definitions.json')
    $manifest = @(Get-ChildItem -LiteralPath $backupRoot -Recurse -File | ForEach-Object {
        [PSCustomObject]@{ File = $_.FullName.Substring($backupRoot.Length + 1); Hash = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash }
    })
    ConvertTo-Json -InputObject $manifest | Set-Content -LiteralPath (Join-Path $backupRoot 'checksums.json') -Encoding utf8
} finally { & "$PSScriptRoot/Compose.ps1" start identity catalog booking payment review support reporting notification gateway }
Write-Host "SQLite, keys, images and broker definitions backed up to $backupRoot"
