# Remote button map

Generated 2026-09-23 01:20 from `data/remote-capture.json` by `tools/export-button-map.ps1`.

## G20S (0x4842:0x0001)

| Button | Status | Interface | Sends | Measured press |
|---|---|---|---|---|
| Voice (mic) - hold 3 seconds | captured | MI_02&COL02 | Consumer 0x00CF | 25 ms |
| Voice (mic) - quick tap | captured | MI_02&COL02 | Consumer 0x00CF | 6 ms |
| Home (your current Flow button) | captured | MI_02&COL02 | Consumer 0x0223 | 150 ms |
| Mute | sends nothing to the PC | - | - | - |
| Pg+ | captured | MI_02&COL01 | PageUp (vk 0x21, sc E0 49) | 190 ms |
| Mouse on/off | sends nothing to the PC | - | - | - |
| Pg- | captured | MI_02&COL01 | Next (vk 0x22, sc E0 51) | 410 ms |
| Up | captured | MI_02&COL01 | Up (vk 0x26, sc E0 48) | 390 ms |
| Down | captured | MI_02&COL01 | Down (vk 0x28, sc E0 50) | 424 ms |
| Left | captured | MI_02&COL01 | Left (vk 0x25, sc E0 4B) | 439 ms |
| Right | captured | MI_02&COL01 | Right (vk 0x27, sc E0 4D) | 490 ms |
| OK | captured | MI_02&COL02 | Consumer 0x0041 | 449 ms |
| Back | captured | MI_02&COL02 | Consumer 0x0224 | 400 ms |
| Volume - | captured | MI_02&COL02 | Consumer 0x00EA | 370 ms |
| Volume + | captured | MI_02&COL02 | Consumer 0x00E9 | 340 ms |
| Previous | captured | MI_02&COL02 | Consumer 0x00B6 | 330 ms |
| Play / Pause | captured | MI_02&COL02 | Consumer 0x00CD | 430 ms |
| Next | captured | MI_02&COL02 | Consumer 0x00B5 | 379 ms |
| 1 | captured | MI_02&COL01 | D1 (vk 0x31, sc 02) | 460 ms |
| 2 | captured | MI_02&COL01 | D2 (vk 0x32, sc 03) | 443 ms |
| 3 | captured | MI_02&COL01 | D3 (vk 0x33, sc 04) | 440 ms |
| 4 | captured | MI_02&COL01 | D4 (vk 0x34, sc 05) | 400 ms |
| 5 | captured | MI_02&COL01 | D5 (vk 0x35, sc 06) | 430 ms |
| 6 | captured | MI_02&COL01 | D6 (vk 0x36, sc 07) | 420 ms |
| 7 | captured | MI_02&COL01 | D7 (vk 0x37, sc 08) | 400 ms |
| 8 | captured | MI_02&COL01 | D8 (vk 0x38, sc 09) | 442 ms |
| 9 | captured | MI_02&COL01 | D9 (vk 0x39, sc 0A) | 400 ms |
| DEL (backspace) | captured | MI_02&COL01 | Back (vk 0x08, sc 0E) | 394 ms |
| 0 | captured | MI_02&COL01 | D0 (vk 0x30, sc 0B) | 380 ms |
| Menu (hold = backlight) | captured | MI_02&COL01 | Apps (vk 0x5D, sc E0 5D) | 46 ms |
| Power (optional) | sends nothing to the PC | - | - | - |

## G10S (0x1915:0x1025)

| Button | Status | Interface | Sends | Measured press |
|---|---|---|---|---|
| Voice (mic) - hold 3 seconds | captured | MI_03&COL02 | Consumer 0x00CF | 108 ms |
| Voice (mic) - quick tap | captured | MI_03&COL02 | Consumer 0x00CF | 96 ms |
| Home / Back | captured | MI_03&COL02 | Consumer 0x0224 | 104 ms |
| Play / Pause | captured | MI_03&COL02 | Consumer 0x00CD | 256 ms |
| Mouse on/off | sends nothing to the PC | - | - | - |
| Up | captured | MI_02 | Up (vk 0x26, sc E0 48) | 256 ms |
| Down | captured | MI_02 | Down (vk 0x28, sc E0 50) | 298 ms |
| Left | captured | MI_02 | Left (vk 0x25, sc E0 4B) | 340 ms |
| Right | captured | MI_02 | Right (vk 0x27, sc E0 4D) | 272 ms |
| OK | captured | MI_02 | Return (vk 0x0D, sc 1C) | 291 ms |
| Menu | captured | MI_02 | Apps (vk 0x5D, sc E0 5D) | 472 ms |
| PAGE up | captured | MI_02 | PageUp (vk 0x21, sc E0 49) | 271 ms |
| PAGE down | captured | MI_02 | Next (vk 0x22, sc E0 51) | 299 ms |
| VOL + | captured | MI_03&COL02 | Consumer 0x00E9 | 244 ms |
| VOL - | captured | MI_03&COL02 | Consumer 0x00EA | 260 ms |
| DEL | captured | MI_02 | Back (vk 0x08, sc 0E) | 280 ms |
| Mute | captured | MI_03&COL02 | Consumer 0x00E2 | 296 ms |
| Power (optional) | sends nothing to the PC | - | - | - |

Notes

- Measured press is how long the button was held during mapping, not a limit. A 5-second hold test showed true press/release on Home and the arrows.
- The Voice (mic) button sends a single instant pulse no matter how long it is held, so it can only toggle Flow hands-free.
- Mouse on/off is handled inside the remote and never reaches the PC. G10S Power sends System Sleep, which Windows consumes before any app can see it.
