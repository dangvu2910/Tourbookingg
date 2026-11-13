$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$directory = Join-Path $root 'artifacts/monitoring-verification'
New-Item -ItemType Directory -Force -Path $directory | Out-Null
$ids = @(docker ps --filter 'label=com.docker.compose.project=tourbooking-hybrid-verification' --filter 'label=com.docker.compose.service=blackbox' --format '{{.ID}}')
if ($LASTEXITCODE -ne 0 -or $ids.Count -ne 1) { throw 'Exactly one isolated blackbox exporter is required' }
$container = $ids[0]
$started = [DateTime]::UtcNow
docker stop $container | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Could not stop isolated exporter' }
try {
    $deadline = [DateTime]::UtcNow.AddSeconds(120)
    do {
        $response = Invoke-RestMethod 'http://127.0.0.1:19090/api/v1/alerts' -TimeoutSec 10
        $firing = @($response.data.alerts | Where-Object { $_.labels.alertname -eq 'ProbeScrapeFailed' -and $_.state -eq 'firing' })
        if ($firing.Count -gt 0) { break }
        Start-Sleep -Seconds 2
    } while ([DateTime]::UtcNow -lt $deadline)
    if ($firing.Count -eq 0) { throw 'Expected ProbeScrapeFailed to fire' }
    $firedAt = [DateTime]::UtcNow
} finally {
    docker start $container | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Could not restore isolated exporter' }
}
$deadline = [DateTime]::UtcNow.AddSeconds(90)
do {
    $response = Invoke-RestMethod 'http://127.0.0.1:19090/api/v1/alerts' -TimeoutSec 10
    $active = @($response.data.alerts | Where-Object { $_.labels.alertname -eq 'ProbeScrapeFailed' })
    if ($active.Count -eq 0) { break }
    Start-Sleep -Seconds 2
} while ([DateTime]::UtcNow -lt $deadline)
if ($active.Count -ne 0) { throw 'Exporter alert did not resolve after recovery' }
@{ started_at_utc = $started.ToString('o'); fired_at_utc = $firedAt.ToString('o');
   resolved_at_utc = [DateTime]::UtcNow.ToString('o'); alert = 'ProbeScrapeFailed'; recovered = $true } |
    ConvertTo-Json | Set-Content -Encoding utf8 (Join-Path $directory 'alert-runtime.json')
Write-Host 'PASS exporter-down alert fired and resolved after recovery'
