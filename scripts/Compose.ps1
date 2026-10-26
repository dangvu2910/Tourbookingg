# Works with Docker Desktop, or the standalone official Compose binary from Setup-Compose.ps1.
$ErrorActionPreference = 'Stop'
if ([string]::IsNullOrWhiteSpace($env:COMPOSE_PARALLEL_LIMIT)) { $env:COMPOSE_PARALLEL_LIMIT = '2' }
$root = Split-Path $PSScriptRoot -Parent
Push-Location $root
try {
    $binary = Join-Path $root '.tools/docker-compose.exe'
    $ErrorActionPreference = 'Continue'
    if (Test-Path -LiteralPath $binary) { & $binary @args 2>&1 | ForEach-Object { $_.ToString() } }
    elseif (Get-Command docker-compose -ErrorAction SilentlyContinue) { & docker-compose @args 2>&1 | ForEach-Object { $_.ToString() } }
    else { & docker compose @args 2>&1 | ForEach-Object { $_.ToString() } }
    $ErrorActionPreference = 'Stop'
    if ($LASTEXITCODE -ne 0) { throw "Docker Compose exited with code $LASTEXITCODE" }
} finally { Pop-Location }
