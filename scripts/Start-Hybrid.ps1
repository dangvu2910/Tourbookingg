param([switch]$AdminPorts, [switch]$SingleInstance)
$ErrorActionPreference = 'Stop'
& (Join-Path $PSScriptRoot 'Initialize-Environment.ps1')
$compose = Join-Path $PSScriptRoot 'Compose.ps1'
$files = @('-f', 'compose.hybrid.yaml')
if ($AdminPorts) { $files += @('-f', 'compose.admin.yaml') }
$native = Select-String -LiteralPath (Join-Path (Split-Path $PSScriptRoot -Parent) '.env') -Pattern '^SQL_DEPLOYMENT=sqlexpress$' -Quiet
if ($native) { $files += @('-f', 'compose.sqlexpress.yaml') }
if (-not $SingleInstance) { $files += @('-f', 'compose.ha.yaml') }
& $compose @files config --quiet
if ($native) { & $compose -f compose.hybrid.yaml --profile docker-sql build identity-init catalog-init booking-init customer-init }
# Release the original Kong host binding when moving an existing single-instance stack to HA.
if (-not $SingleInstance) {
    $kongIds = & $compose @files ps -q kong
    foreach ($id in $kongIds) {
        $info = docker inspect $id | ConvertFrom-Json
        if ($info[0].HostConfig.PortBindings.'8000/tcp') { docker stop $id | Out-Null }
    }
}
& $compose @files up -d --build --wait --wait-timeout 180
& $compose @files ps
Write-Host 'Kong public entry: http://localhost:8080'
if ($native) { Write-Host 'SQL Server: localhost\SQLEXPRESS | Database: TourBookingDB' }
if ($AdminPorts) { Write-Host 'MongoDB: localhost:27018 | RabbitMQ management: http://localhost:15672 (local administration only)' }
