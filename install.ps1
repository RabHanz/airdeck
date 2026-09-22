# Installs (or updates) Airdeck for the current user - no admin rights needed.
#   - builds the app if needed (uses the C# compiler built into Windows)
#   - copies it to %LOCALAPPDATA%\Programs\Airdeck (your profiles and data are never overwritten)
#   - adds Start menu shortcuts and an entry in Settings > Apps
#   - keeps "Start with Windows" if you had it on
# Run with:  powershell -ExecutionPolicy Bypass -File install.ps1   (or double-click Install-Airdeck.cmd)
param(
    [string]$Target = (Join-Path $env:LOCALAPPDATA 'Programs\Airdeck'),
    [switch]$NoLaunch
)
$ErrorActionPreference = 'Stop'
$src = $PSScriptRoot
$bin = Join-Path $src 'tools\bin'

Write-Host "Installing Airdeck to $Target"

# 1. Build (and fetch the Interception library) when needed.
if (-not (Test-Path (Join-Path $bin 'airdeck.exe'))) { & (Join-Path $src 'tools\build.ps1') }
if (-not (Test-Path (Join-Path $bin 'interception.dll'))) {
    try { & (Join-Path $src 'tools\get-interception.ps1') -Root $src; & (Join-Path $src 'tools\build.ps1') }
    catch { Write-Warning "Interception library not downloaded ($($_.Exception.Message)). Remote keyboard keys stay unmapped until it is." }
}

# 2. Stop a running copy so its files can be replaced.
foreach ($exe in @((Join-Path $Target 'airdeck.exe'), (Join-Path $bin 'airdeck.exe'))) {
    if (Test-Path $exe) { & $exe --exit | Out-Null }
}
Start-Sleep -Milliseconds 1500
Get-Process airdeck, airdeck-mapper -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue

# 3. Copy program files.
New-Item -ItemType Directory -Force $Target, (Join-Path $Target 'tools'), (Join-Path $Target 'profiles'), (Join-Path $Target 'data') | Out-Null
foreach ($f in 'airdeck.exe', 'airdeck-mapper.exe', 'hidtool.exe', 'interception.dll') {
    $p = Join-Path $bin $f
    if (Test-Path $p) { Copy-Item $p $Target -Force }
}
foreach ($d in 'ui', 'remote-templates', 'assets') { Copy-Item (Join-Path $src $d) $Target -Recurse -Force }
foreach ($f in 'remote-devices.json', 'remote-button-map.md', 'README.md', 'LICENSE', 'uninstall.ps1', 'disable-remote-mapping.cmd', 'restart-remote-mapping.cmd') {
    $p = Join-Path $src $f
    if (Test-Path $p) { Copy-Item $p $Target -Force }
}
foreach ($f in 'install-interception.cmd', 'uninstall-interception.cmd', 'get-interception.ps1') { Copy-Item (Join-Path $src "tools\$f") (Join-Path $Target 'tools') -Force }
$vendor = Join-Path $src 'vendor'
if (Test-Path $vendor) { Copy-Item $vendor $Target -Recurse -Force }

# Profiles: add new ones, never overwrite yours.
Get-ChildItem (Join-Path $src 'profiles') -Filter *.json | ForEach-Object {
    $dest = Join-Path $Target "profiles\$($_.Name)"
    if (-not (Test-Path $dest)) { Copy-Item $_.FullName $dest }
}
# First install from a working copy: bring your active profiles and input spots along.
foreach ($f in 'state.json', 'spots.json') {
    $from = Join-Path $src "data\$f"; $to = Join-Path $Target "data\$f"
    if ((Test-Path $from) -and -not (Test-Path $to)) { Copy-Item $from $to }
}

# 4. Start menu shortcuts.
$exe = Join-Path $Target 'airdeck.exe'
$icon = Join-Path $Target 'assets\airdeck.ico'
$programs = Join-Path ([Environment]::GetFolderPath('Programs')) ''
$shell = New-Object -ComObject WScript.Shell
function New-Shortcut($name, $file, $description) {
    $lnk = $shell.CreateShortcut((Join-Path $programs "$name.lnk"))
    $lnk.TargetPath = $file
    $lnk.WorkingDirectory = $Target
    $lnk.IconLocation = "$icon,0"
    $lnk.Description = $description
    $lnk.Save()
}
New-Shortcut 'Airdeck' $exe 'Use an air-mouse remote as a dictation and desktop controller'
New-Shortcut 'Airdeck Button Mapper' (Join-Path $Target 'airdeck-mapper.exe') 'Record what every button of an air-mouse remote sends'

# 5. Settings > Apps entry (per user).
$version = '1.0.0'
$key = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\Airdeck'
New-Item -Path $key -Force | Out-Null
$uninstall = "powershell.exe -NoProfile -ExecutionPolicy Bypass -File `"$(Join-Path $Target 'uninstall.ps1')`""
$values = @{
    DisplayName = 'Airdeck'; DisplayVersion = $version; Publisher = 'Airdeck contributors'; DisplayIcon = $icon
    InstallLocation = $Target; UninstallString = $uninstall; URLInfoAbout = 'https://github.com/RabHanz/airdeck'
    NoModify = 1; NoRepair = 1
}
foreach ($k in $values.Keys) { Set-ItemProperty -Path $key -Name $k -Value $values[$k] }

# 6. Keep "Start with Windows" if it was on (also migrates the pre-release "AirRemote" entry).
$run = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$props = Get-ItemProperty $run -ErrorAction SilentlyContinue
if ($props -and ($props.PSObject.Properties.Name -contains 'Airdeck' -or $props.PSObject.Properties.Name -contains 'AirRemote')) {
    Set-ItemProperty $run -Name 'Airdeck' -Value "`"$exe`" --tray"
    Remove-ItemProperty $run -Name 'AirRemote' -ErrorAction SilentlyContinue
    Write-Host 'Start with Windows: kept on'
}

Write-Host "Airdeck installed. Find it in the Start menu as 'Airdeck'."
if (-not $NoLaunch) { Start-Process $exe }
