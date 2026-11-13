<#
.SYNOPSIS
  Restore a backup made by Backup.ps1 into the hybrid stack.

  -Drill  (safe, recommended regularly): restores every database under a *_RestoreDrill name, compares
          row/document counts with manifest.json, checks files.tar.gz, then drops the copies.
          Live data and running services are not touched.

  -Force  (destructive): overwrites the live databases and volumes with the backup.
          1. verifies SHA256SUMS, 2. takes a safety backup to backups\pre-restore-<timestamp>
          (skip with -SkipSafetyBackup), 3. stops the app containers, 4. restores SQL (WITH REPLACE,
          re-maps the *_app users), Mongo (--drop) and the key/image volumes, 5. starts the apps and
          waits until they are healthy. Data written after the backup is lost (it stays in the safety backup).

.EXAMPLE
  .\scripts\Restore.ps1 -BackupDirectory backups\hybrid-20261113-103721 -Drill
  .\scripts\Restore.ps1 -BackupDirectory backups\hybrid-20261113-103721 -Force
  .\scripts\Restore.ps1 -BackupDirectory backups\hybrid-20261113-103721 -Force -Only sql -Database BookingDB
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$BackupDirectory,
    [switch]$Drill,
    [switch]$Force,
    [ValidateSet('sql', 'mongo', 'files')][string[]]$Only = @('sql', 'mongo', 'files'),
    [string[]]$Database,
    [switch]$SkipSafetyBackup,
    [string]$Project
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'BackupCommon.ps1')

if ($Drill -eq $Force) { throw 'Choose exactly one of -Drill (safe check) or -Force (overwrite live data).' }
$root = Split-Path $PSScriptRoot -Parent
$Project = Resolve-HybridProject $root $Project
if (-not [System.IO.Path]::IsPathRooted($BackupDirectory)) { $BackupDirectory = Join-Path $root $BackupDirectory }
$BackupDirectory = (Resolve-Path -LiteralPath $BackupDirectory).Path

function ConvertTo-Map($object) {
    $map = [ordered]@{}
    if ($object) { foreach ($p in $object.PSObject.Properties) { $map[$p.Name] = $p.Value } }
    $map
}

Write-Step "Verify checksums in $BackupDirectory"
Test-Checksums $BackupDirectory
$manifest = Get-Content -LiteralPath (Join-Path $BackupDirectory 'manifest.json') -Raw | ConvertFrom-Json
if ($manifest.format -ne 'tourbooking-hybrid-backup/1') { throw "Unknown backup format '$($manifest.format)'" }
$sqlDbs = @((ConvertTo-Map $manifest.sql).Keys | Where-Object { -not $Database -or $_ -in $Database })
$mongoDbs = @((ConvertTo-Map $manifest.mongo).Keys | Where-Object { -not $Database -or $_ -in $Database })
if ($Database -and -not ($sqlDbs + $mongoDbs)) { throw "None of '$($Database -join ', ')' is in this backup." }
if ($Database) { $Only = @($Only | Where-Object { $_ -ne 'files' }) }
Write-Step ("Backup of {0} taken {1} (commit {2}); mode {3}" -f $manifest.project, $manifest.createdAt, $manifest.gitCommit, $(if ($Drill) { 'DRILL' } else { 'RESTORE' }))

$sql = Get-ServiceContainer $Project 'sqlserver'
$mongo = Get-ServiceContainer $Project 'mongodb'
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$inner = "/var/opt/mssql/backup/restore-$stamp"
$failures = 0
$stopped = @()

function Restore-SqlDatabase([string]$Db, [string]$Target) {
    $file = "$inner/$Db.bak"
    Copy-ToContainer (Join-Path $BackupDirectory "$Db.bak") $sql $file
    if ($Target -eq $Db) {
        $query = "SET NOCOUNT ON; IF DB_ID(N'$Db') IS NOT NULL ALTER DATABASE [$Db] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; " +
                 "RESTORE DATABASE [$Db] FROM DISK = N'$file' WITH REPLACE, CHECKSUM, RECOVERY; " +
                 "ALTER DATABASE [$Db] SET MULTI_USER;"
        try { Invoke-Sql $sql $query | Out-Null }
        catch { Invoke-Sql $sql "IF DB_ID(N'$Db') IS NOT NULL ALTER DATABASE [$Db] SET MULTI_USER;" | Out-Null; throw }
        # Same login names on this server: re-map database users to the server logins (handles a new server too).
        $fix = ($SqlAppLogins | ForEach-Object { "IF DATABASE_PRINCIPAL_ID(N'$_') IS NOT NULL AND SUSER_ID(N'$_') IS NOT NULL ALTER USER [$_] WITH LOGIN = [$_];" }) -join ' '
        Invoke-Sql $sql "SET NOCOUNT ON; USE [$Db]; $fix" | Out-Null
        return
    }
    $moves = foreach ($line in Invoke-Sql $sql "RESTORE FILELISTONLY FROM DISK = N'$file';") {
        $cols = $line -split '\|'
        if ($cols.Count -ge 3 -and $cols[2] -in 'D', 'L') {
            $ext = if ($cols[2] -eq 'D') { 'mdf' } else { 'ldf' }
            "MOVE N'$($cols[0])' TO N'/var/opt/mssql/data/${Target}_$($cols[0]).$ext'"
        }
    }
    if (-not $moves) { throw "Could not read the file list of $Db.bak" }
    Invoke-Sql $sql ("SET NOCOUNT ON; IF DB_ID(N'$Target') IS NOT NULL DROP DATABASE [$Target]; " +
        "RESTORE DATABASE [$Target] FROM DISK = N'$file' WITH CHECKSUM, RECOVERY, " + ($moves -join ', ') + ';') | Out-Null
}

function Restore-MongoDatabase([string]$Db, [string]$Target) {
    $tmp = "/tmp/restore-$stamp-$Db.archive.gz"
    Copy-ToContainer (Join-Path $BackupDirectory "$Db.archive.gz") $mongo $tmp
    try {
        $script = 'mongorestore --quiet -u "$MONGO_INITDB_ROOT_USERNAME" -p "$MONGO_INITDB_ROOT_PASSWORD" --authenticationDatabase admin --gzip --archive="$1" --drop --nsInclude="$2.*" --nsFrom="$2.*" --nsTo="$3.*"'
        Invoke-Container $mongo @('bash', '-c', $script, '_', $tmp, $Db, $Target) | Out-Null
    } finally { Invoke-Container $mongo @('rm', '-f', $tmp) -AllowFailure | Out-Null }
}

function Wait-Healthy([string]$Container) {
    $deadline = (Get-Date).AddMinutes(4)
    while ((Get-Date) -lt $deadline) {
        $state = (Invoke-Docker @('inspect', '-f', '{{if .State.Health}}{{.State.Health.Status}}{{else}}{{.State.Status}}{{end}}', $Container) | Select-Object -First 1).Trim()
        if ($state -in 'healthy', 'running') { return }
        Start-Sleep -Seconds 3
    }
    throw "$Container did not become healthy in time"
}

try {
    if ($Force) {
        if (-not $SkipSafetyBackup) {
            Write-Step 'Safety backup of the current data first'
            & (Join-Path $PSScriptRoot 'Backup.ps1') -OutputDirectory (Join-Path $root "backups\pre-restore-$stamp") -Project $Project
        }
        foreach ($service in $AppServices) {
            $c = Get-ServiceContainer $Project $service -Optional
            if ($c) { Write-Step "Stop $c"; Invoke-Docker @('stop', '-t', '30', $c) | Out-Null; $stopped += $c }
        }
    }

    if ('sql' -in $Only -and $sqlDbs) {
        Invoke-Container $sql @('mkdir', '-p', $inner) | Out-Null
        try {
            foreach ($db in $sqlDbs) {
                $target = if ($Drill) { "${db}_RestoreDrill" } else { $db }
                Write-Step "SQL  $db -> $target"
                Restore-SqlDatabase $db $target
                $expected = ConvertTo-Map $manifest.sql.$db.rows
                $failures += Compare-Counts "SQL $db" $expected (Get-SqlRowCounts $sql $target)
                if ($Drill) { Invoke-Sql $sql "SET NOCOUNT ON; ALTER DATABASE [$target] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [$target];" | Out-Null }
            }
        } finally { Invoke-Container $sql @('rm', '-rf', $inner) -AllowFailure | Out-Null }
    }

    if ('mongo' -in $Only) {
        foreach ($db in $mongoDbs) {
            $target = if ($Drill) { "${db}_RestoreDrill" } else { $db }
            Write-Step "Mongo $db -> $target"
            Restore-MongoDatabase $db $target
            $failures += Compare-Counts "Mongo $db" (ConvertTo-Map $manifest.mongo.$db.documents) (Get-MongoCounts $mongo $target)
            if ($Drill) { Invoke-Mongo $mongo $target 'db.dropDatabase()' | Out-Null }
        }
    }

    if ('files' -in $Only) {
        $toolImage = Get-ToolImage $Project
        $archive = @('--mount', "type=bind,source=$BackupDirectory,target=/backup,readonly")
        $entries = @(Invoke-Docker (@('run', '--rm', '--entrypoint', 'tar') + $archive + @($toolImage, 'tzf', '/backup/files.tar.gz')) |
            Where-Object { $_ -and -not $_.EndsWith('/') })
        if ($entries.Count -ne [int]$manifest.files.entries) { $failures++; Write-Warning "files.tar.gz: backup=$($manifest.files.entries) entries, readable=$($entries.Count)" }
        if ($Force) {
            Write-Step 'Files -> volumes hybrid-keys + hybrid-images'
            Invoke-Docker (@('run', '--rm', '--user', '0', '--entrypoint', 'tar') + $archive + @(
                '--mount', "type=volume,source=$(Get-ProjectVolume $Project 'hybrid-keys'),target=/dst/keys",
                '--mount', "type=volume,source=$(Get-ProjectVolume $Project 'hybrid-images'),target=/dst/images",
                $toolImage, 'xzf', '/backup/files.tar.gz', '-C', '/dst')) | Out-Null
        } else { Write-Step "Files: $($entries.Count) entries readable" }
    }
} finally {
    if ($stopped) {
        [array]::Reverse($stopped)
        foreach ($c in $stopped) { Write-Step "Start $c"; Invoke-Docker @('start', $c) | Out-Null; Wait-Healthy $c }
    }
}

if ($failures) { Write-Warning "$failures count mismatch(es). If the stack was writing during the backup this can be expected; otherwise investigate."; exit 1 }
Write-Step $(if ($Drill) { 'DRILL PASSED: every database restored with the expected counts; drill copies dropped.' } else { 'RESTORE COMPLETED and services are healthy.' })
