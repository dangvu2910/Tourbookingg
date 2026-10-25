param([Parameter(Mandatory=$true)][DateTime]$SinceUtc, [Parameter(Mandatory=$true)][ValidateSet('identity','catalog','booking','payment','review','support')][string]$Service)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
$serviceFolder = [cultureinfo]::InvariantCulture.TextInfo.ToTitleCase($Service)
$databasePath = Join-Path $projectRoot "services/$serviceFolder/data/$Service.db"
if (-not (Test-Path -LiteralPath $databasePath)) { throw 'SQLite database does not exist.' }
& "$PSScriptRoot/Compose.ps1" stop $Service
try {
    & dotnet run --project (Join-Path $projectRoot 'tools/SqliteMigration/SqliteMigration.csproj') -- --replay $Service $SinceUtc.ToUniversalTime().ToString('o') $databasePath
    if ($LASTEXITCODE -ne 0) { throw 'Replay failed.' }
} finally { & "$PSScriptRoot/Compose.ps1" start $Service }
