param([string]$BaseUrl = 'http://localhost:8080')
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Net.Http
$handler = New-Object System.Net.Http.HttpClientHandler
$handler.AllowAutoRedirect = $false
$client = New-Object System.Net.Http.HttpClient($handler)
$client.Timeout = [TimeSpan]::FromSeconds(15)
function Check([string]$path, [int[]]$expected) {
    $response = $client.GetAsync($BaseUrl + $path).GetAwaiter().GetResult()
    try {
        $status = [int]$response.StatusCode
        if ($status -notin $expected) { throw "$path returned $status; expected $expected" }
        Write-Host "PASS: $path -> $status"
    } finally { $response.Dispose() }
}
$stopped = $false
try {
    Check '/api/tours' @(200)
    $container = & "$PSScriptRoot/Compose.ps1" ps -q catalog
    if (!$container) { throw 'Catalog must be running before this test.' }
    $stopped = $true
    & "$PSScriptRoot/Compose.ps1" stop catalog
    Check '/api/tours' @(502,503)
    Check '/' @(200)
    Check '/health/ready' @(200)
    Check '/Account/Login' @(200)
    Check '/Home/Payment?bookingId=1' @(302)
    Check '/Home/Contact' @(302)
    Check '/api/notifications' @(401)
    Check '/assets/identity/css/layout.css' @(200)
    Check '/assets/booking/css/booking.css' @(200)
    Check '/assets/customer/css/layout.css' @(200)
    foreach ($service in @('gateway','identity','booking','customer','notification','rabbitmq')) {
        $id = & "$PSScriptRoot/Compose.ps1" ps -q $service
        if (!$id) { throw "$service is not running" }
        $state = docker inspect --format '{{.State.Status}} {{.State.Health.Status}}' $id
        if ($LASTEXITCODE -ne 0 -or $state -ne 'running healthy') { throw "$service state: $state" }
        Write-Host "PASS: $service remains healthy"
    }
} finally {
    if ($stopped) { & "$PSScriptRoot/Compose.ps1" start catalog }
}
$recovered = $false
for ($attempt = 0; $attempt -lt 45; $attempt++) {
    try { Check '/api/tours' @(200); $recovered = $true; break } catch { Start-Sleep -Seconds 1 }
}
$client.Dispose()
$handler.Dispose()
if (!$recovered) { throw 'Catalog did not recover within 45 attempts.' }
Write-Host 'PASS: Catalog outage was isolated and routing recovered after restart.'
