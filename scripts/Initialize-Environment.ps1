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
