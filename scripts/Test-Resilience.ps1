param([string]$BaseUrl = 'http://localhost:8080')
$ErrorActionPreference = 'Stop'
function Hidden([string]$html, [string]$name) {
    $tag = [regex]::Matches($html, '<input[^>]+>') | Where-Object { $_.Value.Contains('name="' + $name + '"') } | Select-Object -First 1
    if (!$tag) { throw "Missing form field: $name" }
    return [Net.WebUtility]::HtmlDecode([regex]::Match($tag.Value, 'value="([^"]*)"').Groups[1].Value)
}
$session = New-Object Microsoft.PowerShell.Commands.WebRequestSession
$email = 'resilience-' + [guid]::NewGuid().ToString('N') + '@example.test'
$page = Invoke-WebRequest -UseBasicParsing "$BaseUrl/Account/Register" -WebSession $session
$registration = @{
    Email=$email; Password='Resilience!Password123'; ConfirmPassword='Resilience!Password123'
    __RequestVerificationToken=(Hidden $page.Content '__RequestVerificationToken')
}
Invoke-WebRequest -UseBasicParsing "$BaseUrl/Account/Register" -Method Post -Body $registration -WebSession $session | Out-Null
$me = Invoke-RestMethod "$BaseUrl/api/identity/me" -WebSession $session
$tours = Invoke-RestMethod "$BaseUrl/api/tours" -WebSession $session
$tour = $tours | Select-Object -First 1
$page = Invoke-WebRequest -UseBasicParsing "$BaseUrl/Home/Booking?tourId=$($tour.tourId)" -WebSession $session
$bookingId = $null
try {
    & "$PSScriptRoot/Compose.ps1" stop rabbitmq
    $body = @{
        TourId=$tour.tourId; RequestId=(Hidden $page.Content 'RequestId')
        FullName='Resilience Test'; Email=$email; PhoneNumber='0901234567'; GuestCount='1'
        TravelDate=(Get-Date).AddDays(30).ToString('yyyy-MM-dd')
        __RequestVerificationToken=(Hidden $page.Content '__RequestVerificationToken')
    }
    Invoke-WebRequest -UseBasicParsing "$BaseUrl/Home/Booking" -Method Post -Body $body -WebSession $session | Out-Null
    $bookings = Invoke-RestMethod "$BaseUrl/api/bookings" -WebSession $session
    if (@($bookings).Count -ne 1) { throw 'Booking was not saved while RabbitMQ was stopped.' }
    $bookingId = $bookings[0].bookingId
    Write-Host "PASS: booking #$bookingId saved while broker was stopped."
} finally {
    & "$PSScriptRoot/Compose.ps1" start rabbitmq
}
$received = $false
for ($attempt=0; $attempt -lt 60; $attempt++) {
    Start-Sleep -Seconds 1
    try {
        $notifications = Invoke-RestMethod "$BaseUrl/api/notifications" -WebSession $session
        if ($notifications | Where-Object { $_.type -eq 'booking.created' -and $_.resourceId -eq "$bookingId" }) { $received=$true; break }
    } catch { Write-Verbose 'Notification endpoint reconnecting.' }
}
if (!$received) { throw 'Booking event did not recover within 60 seconds after broker restart.' }
Write-Host 'PASS: pending outbox event was delivered after RabbitMQ restarted.'
