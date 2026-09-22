# Removes Airdeck for the current user. Your profiles and data are kept unless you pass -RemoveData.
# The optional Interception driver is separate: remove it with tools\uninstall-interception.cmd.
param([switch]$RemoveData)
$ErrorActionPreference = 'Continue'
$target = $PSScriptRoot
$exe = Join-Path $target 'airdeck.exe'

if (Test-Path $exe) { & $exe --exit | Out-Null; Start-Sleep -Milliseconds 1500 }
Get-Process airdeck, airdeck-mapper -ErrorAction SilentlyContinue | Stop-Process -Force

$programs = [Environment]::GetFolderPath('Programs')
Remove-Item (Join-Path $programs 'Airdeck.lnk'), (Join-Path $programs 'Airdeck Button Mapper.lnk') -ErrorAction SilentlyContinue
Remove-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name 'Airdeck' -ErrorAction SilentlyContinue
Remove-Item 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\Airdeck' -Recurse -ErrorAction SilentlyContinue

$keep = if ($RemoveData) { @() } else { @('profiles', 'data', 'logs') }
Get-ChildItem $target | Where-Object { $keep -notcontains $_.Name } | Remove-Item -Recurse -Force -ErrorAction SilentlyContinue
if ($RemoveData) { Set-Location $env:TEMP; Remove-Item $target -Recurse -Force -ErrorAction SilentlyContinue }

Write-Host ('Airdeck removed.' + $(if ($RemoveData) { '' } else { " Your profiles and data are still in $target." }))
