# Renders the running Airdeck UI in headless Edge and saves a PNG (used for README screenshots).
# -Hash picks a view (live, profiles, spots, settings); -Query adds URL parameters (e.g. "&select=home").
# The headless browser is always shut down afterwards, even if it hangs.
param([string]$Out = "$env:TEMP\airdeck-ui.png", [string]$Hash = "", [string]$Query = "", [int]$Width = 1480, [int]$Height = 940, [int]$Port = 47800)
$edge = "${env:ProgramFiles(x86)}\Microsoft\Edge\Application\msedge.exe"
$profile = Join-Path $env:TEMP 'airdeck-headless'
$url = "http://127.0.0.1:$Port/?nosse$Query" + $(if ($Hash) { "#$Hash" } else { '' })
if (Test-Path $Out) { Remove-Item $Out -Force }
$edgeArgs = @('--headless=new', '--disable-gpu', '--hide-scrollbars', "--user-data-dir=$profile", "--window-size=$Width,$Height",
              '--virtual-time-budget=3000', "--screenshot=$Out", $url)
$p = Start-Process $edge -ArgumentList $edgeArgs -PassThru -WindowStyle Hidden
$deadline = (Get-Date).AddSeconds(25)
while (-not (Test-Path $Out) -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 300 }
Start-Sleep -Milliseconds 500
taskkill /PID $p.Id /T /F 2>$null | Out-Null
Get-CimInstance Win32_Process -Filter "Name='msedge.exe'" | Where-Object { $_.CommandLine -like '*airdeck-headless*' } |
    ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
if (Test-Path $Out) { Write-Host "saved $Out" } else { Write-Host "no screenshot" }
