$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$target = Join-Path $root '.env'
if (Test-Path -LiteralPath $target) {
    Write-Host '.env already exists; keeping current credentials.'
    exit 0
}
function New-Password {
    $bytes = New-Object byte[] 24
    $rng = [System.Security.Cryptography.RandomNumberGenerator]::Create()
    try { $rng.GetBytes($bytes) } finally { $rng.Dispose() }
    return 'Tb!9' + [Convert]::ToBase64String($bytes)
}
$content = @(
    'SQL_PASSWORD=' + (New-Password)
    'RABBITMQ_PASSWORD=' + (New-Password)
    'ADMIN_EMAIL=admin@tourbooking.local'
    'ADMIN_PASSWORD=' + (New-Password)
)
[IO.File]::WriteAllLines($target, $content, [Text.UTF8Encoding]::new($false))
Write-Host 'Created .env with random credentials. Keep this file private.'
