param([Parameter(Mandatory=$true)][DateTime]$SinceUtc, [Parameter(Mandatory=$true)][ValidateSet('identity','catalog','booking','payment','review','support')][string]$Service)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
$backend = switch ($Service) {
    { $_ -in 'payment','reporting' } { 'booking-platform'; break }
    { $_ -in 'review','support' } { 'customer-experience'; break }
    default { $Service }
}
$serviceFolder = switch ($backend) {
    'booking-platform' { 'BookingPlatform' }
    'customer-experience' { 'CustomerExperience' }
    default { [cultureinfo]::InvariantCulture.TextInfo.ToTitleCase($backend) }
}
$databasePath = Join-Path $projectRoot "services/$serviceFolder/data/$backend.db"
if (-not (Test-Path -LiteralPath $databasePath)) { throw 'SQLite database does not exist.' }
& "$PSScriptRoot/Compose.ps1" stop $backend
try {
    & dotnet run --project (Join-Path $projectRoot 'tools/SqliteMigration/SqliteMigration.csproj') -- --replay $Service $SinceUtc.ToUniversalTime().ToString('o') $databasePath
    if ($LASTEXITCODE -ne 0) { throw 'Replay failed.' }
} finally { & "$PSScriptRoot/Compose.ps1" start $backend }
