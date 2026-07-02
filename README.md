# ASUS Ryuo Commander

Take full control of the **ASUS ROG Ryuo IV** AIO LCD — **brightness that actually holds**
and **any video you like on the panel** — without ASUS Info Hub running.

A small Windows (.NET 8 / WPF) tray app that speaks the panel's **native USB‑HID protocol**
directly, keeps the screen alive, plays your video, and **heals every failure mode the panel
firmware throws at it** — automatically.

**Example: a custom video looping on the Ryuo IV at 100% brightness, with live metric
widgets (CPU/GPU temperature, CPU usage, AIO pump RPM, motherboard temperature, clock):**

![Custom video with live metrics on the Ryuo IV LCD](docs/panel-example.png)

| Brightness | Video | Settings |
|:---:|:---:|:---:|
| ![Brightness tab](docs/brightness-tab.png) | ![Video tab](docs/video-tab.png) | ![Settings tab](docs/settings-tab.png) |

---

## What it does

- **Holds the LCD at your chosen brightness.** The panel firmware idle‑dims to ~20% a few
  seconds after the PC stops talking to it (that's the infamous "dims after sleep even though
  Armoury Crate says 100%"). The app keeps a live HID session with a read‑drain and re‑applies
  your level every 3 s.
- **Plays your video on the panel.** Pick any video file; it's transcoded to the panel's
  playable format, uploaded, and looped full‑screen — the same path Info Hub uses. Choose the
  **scale mode**: *Fill* (crop to cover the screen, default), *Fit* (letterbox), or *Stretch* —
  The Video tab embeds an **LCD‑shaped screen** that always shows **what the panel is playing
  right now**, and switches to a live preview of the file you're choosing — with the selected
  scale mode applied — so you see exactly how the cooler will render it before uploading.
- **Shows live system metrics on the panel.** Up to six widgets over the video (CPU/GPU
  temperature, loads, clocks, fan/pump RPM, motherboard temperature, clock) — the same
  telemetry Info Hub streams, reverse‑engineered (`STATE all` snapshots every 3 s) and fed
  from LibreHardwareMonitor. Full sensor set (CPU temp, fan RPM) needs the app run as
  administrator; loads/GPU/memory/disk/network work without. With **Start with Windows**
  ticked, run the app as administrator **once** and it registers an elevated Task Scheduler
  logon task — from then on it auto‑starts as administrator on every boot, no UAC prompt.
- **Survives everything.** Panel reboots, USB re‑enumeration, PC sleep, firmware wedges — the
  app detects each one and restores both brightness *and* your video with no interaction:
  - HID sessions **self‑heal** (failed writes reopen the session and retry);
  - the panel is **re‑detected on USB hot‑plug**;
  - a **wedged firmware is un‑wedged automatically** (see below);
  - the **video is re‑asserted** whenever the panel reconnects, because the panel forgets its
    screen config on every reboot and would otherwise sit on a black screen.
- Tray app: start with Windows, start minimized, click‑to‑copy version, rolling logs.

---

## How we got here (the investigation)

This started from a wrong assumption and was corrected by on‑device reverse engineering and
live testing. The dead ends are documented because they're the obvious‑but‑wrong first
guesses:

1. **sysfs backlight — WRONG.** `/sys/class/backlight/backlight/brightness` (0–256) can be
   written over adb and *reads back* the value, but `actual_brightness` never rises above
   ~13 and the panel doesn't respond. The node is decoupled from what you see.
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
7. **Why the panel sometimes ignored everything (the firmware wedge):** the firmware tries to
   send the host data every ~100 ms. If the host stops reading for more than a moment — the
   controlling app exits, or the PC sleeps — the firmware's send path errors out, it **nulls
   its own HID handle** (`SerialService` logs `hidHandle == null` forever after), and then
   **silently discards every host message**. Host writes still "succeed"; the panel just sits
   dim. It never recovers by itself. The app detects the wedge (writes succeed while the input
   stream stays silent > 30 s) and restarts the panel's `SerialService` via ASUS's bundled
   `adb.exe`; the USB gadget re‑enumerates and everything reconnects hands‑free. **This wedge
   is the real reason the panel "stays dim after sleep"** even with Info Hub claiming 100%.
8. **Why a "successfully set" video can show a black screen:** the panel is 2240×1080 but its
   Rockchip **RK3562 decoder rejects H.264 wider than 1920** (`isCodecSupport error: support
   width = 1920` → `MediaPlayer Error (1,0)` → black). ASUS's own stock videos are
   **1920×960** and the panel stretches the frame across the full screen (SurfaceFlinger maps
   the buffer to the whole display, center‑cropping ~3% of height). The app transcodes to
   exactly that format.
9. **`displayInSleep` flag — rejected.** The device has a "display in sleep mode" flag; with
   it on, the panel stayed lit during sleep **but swapped to a standby video and still dimmed
   after wake**. Side effects not worth it.

---

## The protocol (verified)

**Transport:** USB HID, `VID 0x0B05` (ASUS) / `PID 0x1C76`, interface **MI_00**
(vendor usage page `0xFF00`). Device side is `/dev/hidg0`, report length **1024**.
(The composite device's `MI_01` is the ADB/video interface — a different channel.)

**Message** (HTTP‑like text, **CRLF** line endings):

```
POST <cmdType> 1.0\r\n
SeqNumber=<n>\r\n
ContentType=json\r\n
ContentLength=<len(body)>\r\n
\r\n
<json body>
```

- `cmdType=brightness`, body `{"value":N}` (N = 0..100). The firmware computes
  `screenBrightness = ((int)(N × 2.55)) / 255` and applies it as the home‑UI window's
  `WindowManager.LayoutParams.screenBrightness` (a per‑window override) — which is why
  sysfs / global settings are irrelevant.
- `cmdType=waterBlockScreenId`, body
  `{"id":"Customization","screenMode":"Full Screen","playMode":"Single","media":["<file>"],
  "sysinfoDisplay":["<slot1>",…,"<slot6>"],…}`
  makes the home‑UI loop `/sdcard/pcMedia/<file>` full‑screen (stock preset names from
  `/sdcard/pcMediaPreset` also resolve) and configures the six metric widget slots. Valid slot
  tokens (extracted from the HomeUI apk): `CPU Temperature/Usage/Load/Speed Average/Voltage`,
  `GPU Temperature/Usage/Load/Speed/Frequency/Power/Voltage`, `Memory Frequency`,
  `Motherboard Temperature`, `Date&Time`, `Fan Speed <fan name>`. Empty string hides a slot.
  The file itself is pushed over **adb** (MI_01), exactly as Info Hub does.
- **Telemetry stream** (first line `STATE all 1`, plus a `Date=<unix ms>` header): a JSON
  snapshot of live values the widgets render, sent every few seconds —
  `{"network":{…},"memory":{…},"cpu":{"load","temperature","temperaturePackage",
  "speedAverage","power","voltage","usage"},"gpu":{…},"disk":{…},
  "fans":[{"onBoard":true,"name":"AIO Pump","value":3146},…],"motherboard":{…},"timestamp":…}`.
  Notably the **AIO pump RPM is sent *to* the panel by the PC**, not measured by the cooler.

**Framing** (byte‑stuffed):

```
0x5A | uint16_BE(wireLen) | escape(payload) | escape(checksum) | 0x5A
```

- `checksum` = additive sum of the **un‑escaped** payload bytes `& 0xFF`.
- `escape`: `0x5A → 0x5B 0x01`, `0x5B → 0x5B 0x02` (`0x5B` is the escape byte).
- `wireLen` = number of on‑wire bytes after the length field and before the trailing `0x5A`
  (i.e. `len(escape(payload)) + len(escape(checksum))`).
- Delivered as **one HID output report**: `[0x00] + frame + zero‑pad` to the report length.

**Session / read‑drain:** the panel stays out of standby only while the host reads the
device's HID **input** reports. The app opens the interface and runs a background thread that
continuously reads and discards them, keeping the firmware's "PC connected" state true.
A healthy panel streams reports constantly (~10/s) — their absence while writes succeed is
how the app detects the firmware wedge described above.

**Video format:** H.264 High, yuv420p, **1920×960**, 30 fps, no audio, mp4 — identical to
ASUS's stock videos. Wider than 1920 is rejected by the hardware decoder (black screen).

---

## Requirements

- Windows 10/11, **.NET 8** runtime (or the SDK to build).
- The Ryuo IV AIO connected over USB.
- **No admin rights; ASUS Info Hub doesn't need to run.** The app talks to the HID interface
  directly via [HidSharp](https://www.nuget.org/packages/HidSharp). Info Hub's **installed
  files** are still wanted: its bundled `adb.exe` is used to upload videos and to un‑wedge
  the panel firmware automatically (without it, recovery falls back to "power‑cycle the PC").
- `ffmpeg.exe` next to the app for the Video feature (run `tools\fetch-ffmpeg.ps1` once; the
  binary is git‑ignored and bundled into builds automatically).

---

## Using it

1. Build (below) and run `RyuoBrightnessFix.exe`.
2. **Brightness** tab: drag the slider and click **Apply** (or **100%**). Keep
   **"Hold brightness"** ticked (default) — this opens the HID session, drains the device
   stream, and re‑applies your level so the panel doesn't dim itself.
   **"Restore this brightness after waking"** re‑applies promptly on resume.
3. **Video** tab: **Choose video…**, pick a **Scale mode** (*Fill* crops to cover the whole
   screen — no bars; *Fit* letterboxes; *Stretch* distorts) and check the **LCD preview**
   (the panel's exact shape, with your scale mode applied live), then **Set as panel video**.
   The video is remembered and re‑asserted automatically whenever the panel reconnects.
   Tip: at 100% brightness a dark video still looks dim — that's the footage, not the
   backlight.
4. **Metrics** tab: tick **Show live system metrics on the LCD** and pick up to six widgets
   (temperatures, loads, fan/pump speeds, clock). Values refresh every 3 s while the app
   runs; the slot layout rides along with the video config and survives panel reboots. Run
   the app as administrator for CPU temperature and fan RPMs.
5. **Settings** tab: **Start with Windows**, **Start minimized**, **Show tray icon** to run
   silently from the tray; verbose logging and the activity pane for diagnostics.

The header shows the live state with a green **Connected** badge; the status bar shows the
version (click to copy).

---

## How it works

| Piece | Role |
|-------|------|
| `BacklightService` | Talks the USB‑HID protocol via HidSharp. Opens a **persistent session** with a background **read‑drain** thread to keep the panel awake; `SetPercent(p)` / `SetPanelVideo(f)` send framed commands over it. Sessions **self‑heal** (a failed write reopens and retries), HID **hot‑plug** events re‑detect the panel, and the time since the last device report is tracked as the wedge signal. |
| `PanelRecoveryService` | Un‑wedges the panel firmware: restarts the on‑device `SerialService` via ASUS's bundled `adb.exe` when writes succeed but the panel has gone silent. |
| `MediaService` | The Video pipeline: ffmpeg transcode (1920×960, scale mode applied), adb push to `/sdcard/pcMedia`, HID activation. |
| `SystemMetricsService` | Collects live sensors via LibreHardwareMonitor and renders the `STATE all` JSON snapshot the panel's widgets consume. |
| `MainViewModel` | Slider/Apply, the 3‑second keep‑alive, wedge detection, hot‑plug refresh, **panel‑state re‑assert** (video + brightness on every session open), Video tab, and settings. |
| `ResumeMonitor` | On resume from sleep, re‑applies the target promptly (the wedge detector handles the firmware's post‑sleep state a few seconds later). |
| `StartupRegistrationService` | "Start with Windows" via the per‑user `HKCU\…\Run` key — or, once the app has run elevated, via a **Task Scheduler logon task with highest privileges** (auto‑starts as administrator, no UAC prompt). Self‑heals stale exe paths on every start. |
| `TrayIconService` | System‑tray icon + menu (open / restore brightness / exit). |
| Diagnostics | Toggle **verbose debug logging** in Settings; logs to `%APPDATA%\RyuoBrightnessFix\logs\`, with an "Open logs folder" button. |

---

## Caveats / limitations

- **Don't run this and ASUS Info Hub at the same time.** They use the same USB‑HID channel
  and will fight over it. Use one or the other.
- **During real sleep the panel still dims — and wedges.** While the PC is suspended nothing
  can read the panel's stream, so the firmware dims it *and* wedges its HID handle. On wake,
  the app's wedge detection notices the silent panel within ~30–45 s and restarts the panel's
  `SerialService` over adb, after which brightness and the video re‑apply automatically.
  Expect up to a minute of dim panel after wake. Keeping it bright *through* sleep isn't
  achievable from the host (the device's own `displayInSleep` flag has unwanted side effects).
- **Brightness is held, not persisted.** The device's own saved value isn't rewritten by the
  brightness command, so holding relies on the app's keep‑alive re‑applying it.
- Targets the **Ryuo IV** specifically (`VID 0x0B05 / PID 0x1C76`, interface MI_00). Other
  ASUS LCDs will differ.

---

## Build

```powershell
dotnet build RyuoBrightnessFix.sln -c Release
powershell -File tools\fetch-ffmpeg.ps1   # once, for the Video feature
```

Output: `src\RyuoBrightnessFix\bin\Release\net8.0-windows\RyuoBrightnessFix.exe`.

Dependencies (restored automatically): WPF, **HidSharp** (USB‑HID), **Microsoft.Win32.SystemEvents**
(resume detection), **Serilog** (logging). ffmpeg and ASUS's adb are external tools invoked
by the Video / recovery features.

---

## License

GNU AGPL v3 — see [LICENSE](LICENSE).
