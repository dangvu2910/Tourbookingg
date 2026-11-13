$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
function Read-Metric([string]$Expression) {
    $query = [Uri]::EscapeDataString($Expression)
    (Invoke-RestMethod "http://127.0.0.1:19090/api/v1/query?query=$query" -TimeoutSec 10).data.result
}
function Wait-Observers([int]$ExpectedValue) {
    $deadline = [DateTime]::UtcNow.AddSeconds(90)
    do {
        $result = @(Read-Metric "count(tourbooking_outbox_observation_success{job=`"app-metrics`"} == $ExpectedValue)")
        if ($result.Count -eq 1 -and [double]$result[0].value[1] -eq 4) { return }
        Start-Sleep -Seconds 2
    } while ([DateTime]::UtcNow -lt $deadline)
    throw "Expected four outbox observers with success=$ExpectedValue"
}
$ids = @(docker ps --filter 'label=com.docker.compose.project=tourbooking-hybrid-verification' --filter 'label=com.docker.compose.service=sqlserver' --format '{{.ID}}')
if ($LASTEXITCODE -ne 0 -or $ids.Count -ne 1) { throw 'Exactly one isolated test SQL container is required' }
Wait-Observers 1
docker stop $ids[0] | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Could not stop isolated SQL container' }
try {
    Wait-Observers 0
    $pending = @(Read-Metric 'tourbooking_outbox_pending{job="app-metrics"}')
    if ($pending.Count -ne 4 -or @($pending | Where-Object { $_.value[1] -ne 'NaN' }).Count) {
        throw 'Unavailable SQL must produce NaN backlog, never a false zero'
    }
} finally {
    docker start $ids[0] | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Could not restore isolated SQL container' }
}
Wait-Observers 1
$directory = Join-Path $root 'artifacts/monitoring-verification'
New-Item -ItemType Directory -Force -Path $directory | Out-Null
@{ timestamp_utc = [DateTime]::UtcNow.ToString('o'); observers = 8;
   failed_observation_reported = $true; unavailable_backlog_is_nan = $true; recovered = $true } |
    ConvertTo-Json | Set-Content -Encoding utf8 (Join-Path $directory 'outbox-failure.json')
Write-Host 'PASS SQL outage reports failed observation and NaN backlog; all observers recover'
