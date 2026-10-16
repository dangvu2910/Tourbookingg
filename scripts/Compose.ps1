# Works with Docker Desktop, or the standalone official Compose binary from Setup-Compose.ps1.
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
Push-Location $root
try {
    $binary = Join-Path $root '.tools/docker-compose.exe'
    $ErrorActionPreference = 'Continue'
    if (Test-Path -LiteralPath $binary) { & $binary @args 2>&1 | ForEach-Object { $_.ToString() } }
    else { & docker compose @args 2>&1 | ForEach-Object { $_.ToString() } }
    $ErrorActionPreference = 'Stop'
    if ($LASTEXITCODE -ne 0) { throw "Docker Compose exited with code $LASTEXITCODE" }
} finally { Pop-Location }
