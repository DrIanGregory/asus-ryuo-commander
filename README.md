# ASUS Ryuo Commander

Fixes the **ASUS ROG Ryuo IV** AIO LCD that **dims to ~1% after sleep** — and, more
generally, whenever ASUS Info Hub isn't actively driving it — even though the software
still claims 100%.

A small Windows (.NET 8 / WPF) tray app that holds the LCD at your chosen brightness by
speaking the panel's **native USB‑HID protocol** directly. **No adb, and no ASUS Info Hub
required at runtime.**

![Brightness tab](docs/brightness-tab.png)

---

## TL;DR

- The Ryuo IV screen is a **Rockchip Android** board (device `cm16`) driving a **DSI panel**,
  with an on‑device home app (`com.baiyi.homeui.hshomeui`).
- Brightness is **not** the Linux sysfs backlight, **not** the Android `screen_brightness`
  setting, and **not** adb. It's a **vendor USB‑HID command**; the firmware applies it as the
  home‑UI window's `screenBrightness` (a per‑window override).
- The panel's firmware **dims itself into a low‑power standby a few seconds after the last
  message from the PC**. It stays awake only while the host **keeps reading its HID input
  stream** — which is what Info Hub does.
- This app replicates that: it opens the HID interface, **continuously drains the input
  stream to hold the session**, and re‑applies your brightness. That keeps the panel bright
  with Info Hub closed.

---

## How we got here (the investigation)

This started from a wrong assumption and was corrected by on‑device reverse engineering and
live testing. The dead ends are documented because they're the obvious‑but‑wrong first
guesses:

1. **sysfs backlight — WRONG.** `/sys/class/backlight/backlight/brightness` (0–256) can be
   written over adb and *reads back* the value, but `actual_brightness` never rises above
   ~13 and the panel stays dim. The node is decoupled from the DSI panel.
2. **Android `screen_brightness` — WRONG.** Setting it 10 vs 255 changed nothing on the
   panel. When Info Hub changed brightness, *none* of these values moved.
3. **adb `transfer_proxy` socket — WRONG.** That abstract socket is the Rockchip **RKNN NPU
   video server** (`/vendor/bin/rknn_server`) used for streaming frames to the panel, not
   brightness.
4. **USB HID — CORRECT.** Live `logcat` capture on the (rooted) device showed brightness
   arriving on `/dev/hidg0` from a vendor HID interface, decoded by `SerialService` and
   applied by the home‑UI app. Confirmed by a sender that visibly moved the panel.
5. **Why "Apply" seemed to do nothing / reverted:** the firmware idle‑dims ~5 s after the
   last host message. A one‑shot write applies, then the device reverts.
6. **Why it "only worked with Info Hub open":** the firmware keeps the panel awake only while
   the host **reads** its HID stream. A write‑only client rode on Info Hub's session; once
   Info Hub closed, nobody drained the stream and the panel dropped to standby. The fix is a
   background **read‑drain**.
7. **`displayInSleep` flag — rejected.** The device has a "display in sleep mode" flag; with
   it on, the panel stayed lit during sleep **but swapped to a standby video and still dimmed
   after wake**. Side effects not worth it.

---

## The protocol (verified)

**Transport:** USB HID, `VID 0x0B05` (ASUS) / `PID 0x1C76`, interface **MI_00**
(vendor usage page `0xFF00`). Device side is `/dev/hidg0`, report length **1024**.
(The composite device's `MI_01` is the ADB/video interface — a different channel.)

**Message** (HTTP‑like text, **CRLF** line endings):

```
POST brightness 1.0\r\n
SeqNumber=<n>\r\n
ContentType=json\r\n
ContentLength=<len(body)>\r\n
\r\n
{"value":N}                      # N = 0..100
```

**Framing** (byte‑stuffed):

```
0x5A | uint16_BE(wireLen) | escape(payload) | escape(checksum) | 0x5A
```

- `checksum` = additive sum of the **un‑escaped** payload bytes `& 0xFF`.
- `escape`: `0x5A → 0x5B 0x01`, `0x5B → 0x5B 0x02` (`0x5B` is the escape byte).
- `wireLen` = number of on‑wire bytes after the length field and before the trailing `0x5A`
  (i.e. `len(escape(payload)) + len(escape(checksum))`).
- Delivered as **one HID output report**: `[0x00] + frame + zero‑pad` to the report length.

**Value mapping:** the firmware computes `screenBrightness = ((int)(N × 2.55)) / 255` and
sets it as the home‑UI window's `WindowManager.LayoutParams.screenBrightness` (per‑window
override). That's why sysfs / global settings are irrelevant.

**Session / read‑drain:** the panel stays out of standby only while the host reads the
device's HID **input** reports. The app opens the interface and runs a background thread that
continuously reads and discards them, keeping the firmware's "PC connected" state true.

---

## Requirements

- Windows 10/11, **.NET 8** runtime (or the SDK to build).
- The Ryuo IV AIO connected over USB.
- **Brightness needs no adb, no ASUS Info Hub, no admin** — it talks to the HID interface
  directly via [HidSharp](https://www.nuget.org/packages/HidSharp).
- **The Video feature additionally needs** bundled `ffmpeg` (via `tools\fetch-ffmpeg.ps1`) and
  ASUS's `adb.exe` (from the ASUS Info Hub install) to upload the file. Don't run this app and
  ASUS Info Hub at the same time — they share the USB HID channel.

---

## Using it

1. Build (below) and run `RyuoBrightnessFix.exe`.
2. **Brightness** tab: drag the slider and click **Apply** (or **100%**).
3. Keep **"Hold brightness"** ticked (default) — this opens the HID session, drains the
   device stream, and re‑applies your level so the panel doesn't dim itself.
4. **"Restore this brightness after waking"** re‑applies promptly on resume.
5. **Settings** tab: **Start with Windows**, **Start minimized**, **Show tray icon** to run
   silently from the tray.

![Settings tab](docs/settings-tab.png)

The header shows the live state with a green **Connected** badge when the HID interface is
found.

---

## Change the panel video

The **Video** tab lets you set a custom looping video on the panel — no ASUS Info Hub needed.
Pick any video, click **Set as panel video**, and the app:

1. **Transcodes** it with bundled ffmpeg to the panel format
   (2240×1080, H.264 High/yuv420p, 30 fps, no audio, mp4).
2. **Pushes** it to `/sdcard/pcMedia/<timestamp>.mp4` over adb (the file bytes travel on the
   device's ADB interface — this is what Info Hub does; the HID byte-path is a dead end).
3. **Activates** it with a HID `POST waterBlockScreenId` config (verified by USB-capturing Info Hub):
   ```json
   {"id":"Customization","screenMode":"Full Screen","playMode":"Single",
    "media":["<file>.mp4"],
    "settings":{"titleColor":"#25cfe5","contentColor":"#25cfe5",
                "filter":{"value":null,"opacity":100},"badges":[]},
    "sysinfoDisplay":["","","","","",""]}
   ```
   The device re-asserts its persisted config every few seconds, so the app sends this a few
   times to reliably override the previous video.

The video plays **on the device** (looped locally by its Android board) — the PC does no
continuous streaming, so there's no ongoing CPU cost. Keep **Hold brightness** on so the
panel stays awake (session alive) to show it.

**ffmpeg**: run `tools\fetch-ffmpeg.ps1` once to download `ffmpeg.exe` next to the app (it is
git-ignored — too large for the repo). **adb**: uses ASUS's bundled `adb.exe`.

## How it works

| Piece | Role |
|-------|------|
| `BacklightService` | Talks the USB‑HID protocol via HidSharp. Opens a **persistent session** with a background **read‑drain** thread (`StartHold`/`StopHold`) to keep the panel awake; `SetPercent(p)` sends the framed `{"value":p}` command over it; `SetPanelVideo(name)` sends the `waterBlockScreenId` config. |
| `MediaService` | The Video tab: ffmpeg transcode → `adb push` to `/sdcard/pcMedia` → activate via `BacklightService.SetPanelVideo`. |
| `MainViewModel` | Slider/Apply, the 3‑second keep‑alive that re‑applies brightness, device detection, and settings. Owns the hold lifecycle. |
| `ResumeMonitor` | Subscribes to `SystemEvents.PowerModeChanged`; on resume, waits ~10 s then re‑applies the target (belt‑and‑suspenders on top of the keep‑alive). |
| `StartupRegistrationService` | "Start with Windows" via the per‑user `HKCU\…\Run` key. |
| `TrayIconService` | System‑tray icon + menu (open / restore brightness / exit). |
| Diagnostics | Toggle **verbose debug logging** in Settings; logs to `%APPDATA%\RyuoBrightnessFix\logs\`, with an "Open logs folder" button. |

---

## Caveats / limitations

- **Don't run this and ASUS Info Hub at the same time.** They use the same USB‑HID channel
  and will fight over it. Use one or the other.
- **During real sleep the panel still dims.** While the PC is suspended, nothing on the host
  can run to hold the session, so the firmware dims it. The app restores brightness within a
  few seconds of waking (keep‑alive + resume‑restore). Keeping it bright *through* sleep isn't
  achievable from the host (the device's own `displayInSleep` flag has unwanted side effects).
- **Brightness is effectively held at your chosen level while the app runs.** The device's
  persisted value isn't rewritten by the brightness command, so holding relies on the app's
  keep‑alive re‑applying it.
- Targets the **Ryuo IV** specifically (`VID 0x0B05 / PID 0x1C76`, interface MI_00). Other
  ASUS LCDs will differ.

---

## Manual reference

A standalone Python sender (used during development) lives outside this repo; the essential
logic is: enumerate HID `VID 0x0B05 / PID 0x1C76` interface MI_00, build the framed
`POST brightness 1.0 … {"value":N}` message above, and write it as a 1025‑byte output report
(`[0x00] + frame + zero‑pad`). To hold it, also open a read loop that drains the device's
input reports.

---

## Build

```powershell
dotnet build RyuoBrightnessFix.sln -c Release
```

Output: `src\RyuoBrightnessFix\bin\Release\net8.0-windows\RyuoBrightnessFix.exe`.

Dependencies (restored automatically): WPF, **HidSharp** (USB‑HID), **Microsoft.Win32.SystemEvents**
(resume detection), **Serilog** (logging).

For the **Video** feature, fetch ffmpeg once (git-ignored, ~100 MB):

```powershell
powershell -ExecutionPolicy Bypass -File tools\fetch-ffmpeg.ps1
```

The build copies `ffmpeg.exe` next to the exe if present; brightness works without it.

---

## License

GNU AGPL v3 — see [LICENSE](LICENSE).
