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
foreach ($service in @('identity','catalog','booking','payment','review','support','reporting','notification')) {
    if (-not (Test-Path -LiteralPath (Join-Path $backupRoot "sqlite/$service/$service.db"))) { throw "Missing $service database." }
}
$dataRoot = [IO.Path]::GetFullPath((Join-Path $projectRoot 'data'))
New-Item -ItemType Directory -Path $dataRoot -Force | Out-Null
$previous = Join-Path $dataRoot ('sqlite-before-restore-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
$stagedRestore = Join-Path $dataRoot ('sqlite-restore-staging-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
if (Test-Path -LiteralPath $stagedRestore) { throw 'Restore staging directory already exists.' }
if (Test-Path -LiteralPath $previous) { throw 'Previous-data destination already exists.' }
$serviceNames = @('Identity','Catalog','Booking','Payment','Review','Support','Reporting','Notification')
$restoreTargets = foreach ($serviceName in $serviceNames) {
    $target = [IO.Path]::GetFullPath((Join-Path $projectRoot "services/$serviceName/data"))
    $allowedServiceRoot = [IO.Path]::GetFullPath((Join-Path $projectRoot "services/$serviceName")) + [IO.Path]::DirectorySeparatorChar
    if (-not $target.StartsWith($allowedServiceRoot, [StringComparison]::OrdinalIgnoreCase)) { throw 'Unexpected database target.' }
    [PSCustomObject]@{ Current = $target; Name = $serviceName.ToLowerInvariant() }
}
Copy-Item -LiteralPath (Join-Path $backupRoot 'sqlite') -Destination $stagedRestore -Recurse
New-Item -ItemType Directory -Path $previous | Out-Null
& "$PSScriptRoot/Compose.ps1" stop gateway identity catalog booking payment review support reporting notification
try {
    foreach ($target in $restoreTargets) {
        $savedPath = [IO.Path]::GetFullPath((Join-Path $previous $target.Name))
        $stagedPath = [IO.Path]::GetFullPath((Join-Path $stagedRestore $target.Name))
        if (-not $savedPath.StartsWith($previous + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -or -not $stagedPath.StartsWith($stagedRestore + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Unexpected staging path.' }
        if (Test-Path -LiteralPath $target.Current) { Move-Item -LiteralPath $target.Current -Destination $savedPath }
        Move-Item -LiteralPath $stagedPath -Destination $target.Current
    }
    & "$PSScriptRoot/Compose.ps1" cp (Join-Path $backupRoot 'auth-keys/.') identity:/keys/
    & "$PSScriptRoot/Compose.ps1" cp (Join-Path $backupRoot 'tour-images/.') catalog:/app/wwwroot/images/
    & "$PSScriptRoot/Compose.ps1" cp (Join-Path $backupRoot 'rabbitmq-definitions.json') rabbitmq:/tmp/tourbooking-definitions.json
    & "$PSScriptRoot/Compose.ps1" exec -T rabbitmq rabbitmqctl import_definitions /tmp/tourbooking-definitions.json
} finally {
    # Recreate bind mounts after replacing host directories; start alone can retain the previous mount.
    & "$PSScriptRoot/Compose.ps1" up -d --no-build --force-recreate identity catalog booking payment review support reporting notification gateway
}
Write-Host "Restore complete; previous SQLite files retained at $previous"
