param([Parameter(Mandatory=$true)][DateTime]$SinceUtc)
$ErrorActionPreference = 'Stop'
$timestamp = $SinceUtc.ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ss.fffffff')
$sql = "USE [TourBookingDb]; UPDATE OutboxMessages SET PublishedAt=NULL, LockedUntil=NULL, LockId=NULL, LastError=NULL WHERE CreatedAt >= CONVERT(datetime2, '$timestamp', 126); SELECT @@ROWCOUNT AS EventsScheduledForReplay;"
& "$PSScriptRoot/Invoke-Sql.ps1" -Query $sql
