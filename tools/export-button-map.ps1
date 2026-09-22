# Turns data\remote-capture.json (written by remote-mapper) into:
#   remote-devices.json   - machine-readable button signatures used by airdeck.exe
#   remote-button-map.md  - human-readable table of every button on every remote
param(
    [string]$Root = (Split-Path $PSScriptRoot -Parent)
)
$ErrorActionPreference = 'Stop'
$capture = Get-Content (Join-Path $Root 'data\remote-capture.json') -Raw | ConvertFrom-Json

function Get-Signature($step) {
    if ($step.status -ne 'captured') { return $null }
    $p = $step.primary
    if ($p -match 'USAGE page=0x([0-9A-F]{4}) usage=0x([0-9A-F]{4})') {
        return [ordered]@{ source = 'consumer'; page = "0x$($Matches[1])"; usage = "0x$($Matches[2])"; interface = ($step.primary_interface -split ' ')[0] }
    }
    if ($step.primary_interface -like '*consumer*' -and $p -match 'raw report only: HID ([0-9A-F]{2}) ([0-9A-F]{2}) ([0-9A-F]{2})') {
        # Report ID + 16-bit little-endian consumer usage.
        return [ordered]@{ source = 'consumer'; page = '0x000C'; usage = "0x$($Matches[3])$($Matches[2])"; interface = ($step.primary_interface -split ' ')[0] }
    }
    if ($p -match 'KEY vk=0x([0-9A-F]{2}) \(([^)]*)\) sc=((E0 )?[0-9A-F]{2})') {
        return [ordered]@{ source = 'keyboard'; vk = "0x$($Matches[1])"; key = $Matches[2]; sc = $Matches[3]; interface = ($step.primary_interface -split ' ')[0] }
    }
    if ($p -match 'MOUSE (\w+)') {
        return [ordered]@{ source = 'mouse'; button = $Matches[1]; interface = ($step.primary_interface -split ' ')[0] }
    }
    return [ordered]@{ source = 'unknown'; detail = $p }
}

$out = [ordered]@{ generated_at = (Get-Date -Format s); generated_from = 'data/remote-capture.json'; remotes = [ordered]@{} }
$md = New-Object System.Collections.Generic.List[string]
$md.Add('# Remote button map')
$md.Add('')
$md.Add("Generated $(Get-Date -Format 'yyyy-MM-dd HH:mm') from ``data/remote-capture.json`` by ``tools/export-button-map.ps1``.")

foreach ($rp in $capture.remotes.PSObject.Properties) {
    $id = $rp.Name; $r = $rp.Value
    # Instance paths identify this particular PC; keep only the portable fields.
    $ifaces = @($r.interfaces | Select-Object vid, pid, interface, raw_input_type, category, usage_page, usage, input_report_length, product, manufacturer)
    $pick = { param($cat) ($ifaces | Where-Object { $_.category -eq $cat } | Select-Object -First 1).interface }
    $buttons = [ordered]@{}
    $rows = New-Object System.Collections.Generic.List[string]

    foreach ($sp in $r.steps.PSObject.Properties) {
        $step = $sp.Value
        $button = $step.button
        $sig = Get-Signature $step
        # mic has two steps (hold, tap); keep the first real signature and note its semantics.
        if (-not $buttons.Contains($button) -or ($buttons[$button].source -eq 'none' -and $sig)) {
            if ($sig) {
                $sig['semantics'] = $step.semantics
                $buttons[$button] = $sig
            } else {
                $buttons[$button] = [ordered]@{ source = 'none'; status = $step.status }
            }
        }
        $raw = if ($sig) { if ($sig.source -eq 'consumer') { "Consumer $($sig.usage)" } elseif ($sig.source -eq 'keyboard') { "$($sig.key) (vk $($sig.vk), sc $($sig.sc))" } else { $step.primary } } else { '-' }
        $status = switch ($step.status) { 'captured' { 'captured' } 'no_event' { 'sends nothing to the PC' } 'absent' { 'not on this remote' } default { $step.status } }
        $iface = if ($sig) { $sig.interface } else { '-' }
        $hold = if ($step.measured_hold_ms -ne $null) { "$($step.measured_hold_ms) ms" } else { '-' }
        $rows.Add("| $($step.title) | $status | $iface | $raw | $hold |")
    }

    $voice = $buttons['mic']
    $out.remotes[$id] = [ordered]@{
        name = $r.name; vid = $r.vid; pid = $r.pid
        keyboard_interface = & $pick 'keyboard'
        mouse_interface = & $pick 'mouse'
        consumer_interface = & $pick 'consumer-control'
        vendor_interface = & $pick 'vendor-specific'
        voice_usage = if ($voice -and $voice.source -eq 'consumer') { $voice.usage } else { $null }
        voice_semantics = if ($voice) { 'instant (tap only - use as a toggle)' } else { $null }
        buttons = $buttons
        interfaces = $ifaces
    }

    $md.Add('')
    $md.Add("## $($r.name) ($($r.vid):$($r.pid))")
    $md.Add('')
    $md.Add('| Button | Status | Interface | Sends | Measured press |')
    $md.Add('|---|---|---|---|---|')
    foreach ($row in $rows) { $md.Add($row) }
}

$md.Add('')
$md.Add('Notes')
$md.Add('')
$md.Add('- Measured press is how long the button was held during mapping, not a limit. A 5-second hold test showed true press/release on Home and the arrows.')
$md.Add('- The Voice (mic) button sends a single instant pulse no matter how long it is held, so it can only toggle Flow hands-free.')
$md.Add('- Mouse on/off is handled inside the remote and never reaches the PC. G10S Power sends System Sleep, which Windows consumes before any app can see it.')

$out | ConvertTo-Json -Depth 8 | Set-Content (Join-Path $Root 'remote-devices.json') -Encoding utf8
$md | Set-Content (Join-Path $Root 'remote-button-map.md') -Encoding utf8
Write-Host "Wrote remote-devices.json and remote-button-map.md"
