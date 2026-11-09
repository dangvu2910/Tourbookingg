# Build updated backend images and migration tool before running this maintenance step.
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$compose = Join-Path $PSScriptRoot 'Compose.ps1'
$files = @('-f','compose.hybrid.yaml','-f','compose.admin.yaml','-f','compose.sqlexpress.yaml')
$backup = Join-Path $root ('backups/sql-read-trim-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
Push-Location $root
try {
    & $compose @files config --quiet
    & $compose @files stop kong gateway identity catalog booking customer
    try {
        # Windows PowerShell must allow the CLI to finish emitting a safe SQL error code.
        $ErrorActionPreference = 'Continue'
        dotnet run --no-build --project tools/HybridMigration/HybridMigration.csproj -- native-prune $root $backup
        $result = $LASTEXITCODE
        $ErrorActionPreference = 'Stop'
        if ($result -ne 0) { throw 'Read-table cleanup failed; check the backup receipt and log.' }
    } finally {
        & $compose @files up -d --no-build --wait --wait-timeout 180
        & $compose @files restart kong
    }
    $ok = $false
    for ($attempt=0; $attempt -lt 15; $attempt++) {
        try { $response = Invoke-RestMethod 'http://localhost:8080/api/v1/catalog'; if ($response.success) { $ok=$true; break } } catch { }
        Start-Sleep -Seconds 1
    }
    if (-not $ok) { throw 'Kong API did not recover.' }
    Write-Host "Read-table cleanup verified. Backup/report: $backup"
} finally { Pop-Location }
