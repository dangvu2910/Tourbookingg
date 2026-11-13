param([switch]$AdminPorts, [switch]$Monitoring)
# One instance per service on one Compose network. Kong is the only public entry (http://localhost:8080).
$ErrorActionPreference = 'Stop'
& (Join-Path $PSScriptRoot 'Initialize-Environment.ps1')
$compose = Join-Path $PSScriptRoot 'Compose.ps1'
$files = @('-f', 'compose.hybrid.yaml')
if ($AdminPorts) { $files += @('-f', 'compose.admin.yaml') }
if ($Monitoring) { $files += @('-f', 'compose.monitoring.yaml') }
& $compose @files config --quiet
& $compose @files up -d --build --wait --wait-timeout 180
& $compose @files ps
Write-Host 'Public entry (Kong): http://localhost:8080'
if ($Monitoring) { Write-Host 'Prometheus: http://localhost:9090 | Grafana: http://localhost:3000 (credentials in .env)' }
if ($AdminPorts) {
    Write-Host 'SQL Server (SSMS): 127.0.0.1,14333 | MongoDB: localhost:27018 | RabbitMQ management: http://localhost:15672 (local administration only)'
} else { Write-Host 'Data stores are private; add -AdminPorts to reach them from SSMS/Compass.' }
