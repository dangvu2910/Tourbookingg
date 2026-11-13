$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$directory = Join-Path $root 'artifacts/restore-verification'
New-Item -ItemType Directory -Force -Path $directory | Out-Null
$containers = @(docker ps --filter 'label=com.docker.compose.project=tourbooking-hybrid-verification' --filter 'label=com.docker.compose.service=sqlserver' --format '{{.ID}}')
if ($LASTEXITCODE -ne 0 -or $containers.Count -ne 1) { throw 'Exactly one isolated SQL verification container must be running' }
$container = $containers[0]
$info = docker inspect $container | ConvertFrom-Json
if ($info[0].Config.Labels.'com.docker.compose.project' -ne 'tourbooking-hybrid-verification') { throw 'Refusing non-test SQL container' }
docker cp (Join-Path $root 'tests/restore-verification.sql') "${container}:/tmp/restore-verification.sql"
if ($LASTEXITCODE -ne 0) { throw 'SQL script copy failed' }
docker exec $container bash -ec '/opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -P "$MSSQL_SA_PASSWORD" -C -b -i /tmp/restore-verification.sql' |
    Tee-Object -FilePath (Join-Path $directory 'sql-restore.log')
if ($LASTEXITCODE -ne 0) { throw 'SQL backup/restore verification failed' }
