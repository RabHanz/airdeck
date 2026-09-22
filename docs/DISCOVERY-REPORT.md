# Air remote discovery report

Hardware discovery for the G20S and G10S air-mouse remotes, done on 22–23 Sep 2026 on Windows 11 with `airdeck-mapper.exe`, `hidtool.exe` and a 5-second hold test.
The full per-button table is in [remote-button-map.md](../remote-button-map.md). The raw captures are in `data/remote-capture.json`.

## Devices

| Remote | USB receiver | Interfaces (raw input) | Microphone |
|---|---|---|---|
| G20S | `4842:0001` HAOBO "USB Composite Device" | MI_02&COL01 keyboard, MI_02&COL02 consumer control, MI_02&COL03 system control, MI_03 mouse, MI_04 vendor 0xFF00 (65-byte reports) | "USBAudio2.0" (Wispr Flow uses this mic) |
| G10S | `1915:1025` "2.4G Composite Device" | MI_02 keyboard, MI_03&COL01 mouse, MI_03&COL02 consumer control, MI_03&COL03 system control | "MIC-Wireless Device" |

The system-control collections are opened exclusively by Windows. Raw input cannot read them.

## Key findings

- **The mic button is Case B on both remotes.** It sends Consumer `0x00CF` (Voice Command) as one instant pulse: 6–25 ms on the G20S and about 100 ms on the G10S, whether it is tapped or held for 5 seconds. It has no hold information, so it is mapped to the Flow **hands-free toggle** (`Ctrl+Win+Space`).
- **The G20S "browser button" is the Home key.** It sends Consumer `0x0223` (AC Home → Browser_Home). The 5-second hold test measured a 6.4-second DOWN→UP, so it is a **true hold** and drives Flow **push-to-talk** (`Ctrl+Win`).
- The arrows auto-repeat while held, so they are true holds as well.
- **No signal reaches the PC from:**
  - the Mouse on/off key on both remotes (it is handled inside the remote);
  - G20S Mute and G20S Power (infrared-only);
  - G10S Power. It sends System Sleep through the system-control collection. Windows acts on that before any app can see it. Turn it off in *Power Options → When I press the sleep button → Do nothing* if needed.
- **Consumer keys:** Home, Back, OK (G20S), Voice, volume and media keys arrive as raw HID reports first. Windows then synthesises the matching virtual key about 8 ms later, with no source device. Airdeck uses the raw report to know the key came from the remote and blocks only that synthesised key. **No driver is needed.**
- **Keyboard keys:** arrows, 0–9, Pg+/Pg-, DEL and Menu (Apps), plus the G10S OK (Enter), are ordinary keyboard strokes on the remote's keyboard interface. They can only be remapped per device with the Interception filter driver (`tools/install-interception.cmd`, then re-plug the receivers). Airdeck filters **only** the remotes' keyboard interfaces and never the main keyboard.

## Final mapping (AI dictation workflow profile)

| Button | Remote | Interface | Raw input | Action |
|---|---|---|---|---|
| Home | G20S | Consumer (MI_02&COL02) | 0x0223 | Flow push-to-talk (hold) |
| Home / Back | G10S | Consumer (MI_03&COL02) | 0x0224 | Flow push-to-talk (hold) |
| Voice | G20S, G10S | Consumer | 0x00CF | Flow hands-free toggle |
| Right / Left | G20S, G10S | Keyboard | E0 4D / E0 4B | Next / previous input spot (needs driver) |
| 1–9 | G20S | Keyboard | 02–0A | Jump to input spot 1–9 (needs driver) |
| Pg+ / Pg- | G20S, G10S | Keyboard | E0 49 / E0 51 | Next / previous virtual desktop (needs driver) |
| Play/Pause | G20S, G10S | Consumer | 0x00CD | Enter |
| Back | G20S | Consumer | 0x0224 | Cancel dictation (Esc) |
| OK | G20S | Consumer | 0x0041 (Menu Pick) | Stock (Windows ignores it) |
| OK | G10S | Keyboard | 1C (Enter) | Stock |
| Gyro, clicks, scroll | both | Mouse | - | Always passed through untouched |

The Stock, Wispr Flow and Flow + desktop profiles are in `profiles/`. The Stock profile leaves a remote exactly as it came.

## Acceptance tests

| Test | Status |
|---|---|
| Device isolation, consumer keys (remote Home → Flow; keyboard Browser_Home unchanged) | Implemented. Needs your confirmation with both devices. |
| Flow PTT: hold Home → listening, release → inserts | Implemented. Home is proven to be a true hold. Needs your confirmation in Flow. |
| Mic button: tap → hands-free starts, tap → stops | Implemented as Case B. Needs your confirmation. |
| Air mouse movement unaffected | By design: mouse input is never intercepted. |
| Keyboard safety: unmapped keys unchanged | By design. The hook only acts on the browser/media/volume virtual keys that follow a mapped remote report. |
| Remote keyboard keys (arrows, digits, Pg±) | Needs the Interception driver, then a receiver re-plug or reboot. |
| Reboot → profiles load automatically | Turn on *Settings → Start with Windows* after the manual tests. |
