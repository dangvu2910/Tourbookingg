$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$target = Join-Path $root '.env'
if (Test-Path -LiteralPath $target) {
    Write-Host '.env already exists; keeping current credentials.'
}
function New-Password {
    $bytes = New-Object byte[] 24
    $rng = [System.Security.Cryptography.RandomNumberGenerator]::Create()
    try { $rng.GetBytes($bytes) } finally { $rng.Dispose() }
    return 'Tb!9' + [Convert]::ToBase64String($bytes)
}
if (-not (Test-Path -LiteralPath $target)) {
$content = @(
    'SQL_PASSWORD=' + (New-Password)
    'RABBITMQ_PASSWORD=' + (New-Password)
    'ADMIN_EMAIL=admin@tourbooking.local'
    'ADMIN_PASSWORD=' + (New-Password)
)
[IO.File]::WriteAllLines($target, $content, [Text.UTF8Encoding]::new($false))
Write-Host 'Created .env with random credentials. Keep this file private.'
}

# Private service-to-service snapshot API key. Never print its value.
$environmentPath = Join-Path (Split-Path $PSScriptRoot -Parent) '.env'
if (-not (Select-String -LiteralPath $environmentPath -Pattern '^INTERNAL_SERVICE_KEY=.+' -Quiet)) {
    $keyBytes = New-Object byte[] 32
    $keyRng = [Security.Cryptography.RandomNumberGenerator]::Create()
    try { $keyRng.GetBytes($keyBytes) } finally { $keyRng.Dispose() }
    $serviceKey = [Convert]::ToBase64String($keyBytes)
    Add-Content -LiteralPath $environmentPath -Value "`nINTERNAL_SERVICE_KEY=$serviceKey" -Encoding utf8
}

# Hex passwords are URI-safe for MongoDB and cannot contain SQLCMD metacharacters.
if (-not (Select-String -LiteralPath $environmentPath -Pattern '^API_JWT_SIGNING_KEY=.+' -Quiet)) {
    $jwtBytes=New-Object byte[] 48
    $jwtGenerator=[Security.Cryptography.RandomNumberGenerator]::Create()
    try { $jwtGenerator.GetBytes($jwtBytes) } finally { $jwtGenerator.Dispose() }
    Add-Content -LiteralPath $environmentPath -Value ('API_JWT_SIGNING_KEY='+[Convert]::ToBase64String($jwtBytes)) -Encoding utf8
}

foreach ($name in @('IDENTITY_SQL_PASSWORD', 'CATALOG_SQL_PASSWORD', 'BOOKING_SQL_PASSWORD',
    'CUSTOMER_SQL_PASSWORD', 'MONGO_ROOT_PASSWORD', 'MONGO_CHAT_PASSWORD', 'MONGO_NOTIFICATION_PASSWORD', 'GRAFANA_ADMIN_PASSWORD')) {
    if (-not (Select-String -LiteralPath $environmentPath -Pattern "^$name=.+" -Quiet)) {
        $randomBytes = New-Object byte[] 24
        $generator = [Security.Cryptography.RandomNumberGenerator]::Create()
        try { $generator.GetBytes($randomBytes) } finally { $generator.Dispose() }
        $password = 'Tb9' + ([BitConverter]::ToString($randomBytes)).Replace('-', '')
        Add-Content -LiteralPath $environmentPath -Value "$name=$password" -Encoding utf8
    }
}

# A separate administrator credential for the new stack preserves legacy credentials.
if (-not (Select-String -LiteralPath $environmentPath -Pattern '^HYBRID_ADMIN_PASSWORD=.+' -Quiet)) {
    Add-Content -LiteralPath $environmentPath -Value ('HYBRID_ADMIN_PASSWORD=' + (New-Password)) -Encoding utf8
}
