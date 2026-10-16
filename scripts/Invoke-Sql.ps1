param([Parameter(Mandatory=$true)][string]$Query)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$directory = Join-Path $root 'artifacts/sql'
New-Item -ItemType Directory -Path $directory -Force | Out-Null
$name = 'query-' + [guid]::NewGuid().ToString('N') + '.sql'
$file = Join-Path $directory $name
[IO.File]::WriteAllText($file, $Query, [Text.UTF8Encoding]::new($false))
try {
    & "$PSScriptRoot/Compose.ps1" cp $file "sqlserver:/tmp/$name"
    # Use a SQL file to avoid Windows/native/bash quoting differences for multiline queries.
    & "$PSScriptRoot/Compose.ps1" exec -T sqlserver bash -c "SQLCMDPASSWORD=`$MSSQL_SA_PASSWORD /opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -C -b -i /tmp/$name"
} finally { Remove-Item -LiteralPath $file -ErrorAction SilentlyContinue }
