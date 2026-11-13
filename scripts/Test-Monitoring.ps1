$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$directory = Join-Path $root 'artifacts/monitoring-verification'
New-Item -ItemType Directory -Path $directory -Force | Out-Null
$deadline = [DateTime]::UtcNow.AddSeconds(90)
do {
    $response = Invoke-RestMethod 'http://127.0.0.1:19090/api/v1/targets' -TimeoutSec 10
    $targets = @($response.data.activeTargets | Where-Object { $_.labels.job -eq 'app-metrics' })
    if ($targets.Count -eq 6 -and @($targets | Where-Object health -ne 'up').Count -eq 0) { break }
    Start-Sleep -Seconds 2
} while ([DateTime]::UtcNow -lt $deadline)
if ($targets.Count -ne 6 -or @($targets | Where-Object health -ne 'up').Count -gt 0) { throw 'Expected six healthy application metrics targets (one per service)' }
$query = [Uri]::EscapeDataString('sum(http_requests_received_total{job="app-metrics"})')
$requests = Invoke-RestMethod "http://127.0.0.1:19090/api/v1/query?query=$query" -TimeoutSec 10
if ($requests.data.result.Count -ne 1 -or [double]$requests.data.result[0].value[1] -le 0) { throw 'No HTTP measurements scraped' }
$outboxQuery = [Uri]::EscapeDataString('count(tourbooking_outbox_observation_success{job="app-metrics"} == 1)')
$outbox = Invoke-RestMethod "http://127.0.0.1:19090/api/v1/query?query=$outboxQuery" -TimeoutSec 10
if ($outbox.data.result.Count -ne 1 -or [double]$outbox.data.result[0].value[1] -ne 4) { throw 'Expected four healthy SQL outbox observers (identity, catalog, booking, customer)' }
$secretLine = Get-Content (Join-Path $root 'artifacts/hybrid-verification/test.env') | Where-Object { $_ -match '^GRAFANA_ADMIN_PASSWORD=' }
$password = $secretLine.Substring('GRAFANA_ADMIN_PASSWORD='.Length)
$authorization = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes("admin:$password"))
foreach ($uid in @('tourbooking-health','tourbooking-http')) {
    $dashboard = Invoke-RestMethod "http://127.0.0.1:13000/api/dashboards/uid/$uid" -Headers @{ Authorization = "Basic $authorization" } -TimeoutSec 10
    if ($dashboard.dashboard.panels.Count -eq 0) { throw "Missing provisioned dashboard $uid" }
}
foreach ($path in @('/metrics','/metrics/','/METRICS')) {
    $status = try { (Invoke-WebRequest "http://127.0.0.1:18083$path" -UseBasicParsing -TimeoutSec 10).StatusCode } catch { [int]$_.Exception.Response.StatusCode }
    if ($status -ne 404) { throw "Public metrics must be blocked: $path" }
}
@{ timestamp_utc = [DateTime]::UtcNow.ToString('o'); healthy_metrics_targets = $targets.Count;
   http_measurements = [double]$requests.data.result[0].value[1]; outbox_observers = 4; dashboards_provisioned = 2; public_metrics_status = $status } |
    ConvertTo-Json | Set-Content -Encoding utf8 (Join-Path $directory 'runtime.json')
Write-Host 'PASS six metrics targets, HTTP measurements, two provisioned dashboards and public endpoint protection'
