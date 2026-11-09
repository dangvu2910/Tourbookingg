# Explicit cleanup for the retired empty tables in the local TourBookingDB.
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$folder = Join-Path $root ('backups/sql-unused-cleanup-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
New-Item -ItemType Directory -Path $folder -Force | Out-Null
$connection = New-Object System.Data.SqlClient.SqlConnection 'Server=localhost\SQLEXPRESS;Database=TourBookingDB;Integrated Security=True;TrustServerCertificate=True;Connect Timeout=10'
$transaction = $null
try {
    $connection.Open()
    $command = $connection.CreateCommand()
    $command.CommandText = "SELECT CAST(SERVERPROPERTY('InstanceDefaultBackupPath') AS nvarchar(4000))"
    $backup = Join-Path ([string]$command.ExecuteScalar()) ('TourBookingDB-before-unused-cleanup-' + (Get-Date -Format 'yyyyMMddHHmmss') + '.bak')
    $command.CommandText = 'BACKUP DATABASE [TourBookingDB] TO DISK=@path WITH COPY_ONLY,CHECKSUM; RESTORE VERIFYONLY FROM DISK=@path WITH CHECKSUM;'
    [void]$command.Parameters.AddWithValue('@path',$backup)
    $command.CommandTimeout = 120
    [void]$command.ExecuteNonQuery()
    @{ database='TourBookingDB'; server='localhost\SQLEXPRESS'; backup=$backup; restoreVerifyOnly=$true } | ConvertTo-Json | Set-Content -Encoding UTF8 (Join-Path $folder 'backup-receipt.json')
    $command.Parameters.Clear()
    $transaction = $connection.BeginTransaction()
    $command.Transaction = $transaction
    # Fixed allowlist, child tables first. No runtime business/auth/event tables are included.
    $tables = @('dbo.BookingDetails','dbo.Payments','dbo.Bookings','dbo.Customers','dbo.__EFMigrationsHistory',
                'booking.LegacyOrphanRows','catalog.LegacyOrphanRows','customer.LegacyOrphanRows')
    $removed = @()
    foreach ($table in $tables) {
        $parts=$table.Split('.')
        $qualified='['+$parts[0]+'].['+$parts[1]+']'
        $command.CommandText="SELECT OBJECT_ID(N'$qualified',N'U')"
        if ($command.ExecuteScalar() -is [DBNull]) { continue }
        $command.CommandText="SELECT COUNT_BIG(*) FROM $qualified WITH (TABLOCKX,HOLDLOCK)"
        if ([long]$command.ExecuteScalar() -ne 0) { throw "Cleanup stopped: $table contains data." }
        $command.CommandText="DROP TABLE $qualified"
        [void]$command.ExecuteNonQuery()
        $removed += $table
    }
    $transaction.Commit()
    @{ verified=$true; removed=$removed; removedRows=0; backup=$backup } | ConvertTo-Json | Set-Content -Encoding UTF8 (Join-Path $folder 'cleanup-report.json')
    Write-Output ('Removed '+$removed.Count+' unused empty tables; no data rows deleted.')
    $removed
    Write-Output "Backup/report: $folder"
} catch {
    if ($transaction -and $transaction.Connection) { $transaction.Rollback() }
    throw
} finally {
    if ($transaction) { $transaction.Dispose() }
    $connection.Dispose()
}
