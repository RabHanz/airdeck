# Downloads Interception v1.0.1 (https://github.com/oblitum/Interception) from its official
# GitHub release into vendor\, verifies the archive hash and unpacks it. Interception is a
# separate project under its own license; Airdeck only loads its user-mode DLL.
param([string]$Root = (Split-Path $PSScriptRoot -Parent))
$ErrorActionPreference = 'Stop'
$url = 'https://github.com/oblitum/Interception/releases/download/v1.0.1/Interception.zip'
$sha256 = 'AD038963D6413055765128B0B931F6E765147C9916DBA79E65D872B261F9AF10'
$vendor = Join-Path $Root 'vendor'
$dll = Join-Path $vendor 'Interception\Interception\library\x64\interception.dll'

if (Test-Path $dll) { Write-Host "Interception already present in $vendor"; return }

New-Item -ItemType Directory -Force $vendor | Out-Null
$zip = Join-Path $vendor 'Interception.zip'
Write-Host "Downloading $url"
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
Invoke-WebRequest -Uri $url -OutFile $zip -UseBasicParsing
$actual = (Get-FileHash $zip -Algorithm SHA256).Hash
if ($actual -ne $sha256) {
    Remove-Item $zip -Force
    throw "Interception.zip hash mismatch (got $actual) - refusing to use it."
}
Expand-Archive $zip -DestinationPath (Join-Path $vendor 'Interception') -Force
Write-Host "Interception unpacked to $vendor"
