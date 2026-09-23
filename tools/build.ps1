# Builds airdeck.exe, airdeck-mapper.exe and hidtool.exe into tools\bin with the .NET Framework
# compiler that ships with Windows (no SDK needed).
$ErrorActionPreference = 'Stop'
$csc = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
$wpf = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\WPF"
$src = Join-Path $PSScriptRoot 'src'
$bin = Join-Path $PSScriptRoot 'bin'
$icon = Join-Path $PSScriptRoot '..\assets\airdeck.ico'
New-Item -ItemType Directory -Force $bin | Out-Null
if (-not (Test-Path $icon)) { & (Join-Path $PSScriptRoot 'make-icon.ps1') }

$common = @('/nologo', '/optimize+', '/platform:x64', '/r:System.Windows.Forms.dll', '/r:System.Drawing.dll', '/r:System.Core.dll', '/r:System.Web.Extensions.dll')

& $csc @common /target:exe "/out:$bin\hidtool.exe" "$src\RawInput.cs" "$src\Json.cs" "$src\HidTool.cs"
if ($LASTEXITCODE) { throw 'hidtool build failed' }

& $csc @common "/win32icon:$icon" /target:winexe "/out:$bin\airdeck-mapper.exe" "$src\RawInput.cs" "$src\Json.cs" "$src\RemoteMapper.cs"
if ($LASTEXITCODE) { throw 'airdeck-mapper build failed' }

& $csc @common "/win32icon:$icon" "/r:$wpf\UIAutomationClient.dll" "/r:$wpf\UIAutomationTypes.dll" "/r:$wpf\WindowsBase.dll" /target:winexe "/out:$bin\airdeck.exe" `
    "$src\RawInput.cs" "$src\Json.cs" "$src\Airdeck.cs" "$src\AirdeckApp.cs" "$src\Interception.cs" "$src\Targets.cs" "$src\Screens.cs" "$src\WebServer.cs"
if ($LASTEXITCODE) { throw 'airdeck build failed' }

# The Interception user-mode library must sit next to airdeck.exe (fetched by get-interception.ps1).
$dll = Join-Path $PSScriptRoot '..\vendor\Interception\Interception\library\x64\interception.dll'
if (Test-Path $dll) { Copy-Item $dll $bin -Force }

Write-Host "Built airdeck.exe, airdeck-mapper.exe and hidtool.exe in $bin"
