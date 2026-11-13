# Shared helpers for Backup.ps1 / Restore.ps1. Dot-source only.
# Passwords never leave the containers: sqlcmd/mongodump read them from the container's own env.

$SqlDatabases = @('IdentityDB', 'CatalogDB', 'BookingDB', 'CustomerDB')
$MongoDatabases = @('ChatDB', 'NotificationDB')
$SqlAppLogins = @('identity_app', 'catalog_app', 'booking_app', 'customer_app')
# Stop order (entry first); start in reverse.
$AppServices = @('gateway', 'web-router', 'notification', 'customer', 'booking', 'catalog', 'identity')

# Windows PowerShell 5.1 (and pwsh < 7.3) do not escape embedded double quotes for native programs.
$script:LegacyNativeArgs = $PSVersionTable.PSVersion -lt [version]'7.3' -or
    ((Get-Variable PSNativeCommandArgumentPassing -ValueOnly -ErrorAction SilentlyContinue) -eq 'Legacy')

function Write-Step([string]$Message) { Write-Host ("[{0:HH:mm:ss}] {1}" -f (Get-Date), $Message) }

function Resolve-HybridProject([string]$Root, [string]$Project) {
    if ($Project) { return $Project }
    $envFile = Join-Path $Root '.env'
    if (Test-Path -LiteralPath $envFile) {
        $line = Get-Content -LiteralPath $envFile | Where-Object { $_ -match '^\s*HYBRID_PROJECT_NAME\s*=' } | Select-Object -Last 1
        if ($line) { $value = ($line -split '=', 2)[1].Trim().Trim('"', "'"); if ($value) { return $value } }
    }
    'tourbooking-live'
}

function Invoke-Docker([string[]]$Arguments, [switch]$AllowFailure) {
    $native = if ($script:LegacyNativeArgs) { $Arguments | ForEach-Object { $_ -replace '"', '\"' } } else { $Arguments }
    $previous = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try { $output = & docker @native 2>&1 | ForEach-Object { "$_" } }
    finally { $ErrorActionPreference = $previous }
    if ($LASTEXITCODE -ne 0 -and -not $AllowFailure) {
        throw ("docker {0} failed (exit {1}): {2}" -f $Arguments[0], $LASTEXITCODE, (($output | Select-Object -Last 6) -join ' | '))
    }
    $output
}

function Invoke-Container([string]$Container, [string[]]$Command, [switch]$AllowFailure) {
    Invoke-Docker (@('exec', $Container) + $Command) -AllowFailure:$AllowFailure
}

function Invoke-Sql([string]$Container, [string]$Query) {
    Invoke-Container $Container @('bash', '-c',
        'SQLCMDPASSWORD="$MSSQL_SA_PASSWORD" /opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -C -b -h -1 -W -s "|" -Q "$1"',
        '_', $Query)
}

function Invoke-Mongo([string]$Container, [string]$Database, [string]$Script) {
    Invoke-Container $Container @('bash', '-c',
        'mongosh --quiet -u "$MONGO_INITDB_ROOT_USERNAME" -p "$MONGO_INITDB_ROOT_PASSWORD" --authenticationDatabase admin "$1" --eval "$2"',
        '_', $Database, $Script)
}

function Get-ServiceContainer([string]$Project, [string]$Service, [switch]$Optional, [switch]$IncludeStopped) {
    $list = @('ps', '--filter', "label=com.docker.compose.project=$Project", '--filter', "label=com.docker.compose.service=$Service", '--format', '{{.Names}}')
    if ($IncludeStopped) { $list = @('ps', '-a') + $list[1..($list.Count - 1)] }
    $names = @(Invoke-Docker $list | Where-Object { $_ })
    if ($names.Count -eq 1) { return $names[0] }
    if ($Optional -and $names.Count -eq 0) { return $null }
    throw "Expected exactly one running '$Service' container in project '$Project', found $($names.Count). Is the stack up?"
}

function Get-ProjectVolume([string]$Project, [string]$Volume) {
    $names = @(Invoke-Docker @('volume', 'ls', '--filter', "label=com.docker.compose.project=$Project",
        '--filter', "label=com.docker.compose.volume=$Volume", '--format', '{{.Name}}') | Where-Object { $_ })
    if ($names.Count -ne 1) { throw "Expected one volume '$Volume' in project '$Project', found $($names.Count)." }
    $names[0]
}

# Any image of the project that has tar/gzip; the Mongo image always does.
function Get-ToolImage([string]$Project) {
    $mongo = Get-ServiceContainer $Project 'mongodb' -IncludeStopped
    (Invoke-Docker @('inspect', '-f', '{{.Config.Image}}', $mongo) | Select-Object -First 1).Trim()
}

function Copy-FromContainer([string]$Container, [string]$Source, [string]$Destination) {
    Invoke-Docker @('cp', "${Container}:$Source", $Destination) | Out-Null
}

function Copy-ToContainer([string]$Source, [string]$Container, [string]$Destination) {
    Invoke-Docker @('cp', $Source, "${Container}:$Destination") | Out-Null
}

function Get-SqlRowCounts([string]$Container, [string]$Database) {
    $query = "SET NOCOUNT ON; SELECT s.name + '.' + t.name, SUM(p.rows) FROM [$Database].sys.tables t " +
             "JOIN [$Database].sys.schemas s ON s.schema_id = t.schema_id " +
             "JOIN [$Database].sys.partitions p ON p.object_id = t.object_id AND p.index_id IN (0, 1) " +
             "GROUP BY s.name, t.name ORDER BY 1;"
    $rows = [ordered]@{}
    foreach ($line in Invoke-Sql $Container $query) {
        if ($line -match '^(?<name>[^|]+)\|(?<count>\d+)$') { $rows[$Matches.name] = [long]$Matches.count }
    }
    $rows
}

function Get-MongoCounts([string]$Container, [string]$Database) {
    $script = "db.getCollectionNames().sort().forEach(function(c){print(c+'|'+db.getCollection(c).countDocuments({}))})"
    $counts = [ordered]@{}
    foreach ($line in Invoke-Mongo $Container $Database $script) {
        if ($line -match '^(?<name>[^|]+)\|(?<count>\d+)$') { $counts[$Matches.name] = [long]$Matches.count }
    }
    $counts
}

function Write-Checksums([string]$Directory) {
    Get-ChildItem -LiteralPath $Directory -File | Where-Object Name -ne 'SHA256SUMS' | Sort-Object Name | ForEach-Object {
        "{0}  {1}" -f (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant(), $_.Name
    } | Set-Content -Encoding ascii -LiteralPath (Join-Path $Directory 'SHA256SUMS')
}

function Test-Checksums([string]$Directory) {
    $sums = Join-Path $Directory 'SHA256SUMS'
    if (-not (Test-Path -LiteralPath $sums)) { throw "Missing SHA256SUMS in $Directory" }
    foreach ($line in Get-Content -LiteralPath $sums) {
        if ($line -notmatch '^(?<hash>[0-9a-f]{64})  (?<name>.+)$') { throw "Bad SHA256SUMS line: $line" }
        $path = Join-Path $Directory $Matches.name
        if (-not (Test-Path -LiteralPath $path)) { throw "Missing backup file $($Matches.name)" }
        if ((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() -ne $Matches.hash) { throw "Checksum mismatch: $($Matches.name)" }
    }
}

function Compare-Counts([string]$Label, $Expected, $Actual) {
    $failures = 0
    $names = @($Expected.Keys) + @($Actual.Keys) | Sort-Object -Unique
    foreach ($name in $names) {
        $e = if ($Expected.Contains($name)) { $Expected[$name] } else { 'missing' }
        $a = if ($Actual.Contains($name)) { $Actual[$name] } else { 'missing' }
        if ("$e" -ne "$a") { $failures++; Write-Warning "$Label ${name}: backup=$e restored=$a" }
    }
    $failures
}
