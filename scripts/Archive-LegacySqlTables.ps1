# Retire legacy tables/history and empty SQL chat tables after switching Customer to MongoDB.
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$folder = Join-Path $root ('backups/sql-legacy-retirement-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
New-Item -ItemType Directory -Path $folder -Force | Out-Null
$connection = New-Object System.Data.SqlClient.SqlConnection 'Server=localhost\SQLEXPRESS;Database=TourBookingDB;Integrated Security=True;TrustServerCertificate=True;Connect Timeout=10'
$transaction = $null
try {
    $connection.Open()
    $command=$connection.CreateCommand()
    $command.CommandText="SELECT CAST(SERVERPROPERTY('InstanceDefaultBackupPath') AS nvarchar(4000))"
    $backup=Join-Path ([string]$command.ExecuteScalar()) ('TourBookingDB-before-legacy-retirement-'+(Get-Date -Format 'yyyyMMddHHmmss')+'.bak')
    $command.CommandText='BACKUP DATABASE [TourBookingDB] TO DISK=@path WITH COPY_ONLY,CHECKSUM; RESTORE VERIFYONLY FROM DISK=@path WITH CHECKSUM;'
    [void]$command.Parameters.AddWithValue('@path',$backup)
    $command.CommandTimeout=120
    [void]$command.ExecuteNonQuery()
    @{ database='TourBookingDB'; server='localhost\SQLEXPRESS'; backup=$backup; restoreVerifyOnly=$true } | ConvertTo-Json | Set-Content -Encoding UTF8 (Join-Path $folder 'backup-receipt.json')
    $command.Parameters.Clear()
    $transaction=$connection.BeginTransaction()
    $command.Transaction=$transaction
    $archived=@()
    $removed=@()
    foreach ($table in @('dbo.Staffs','dbo.Tours','dbo.__MigrationHistory','dbo.sysdiagrams','customer.ContactInquiryReplies','customer.ContactInquiries','identity.LegacyOrphanRows')) {
        $parts=$table.Split('.')
        $qualified='['+$parts[0]+'].['+$parts[1]+']'
        $command.CommandText="SELECT OBJECT_ID(N'$qualified',N'U')"
        if ($command.ExecuteScalar() -is [DBNull]) { continue }
        $command.CommandText="SELECT COUNT_BIG(*) FROM $qualified WITH(TABLOCKX,HOLDLOCK)"
        $rows=[long]$command.ExecuteScalar()
        if ($table.StartsWith('customer.') -and $rows -ne 0) { throw 'SQL chat contains data; migrate it before removing its tables.' }
        if ($rows -gt 0) {
            $command.CommandText="SELECT * FROM $qualified FOR JSON PATH,INCLUDE_NULL_VALUES"
            $reader=$command.ExecuteReader()
            $json=New-Object System.Text.StringBuilder
            try { while ($reader.Read()) { [void]$json.Append($reader.GetString(0)) } } finally { $reader.Dispose() }
            $archive=Join-Path $folder ($table+'.json')
            [IO.File]::WriteAllText($archive,$json.ToString(),(New-Object System.Text.UTF8Encoding($false)))
            $saved=ConvertFrom-Json ([IO.File]::ReadAllText($archive))
            $savedRows=if ($saved -is [Array]) { $saved.Length } else { 1 }
            if ($savedRows -ne $rows) { throw "Archive count mismatch for $table" }
            $archived+=@{table=$table; rows=$rows; sha256=(Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash}
        }
        $command.CommandText="DROP TABLE $qualified"
        [void]$command.ExecuteNonQuery()
        $removed+=$table
    }
    # These SSMS diagram helpers belong to the retired, empty sysdiagrams table.
    foreach ($name in @('sp_upgraddiagrams','sp_helpdiagrams','sp_helpdiagramdefinition','sp_creatediagram','sp_renamediagram','sp_alterdiagram','sp_dropdiagram')) {
        $command.CommandText="DROP PROCEDURE IF EXISTS [dbo].[$name]"
        [void]$command.ExecuteNonQuery()
    }
    $command.CommandText='DROP FUNCTION IF EXISTS [dbo].[fn_diagramobjects]'
    [void]$command.ExecuteNonQuery()
    $transaction.Commit()
    @{ verified=$true; backup=$backup; removed=$removed; archivedLegacyRows=$archived; runtimeBusinessTablesUnchanged=$true } | ConvertTo-Json -Depth 5 | Set-Content -Encoding UTF8 (Join-Path $folder 'cleanup-report.json')
    Write-Output ('Retired '+$removed.Count+' tables; legacy data archived and backup verified.')
    Write-Output "Backup/report: $folder"
} catch {
    if ($transaction -and $transaction.Connection) { $transaction.Rollback() }
    throw
} finally {
    if ($transaction) { $transaction.Dispose() }
    $connection.Dispose()
}
