param([string]$Server = 'localhost\SQLEXPRESS')
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$name = 'TourbookingReservationTest_' + [Guid]::NewGuid().ToString('N')
if ($name -notmatch '^TourbookingReservationTest_[a-f0-9]{32}$') { throw 'Invalid test database name.' }
$master = New-Object System.Data.SqlClient.SqlConnection ("Server=$Server;Database=master;Integrated Security=True;TrustServerCertificate=True")
$previous = [Environment]::GetEnvironmentVariable('TOURBOOKING_RESERVATION_SQL')
$created = $false
try {
    $master.Open()
    $command = $master.CreateCommand()
    $command.CommandText = "CREATE DATABASE [$name]"
    [void]$command.ExecuteNonQuery()
    $created = $true
    [Environment]::SetEnvironmentVariable('TOURBOOKING_RESERVATION_SQL', "Server=$Server;Database=$name;Integrated Security=True;TrustServerCertificate=True")
    Push-Location $root
    try {
        $ErrorActionPreference = 'Continue'
        dotnet test tests/RabbitMq.Tests/RabbitMq.Tests.csproj --no-build --filter 'FullyQualifiedName!~DistributedWorkflowTests&FullyQualifiedName!~RabbitMqRoundTripTests' --verbosity normal
        $result = $LASTEXITCODE
        $ErrorActionPreference = 'Stop'
        if ($result -ne 0) { throw 'Reservation SQL tests failed.' }
    } finally { Pop-Location }
} finally {
    [Environment]::SetEnvironmentVariable('TOURBOOKING_RESERVATION_SQL', $previous)
    if ($created) {
        # Only the freshly generated, allowlisted test database can reach this DROP.
        $command = $master.CreateCommand()
        $command.CommandText = "ALTER DATABASE [$name] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [$name]"
        [void]$command.ExecuteNonQuery()
        Write-Output 'Isolated reservation test database removed.'
    }
    $master.Dispose()
}
