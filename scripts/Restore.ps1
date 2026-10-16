param([Parameter(Mandatory=$true)][string]$BackupDirectory, [switch]$Apply)
$ErrorActionPreference = 'Stop'
if (!$Apply) { throw 'Restore replaces current data. Back up first, then pass -Apply to perform the restore.' }
$directory = (Resolve-Path -LiteralPath $BackupDirectory).Path
$manifest = Get-Content -LiteralPath (Join-Path $directory 'checksums.json') -Raw | ConvertFrom-Json
foreach ($required in @('TourBookingDb.bak', 'TourBookingNotifications.bak', 'rabbitmq-definitions.json', 'auth-keys.tar.gz', 'tour-images.tar.gz')) {
    if (!($manifest | Where-Object File -eq $required)) { throw "Backup manifest is missing $required" }
}
foreach ($entry in $manifest) {
    if ($entry.File -ne (Split-Path $entry.File -Leaf)) { throw 'Invalid file name in backup manifest.' }
    if ((Get-FileHash -LiteralPath (Join-Path $directory $entry.File) -Algorithm SHA256).Hash -ne $entry.Hash) { throw "Checksum mismatch: $($entry.File)" }
}
& "$PSScriptRoot/Compose.ps1" stop gateway booking catalog identity notification
try {
    foreach ($database in @('TourBookingDb', 'TourBookingNotifications')) {
        & "$PSScriptRoot/Compose.ps1" cp (Join-Path $directory "$database.bak") "sqlserver:/var/opt/mssql/backup/$database.bak"
        $sql = "ALTER DATABASE [$database] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; RESTORE DATABASE [$database] FROM DISK=N'/var/opt/mssql/backup/$database.bak' WITH REPLACE, CHECKSUM; ALTER DATABASE [$database] SET MULTI_USER;"
        & "$PSScriptRoot/Invoke-Sql.ps1" -Query $sql
    }
    & "$PSScriptRoot/Compose.ps1" cp (Join-Path $directory 'rabbitmq-definitions.json') rabbitmq:/tmp/tourbooking-definitions.json
    & "$PSScriptRoot/Compose.ps1" exec -T rabbitmq rabbitmqctl import_definitions /tmp/tourbooking-definitions.json
    # docker cp works with stopped containers and avoids Windows-to-Linux bind-mount paths.
    & "$PSScriptRoot/Compose.ps1" cp (Join-Path $directory 'auth-keys.tar.gz') identity:/keys/.restore-keys.tar.gz
    & "$PSScriptRoot/Compose.ps1" cp (Join-Path $directory 'tour-images.tar.gz') catalog:/app/wwwroot/images/.restore-images.tar.gz
    & "$PSScriptRoot/Compose.ps1" run --rm --no-deps --entrypoint sh identity -c 'tar -xzf /keys/.restore-keys.tar.gz -C /keys && rm -f /keys/.restore-keys.tar.gz'
    & "$PSScriptRoot/Compose.ps1" run --rm --no-deps --entrypoint sh catalog -c 'tar -xzf /app/wwwroot/images/.restore-images.tar.gz -C /app/wwwroot/images && rm -f /app/wwwroot/images/.restore-images.tar.gz'
} finally {
    & "$PSScriptRoot/Compose.ps1" up -d identity catalog booking notification gateway
}
Write-Host 'Restore complete. Run the workflow tests to verify the restored system.'
