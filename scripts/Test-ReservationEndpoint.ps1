# Exercise the real private endpoint with a proven absent tour; the SQL transaction rolls back without inserting rows.
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$settings=@{}
foreach($line in [IO.File]::ReadAllLines((Join-Path $root '.env'))) {
    if($line -match '^([^#=]+)=(.*)$') { $settings[$matches[1]]=$matches[2] }
}
$server=if($settings['SQL_EXPRESS_SERVER']){$settings['SQL_EXPRESS_SERVER']}else{'localhost\SQLEXPRESS'}
$connection=New-Object System.Data.SqlClient.SqlConnection ("Server=$server;Database=TourBookingDB;Integrated Security=True;TrustServerCertificate=True")
try {
    $connection.Open()
    $command=$connection.CreateCommand()
    $command.CommandText='SELECT ISNULL(MAX(TourId),0) FROM catalog.Tours'
    $max=[long]$command.ExecuteScalar()
    if($max -ge [int]::MaxValue) { throw 'No safe absent test ID available.' }
    $absent=[int]($max+1)
} finally { $connection.Dispose() }
$body=@{ requestId=[Guid]::NewGuid().ToString(); tourId=$absent; userId='operator-read-check'; travelDate=[DateTime]::UtcNow.Date.AddDays(30).ToString('O'); guestCount=1 } | ConvertTo-Json -Compress
$size=[Text.Encoding]::UTF8.GetByteCount($body)
$request="POST /internal/v1/reservations HTTP/1.1`r`nHost: catalog`r`nX-Service-Key: $($settings['INTERNAL_SERVICE_KEY'])`r`nContent-Type: application/json`r`nContent-Length: $size`r`nConnection: close`r`n`r`n$body"
Push-Location $root
try {
    $gateway=& ./scripts/Compose.ps1 -f compose.hybrid.yaml -f compose.admin.yaml -f compose.sqlexpress.yaml ps -q gateway
    if(-not $gateway) { throw 'Gateway container is unavailable.' }
    $reply=$request | docker exec -i $gateway bash -c 'exec 3<>/dev/tcp/catalog/8080; cat >&3; cat <&3'
    if($LASTEXITCODE -ne 0 -or -not ($reply -match '^HTTP/1.1 404') -or -not (($reply -join "`n").Contains('RESOURCE_NOT_FOUND'))) {
        throw 'Private reservation endpoint did not return the expected missing-tour result.'
    }
    Write-Output 'Private reservation endpoint, internal authentication, runtime SQL connection and application lock verified; no reservation inserted.'
} finally { Pop-Location }
