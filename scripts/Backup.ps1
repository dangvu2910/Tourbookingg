param([string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
if (!$OutputDirectory) { $OutputDirectory = Join-Path $root ('backups/' + (Get-Date -Format 'yyyyMMdd-HHmmss')) }
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$OutputDirectory = (Resolve-Path -LiteralPath $OutputDirectory).Path
& "$PSScriptRoot/Compose.ps1" exec -T --user 0 sqlserver chown 10001:0 /var/opt/mssql/backup
foreach ($database in @('TourBookingDb', 'TourBookingNotifications')) {
    $sql = "BACKUP DATABASE [$database] TO DISK=N'/var/opt/mssql/backup/$database.bak' WITH INIT, CHECKSUM; RESTORE VERIFYONLY FROM DISK=N'/var/opt/mssql/backup/$database.bak' WITH CHECKSUM;"
    & "$PSScriptRoot/Invoke-Sql.ps1" -Query $sql
    & "$PSScriptRoot/Compose.ps1" cp "sqlserver:/var/opt/mssql/backup/$database.bak" (Join-Path $OutputDirectory "$database.bak")
}
& "$PSScriptRoot/Compose.ps1" exec -T rabbitmq rabbitmqctl export_definitions /tmp/tourbooking-definitions.json
& "$PSScriptRoot/Compose.ps1" cp rabbitmq:/tmp/tourbooking-definitions.json (Join-Path $OutputDirectory 'rabbitmq-definitions.json')
& "$PSScriptRoot/Compose.ps1" exec -T identity tar -czf /tmp/auth-keys.tar.gz -C /keys .
& "$PSScriptRoot/Compose.ps1" cp identity:/tmp/auth-keys.tar.gz (Join-Path $OutputDirectory 'auth-keys.tar.gz')
& "$PSScriptRoot/Compose.ps1" exec -T catalog tar -czf /tmp/tour-images.tar.gz -C /app/wwwroot/images .
& "$PSScriptRoot/Compose.ps1" cp catalog:/tmp/tour-images.tar.gz (Join-Path $OutputDirectory 'tour-images.tar.gz')
Get-ChildItem -LiteralPath $OutputDirectory -File | Get-FileHash -Algorithm SHA256 |
    Select-Object @{Name='File';Expression={Split-Path $_.Path -Leaf}}, Hash |
    ConvertTo-Json | Set-Content (Join-Path $OutputDirectory 'checksums.json') -Encoding utf8
Write-Host "Verified SQL backups and exported keys, images and broker definitions to $OutputDirectory"
