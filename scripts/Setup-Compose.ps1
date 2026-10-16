$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$directory = Join-Path $root '.tools'
New-Item -ItemType Directory -Path $directory -Force | Out-Null
$release = Invoke-RestMethod 'https://api.github.com/repos/docker/compose/releases/latest'
$asset = $release.assets | Where-Object name -eq 'docker-compose-windows-x86_64.exe'
$checksum = $release.assets | Where-Object name -eq 'docker-compose-windows-x86_64.exe.sha256'
if (!$asset -or !$checksum) { throw 'Official Windows binary or checksum missing from release.' }
$binary = Join-Path $directory 'docker-compose.exe'
$checksumFile = Join-Path $directory 'compose.sha256'
Invoke-WebRequest -UseBasicParsing $checksum.browser_download_url -OutFile $checksumFile
$expected = ((Get-Content -LiteralPath $checksumFile -Raw).Trim() -split '\s+')[0]
if (!(Test-Path -LiteralPath $binary) -or (Get-FileHash -LiteralPath $binary -Algorithm SHA256).Hash -ne $expected) {
    Invoke-WebRequest -UseBasicParsing $asset.browser_download_url -OutFile $binary
}
$actual = (Get-FileHash -LiteralPath $binary -Algorithm SHA256).Hash
if ($actual -ne $expected) { throw 'Compose SHA256 verification failed. Do not execute the downloaded file.' }
Write-Host "Verified official Docker Compose $($release.tag_name)"
& $binary version
