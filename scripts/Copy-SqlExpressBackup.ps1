$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$destination = [IO.File]::ReadAllText((Join-Path $root 'artifacts/sqlexpress-backup-path.txt')).Trim()
$boundary = [IO.Path]::GetFullPath((Join-Path $root 'backups')) + '\'
if (-not ([IO.Path]::GetFullPath($destination)).StartsWith($boundary, [StringComparison]::OrdinalIgnoreCase)) { throw 'Invalid backup destination.' }
$settings=@{}
foreach ($line in [IO.File]::ReadAllLines((Join-Path $root '.env'))) {
    if ($line -match '^([^#=]+)=(.*)$') { $settings[$matches[1]]=$matches[2] }
}
$server=if ($settings['SQL_EXPRESS_SERVER']) { $settings['SQL_EXPRESS_SERVER'] } else { 'localhost\SQLEXPRESS' }
$connection=New-Object System.Data.SqlClient.SqlConnection ("Server=$server;Database=master;Integrated Security=True;TrustServerCertificate=True")
try {
    $connection.Open()
    $command=$connection.CreateCommand()
    $command.CommandText="SELECT CAST(SERVERPROPERTY('InstanceDefaultBackupPath') AS nvarchar(4000))"
    $backupFolder=[string]$command.ExecuteScalar()
} finally { $connection.Dispose() }
$source = Get-ChildItem -LiteralPath $backupFolder -Filter 'TourBookingDB-before-switch-*.bak' | Sort-Object LastWriteTime -Descending | Select-Object -First 1
if (-not $source) { throw 'Verified native backup not found.' }
Copy-Item -LiteralPath $source.FullName -Destination (Join-Path $destination 'TourBookingDB-before-sqlexpress.bak')
'Backup copied.' | Set-Content (Join-Path $root 'artifacts/sqlexpress-backup-copy.log')
