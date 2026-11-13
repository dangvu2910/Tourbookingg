<#
.SYNOPSIS
  Online backup of the running hybrid stack (compose.hybrid.yaml), without stopping anything.

  - SQL Server: BACKUP DATABASE ... COPY_ONLY, CHECKSUM (+ COMPRESSION when the edition supports it),
    then RESTORE VERIFYONLY, for IdentityDB, CatalogDB, BookingDB, CustomerDB.
  - MongoDB: mongodump --gzip --archive for ChatDB and NotificationDB.
  - Files: volumes hybrid-keys (cookie/DataProtection key ring) and hybrid-images (tour images).
  - manifest.json (row/document counts, sizes) + SHA256SUMS. Old backups pruned after -KeepDays.

  RabbitMQ is not backed up: messages are transient, the SQL outbox is the source of truth.
  Output contains the key ring and user data: it stays under backups/ (gitignored); never commit it.

.EXAMPLE
  .\scripts\Backup.ps1                 # -> backups\hybrid-<timestamp>\
  .\scripts\Backup.ps1 -KeepDays 0     # keep every backup
#>
[CmdletBinding()]
param(
    [string]$OutputDirectory,
    [int]$KeepDays = 14,
    [string]$Project
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'BackupCommon.ps1')

$root = Split-Path $PSScriptRoot -Parent
$Project = Resolve-HybridProject $root $Project
$backupRoot = Join-Path $root 'backups'
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$auto = -not $OutputDirectory
if ($auto) { $OutputDirectory = Join-Path $backupRoot "hybrid-$stamp" }
if ((Test-Path -LiteralPath $OutputDirectory) -and (Get-ChildItem -LiteralPath $OutputDirectory -Force | Select-Object -First 1)) {
    throw "Backup directory must be empty: $OutputDirectory"
}
New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
$OutputDirectory = (Resolve-Path -LiteralPath $OutputDirectory).Path
New-Item -ItemType Directory -Force -Path (Join-Path $backupRoot 'logs') | Out-Null
$log = Join-Path $backupRoot "logs\backup-$stamp.log"
Start-Transcript -Path $log -Append | Out-Null
$ok = $false
try {
    Write-Step "Backup of project '$Project' -> $OutputDirectory"
    $sql = Get-ServiceContainer $Project 'sqlserver'
    $mongo = Get-ServiceContainer $Project 'mongodb'
    $manifest = [ordered]@{
        format = 'tourbooking-hybrid-backup/1'
        createdAt = (Get-Date).ToString('o')
        project = $Project
        gitCommit = (git -C $root rev-parse --short HEAD 2>$null)
        sql = [ordered]@{}; mongo = [ordered]@{}; files = $null
    }

    # ---- SQL Server -----------------------------------------------------------------------
    $inner = "/var/opt/mssql/backup/tourbooking-$stamp"
    Invoke-Container $sql @('mkdir', '-p', $inner) | Out-Null
    try {
        foreach ($db in $SqlDatabases) {
            Write-Step "SQL  $db"
            $file = "$inner/$db.bak"
            $query = "SET NOCOUNT ON; DECLARE @opt nvarchar(40) = CASE WHEN CAST(SERVERPROPERTY('EngineEdition') AS int) = 4 THEN N'' ELSE N', COMPRESSION' END; " +
                     "DECLARE @cmd nvarchar(max) = N'BACKUP DATABASE [$db] TO DISK = N''$file'' WITH COPY_ONLY, INIT, CHECKSUM' + @opt + N';'; EXEC (@cmd); " +
                     "RESTORE VERIFYONLY FROM DISK = N'$file' WITH CHECKSUM;"
            Invoke-Sql $sql $query | Out-Null
            $rows = Get-SqlRowCounts $sql $db
            Copy-FromContainer $sql $file (Join-Path $OutputDirectory "$db.bak")
            $manifest.sql[$db] = [ordered]@{ file = "$db.bak"; bytes = (Get-Item (Join-Path $OutputDirectory "$db.bak")).Length; rows = $rows }
        }
    } finally { Invoke-Container $sql @('rm', '-rf', $inner) -AllowFailure | Out-Null }

    # ---- MongoDB --------------------------------------------------------------------------
    foreach ($db in $MongoDatabases) {
        Write-Step "Mongo $db"
        $tmp = "/tmp/tourbooking-$stamp-$db.archive.gz"
        try {
            Invoke-Container $mongo @('bash', '-c', 'mongodump --quiet -u "$MONGO_INITDB_ROOT_USERNAME" -p "$MONGO_INITDB_ROOT_PASSWORD" --authenticationDatabase admin --db "$1" --gzip --archive="$2"', '_', $db, $tmp) | Out-Null
            $counts = Get-MongoCounts $mongo $db
            Copy-FromContainer $mongo $tmp (Join-Path $OutputDirectory "$db.archive.gz")
        } finally { Invoke-Container $mongo @('rm', '-f', $tmp) -AllowFailure | Out-Null }
        $manifest.mongo[$db] = [ordered]@{ file = "$db.archive.gz"; bytes = (Get-Item (Join-Path $OutputDirectory "$db.archive.gz")).Length; documents = $counts }
    }

    # ---- Volumes: key ring + tour images ----------------------------------------------------
    Write-Step 'Files hybrid-keys + hybrid-images'
    $keys = Get-ProjectVolume $Project 'hybrid-keys'
    $images = Get-ProjectVolume $Project 'hybrid-images'
    $toolImage = Get-ToolImage $Project
    Invoke-Docker @('run', '--rm', '--user', '0', '--entrypoint', 'tar',
        '--mount', "type=volume,source=$keys,target=/src/keys,readonly",
        '--mount', "type=volume,source=$images,target=/src/images,readonly",
        '--mount', "type=bind,source=$OutputDirectory,target=/backup",
        $toolImage, 'czf', '/backup/files.tar.gz', '-C', '/src', 'keys', 'images') | Out-Null
    $entries = @(Invoke-Docker @('run', '--rm', '--entrypoint', 'tar', '--mount', "type=bind,source=$OutputDirectory,target=/backup,readonly",
        $toolImage, 'tzf', '/backup/files.tar.gz') | Where-Object { $_ -and -not $_.EndsWith('/') })
    $manifest.files = [ordered]@{ file = 'files.tar.gz'; bytes = (Get-Item (Join-Path $OutputDirectory 'files.tar.gz')).Length; entries = $entries.Count }

    # ---- Manifest + checksums ------------------------------------------------------------
    $manifest | ConvertTo-Json -Depth 6 | Set-Content -Encoding utf8 (Join-Path $OutputDirectory 'manifest.json')
    Write-Checksums $OutputDirectory
    $ok = $true

    $size = (Get-ChildItem $OutputDirectory -File | Measure-Object Length -Sum).Sum
    Write-Step ("Done: {0} files, {1:N1} MB, SQL {2} DB, Mongo {3} DB, {4} key/image files" -f `
        (Get-ChildItem $OutputDirectory -File).Count, ($size / 1MB), $manifest.sql.Count, $manifest.mongo.Count, $entries.Count)

    if ($auto -and $KeepDays -gt 0) {
        $limit = (Get-Date).AddDays(-$KeepDays)
        Get-ChildItem $backupRoot -Directory -Filter 'hybrid-*' |
            Where-Object { $_.FullName -ne $OutputDirectory -and $_.CreationTime -lt $limit -and (Test-Path (Join-Path $_.FullName 'manifest.json')) } |
            ForEach-Object { Write-Step "Prune old backup $($_.Name)"; Remove-Item -LiteralPath $_.FullName -Recurse -Force }
    }
} finally {
    if (-not $ok) { Write-Warning "Backup FAILED; incomplete output kept for inspection: $OutputDirectory" }
    Stop-Transcript | Out-Null
}
