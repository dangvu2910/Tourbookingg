param(
    [ValidatePattern('^tourbooking-[a-z0-9-]+$')][string]$TargetProject = 'tourbooking-live',
    [string]$BackupDirectory,
    [switch]$Resume
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$compose = Join-Path $PSScriptRoot 'Compose.ps1'
$environmentFile = Join-Path $root '.env'
function Read-Setting([string]$name, [string]$fallback) {
    $value = $fallback
    foreach ($line in [IO.File]::ReadAllLines($environmentFile)) {
        if ($line.StartsWith($name + '=')) { $value = $line.Substring($name.Length + 1).Trim() }
    }
    return $value
}
function Set-Setting([string]$name, [string]$value) {
    $lines = @([IO.File]::ReadAllLines($environmentFile) | Where-Object { -not $_.StartsWith($name + '=') })
    $lines += "$name=$value"
    [IO.File]::WriteAllLines($environmentFile, $lines, [Text.UTF8Encoding]::new($false))
}
function Assert-Native([string]$operation) {
    if ($LASTEXITCODE -ne 0) { throw "$operation failed. Previous stack and SQLite sources are preserved." }
}
Push-Location $root
$cutover = $false
try {
    & "$PSScriptRoot/Initialize-Environment.ps1"
    $previousProject = Read-Setting 'HYBRID_PROJECT_NAME' 'tourbooking-hybrid'
    if ($previousProject -eq $TargetProject) { throw 'Choose a new target project; migration never overwrites the active stack.' }
    $existing = @(docker volume ls --filter "label=com.docker.compose.project=$TargetProject" --format '{{.Name}}')
    Assert-Native 'Destination volume inspection'
    if ($existing.Count -gt 0 -and -not $Resume) { throw 'Target volumes already exist. Use a new project name, or -Resume only for this same verified snapshot.' }
    if (-not $BackupDirectory) { $BackupDirectory = Join-Path $root ('backups/sqlite-to-hybrid-' + (Get-Date -Format 'yyyyMMdd-HHmmss')) }
    $BackupDirectory = [IO.Path]::GetFullPath($BackupDirectory)
    $backupBoundary = [IO.Path]::GetFullPath((Join-Path $root 'backups')) + [IO.Path]::DirectorySeparatorChar
    if (-not $BackupDirectory.StartsWith($backupBoundary, [StringComparison]::OrdinalIgnoreCase)) { throw 'Backup destination must stay inside the repository backups directory.' }
    $sqliteDirectory = if (Test-Path (Join-Path $BackupDirectory 'snapshot-manifest.json')) { $BackupDirectory } else { Join-Path $BackupDirectory 'sqlite' }
    if (-not (Test-Path (Join-Path $sqliteDirectory 'snapshot-manifest.json'))) {
        dotnet run --project tools/HybridMigration/HybridMigration.csproj -- snapshot $root $sqliteDirectory
        Assert-Native 'Consistent SQLite snapshot'
    }
    $reportDirectory = Join-Path $BackupDirectory 'reports'
    New-Item -ItemType Directory -Path $reportDirectory -Force | Out-Null
    Copy-Item -LiteralPath $environmentFile -Destination (Join-Path $BackupDirectory 'environment-before-migration.env')
    $env:HYBRID_MIGRATION_SOURCE = $sqliteDirectory.Replace('\', '/')
    $env:HYBRID_MIGRATION_REPORT = $reportDirectory.Replace('\', '/')
    $env:HYBRID_SEED_DEMO = 'false'
    $arguments = @('-p', $TargetProject, '-f', 'compose.hybrid.yaml')
    & $compose @arguments config --quiet
    & $compose @arguments up -d --wait --wait-timeout 180 sqlserver mongodb rabbitmq
    & $compose @arguments run --rm --no-deps sql-provision
    & $compose @arguments --profile migration build migrate
    & $compose @arguments --profile migration run --rm --no-deps migrate
    $report = Get-Content -Raw (Join-Path $reportDirectory 'migration-report.json') | ConvertFrom-Json
    if (-not $report.verified) { throw 'Content checks did not pass; refusing cutover.' }

    # Let Compose create correctly labelled volumes without starting application workers.
    & $compose @arguments up --no-start --no-deps identity catalog gateway
    foreach ($item in @(@{Source='tourbooking-distributed_auth-keys'; Target='hybrid-keys'; Folder='auth-keys'},
                        @{Source='tourbooking-distributed_tour-images'; Target='hybrid-images'; Folder='tour-images'})) {
        docker volume inspect $item.Source --format '{{.Name}}' | Out-Null
        Assert-Native 'Legacy assets volume inspection'
        $assetsBackup = Join-Path $BackupDirectory $item.Folder
        New-Item -ItemType Directory -Path $assetsBackup -Force | Out-Null
        docker run --rm --mount "type=volume,source=$($item.Source),target=/from,readonly" `
            --mount "type=volume,source=$($TargetProject)_$($item.Target),target=/to" `
            --mount "type=bind,source=$assetsBackup,target=/backup" mcr.microsoft.com/dotnet/aspnet:10.0 `
            sh -c 'cp -a /from/. /to/ && cp -a /from/. /backup/ && chown -R app:app /to'
        Assert-Native 'Legacy keys/images copy'
    }

    & $compose -p $previousProject -f compose.hybrid.yaml stop
    Set-Setting 'HYBRID_PROJECT_NAME' $TargetProject
    Set-Setting 'HYBRID_SEED_DEMO' 'false'
    $cutover = $true
    & $compose @arguments up -d --wait --wait-timeout 180
    & $compose @arguments --profile migration run --rm --no-deps migrate verify /source /report/post-start-verification.json
    & $compose @arguments --profile migration run --rm --no-deps migrate storage /report/storage-locations.json
    $catalog = Invoke-RestMethod 'http://localhost:8080/api/v1/catalog'
    if (-not $catalog.success -or $catalog.meta.pagination.total_items -ne $report.databases.CatalogDB.Tours.sourceCount) {
        throw 'Kong catalog verification failed after cutover.'
    }
    Write-Host "Verified SQLite migration completed. Active project: $TargetProject"
    Write-Host "Backup and verification reports: $BackupDirectory"
    Write-Host 'Kong: http://localhost:8080. Existing imported accounts retain their original passwords.'
} catch {
    if ($cutover) {
        & $compose -p $TargetProject -f compose.hybrid.yaml stop
        Copy-Item -LiteralPath (Join-Path $BackupDirectory 'environment-before-migration.env') -Destination $environmentFile -Force
        & $compose -p $previousProject -f compose.hybrid.yaml up -d --wait --wait-timeout 180
        Write-Warning 'Cutover failed; restored the previous stack. Imported volumes and source backups remain available.'
    }
    throw
} finally {
    Remove-Item Env:HYBRID_MIGRATION_SOURCE -ErrorAction SilentlyContinue
    Remove-Item Env:HYBRID_MIGRATION_REPORT -ErrorAction SilentlyContinue
    Remove-Item Env:HYBRID_SEED_DEMO -ErrorAction SilentlyContinue
    Pop-Location
}
