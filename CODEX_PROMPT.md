# Task: Make the Video-tab "Scale mode" change visibly work for ALL media-library entries

## Repo / app

`c:\projects\general_programs\AsusRyuoCommander` — a WPF .NET 8 app (`src/RyuoBrightnessFix`)
that controls an ASUS Ryuo IV AIO cooler LCD over USB HID + adb. The Video tab keeps a
media library of videos that play on the cooler's 2240×1080 panel. Videos are transcoded
with a bundled ffmpeg to 1920×960 H.264 (the panel's decoder limit), pushed to
`/sdcard/pcMedia` over adb, and activated over HID.

## The user-visible bug

The "Scale mode" ComboBox (Fill / Fit / Stretch) on the Video tab appears to do nothing.
The user selects Stretch; the playing video stays cropped (Fill).

## Root cause (verified)

The scale mode is applied by baking it into the pixels at **transcode time** only
(`MediaService.Transcode`, `src/RyuoBrightnessFix/Services/MediaService.cs` — the ffmpeg
`-vf` filter chain). Changing the ComboBox after a video is in the library changed a
setting that nothing re-read. Verified empirically: SSIM-matching the cached transcodes in
`%APPDATA%\RyuoBrightnessFix\videocache` against the three candidate filter chains shows
every existing library entry was encoded as Fill even though `settings.json` says Stretch.

## What is ALREADY implemented (v1.9.0, built and currently running — do not redo)

Uncommitted working-tree changes implement live re-encoding:

- `Models/PanelVideoEntry.cs` (new): persisted library entry = device file name +
  `SourcePath` + baked `ScaleMode`.
- `Models/AppSettings.cs`: `PanelVideos` list + `Normalize()` migration from the legacy
  `PanelVideoFiles` name list (legacy entries get null SourcePath/ScaleMode);
  `JsonStringEnumConverter` registered globally.
- `ViewModels/MainViewModel.cs`:
  - `PlaylistItem` now carries `SourcePath` + `BakedScaleMode`; `AddVideo()` records them.
  - `SelectedVideoScaleMode` setter fires `ReencodeLibraryForScaleModeAsync()`: re-transcodes
    every entry whose source file exists, uploads under a fresh timestamp name, swaps the
    playlist item in place, re-asserts the playlist, then deletes replaced uploads
    (`MediaService.TryDeleteDeviceVideoAsync`). Single-runner guard, converges under rapid
    ComboBox flipping, serializes with an in-flight Add via `VideoBusy`.
  - Entries with no usable source set a status line: "N video(s) can't be re-encoded …
    remove and re-add them."

Build is clean (0 warnings). Settings migration verified against the user's real
settings.json via the compiled assembly.

## The remaining gap — YOUR task

The user's 4 existing entries predate provenance tracking, so `SourcePath`/`ScaleMode`
are null and the re-encoder correctly refuses — the ComboBox still "does nothing" for
them apart from the status text. Fix this properly:

1. **Relink flow for legacy entries.** When a scale-mode change (or an explicit UI action)
   finds entries with unknown/missing sources, let the user relink them: per-entry
   "locate source file" (OpenFileDialog), storing `SourcePath` into the entry and then
   feeding it through the existing re-encode path. No silent skips — every non-re-encodable
   entry must be visibly actionable, not just mentioned in a status line. Consider a
   prominent inline warning panel in the Media library card rather than the easy-to-miss
   status TextBlock.

2. **Seed this user's provenance now** (one-time data fix, exact mapping recovered from the
   app's own logs `%APPDATA%\RyuoBrightnessFix\logs\ryuo-20260702.log` / `ryuo-20260707.log`;
   baked mode Fill verified by SSIM):

   | device file | source | baked mode |
   |---|---|---|
   | 2026-07-02_13-52-55-065.mp4 | C:\computer_specific_files\aio_cooler_videos\dog_at_the_beah.mp4 | Fill |
   | 2026-07-02_16-38-02-495.mp4 | C:\computer_specific_files\aio_cooler_videos\small_round-faced_character_lying_on_a_couch.mp4 | Fill |
   | 2026-07-07_09-53-11-007.mp4 | C:\computer_specific_files\aio_cooler_videos\space_station.mp4 | Fill |
   | 2026-07-07_09-53-16-452.mp4 | C:\computer_specific_files\aio_cooler_videos\unicorn.mp4 | Fill |

   The app holds settings in memory and rewrites `settings.json` on every save, so patch the
   file ONLY while the app is not running, or implement the relink flow and let the user do
   it in-app.

3. **Verify end-to-end** before declaring done: flip the mode, confirm a new transcode
   appears in the videocache, and confirm its content actually matches the chosen mode
   (e.g. extract a frame with ffmpeg and compare against the expected filter output —
   portrait source `unicorn.mp4` makes Fill/Fit/Stretch trivially distinguishable).

## Hard constraints

- **NEVER kill or restart the running RyuoBrightnessFix process** — the user is using it.
  Building is fine (compile errors surface before the locked-exe copy step); the user
  chooses when to restart.
- No hacks, no placeholder code, full error handling at service boundaries; the fix lives
  in the correct layer (transcode logic in MediaService, orchestration in MainViewModel,
  persistence in AppSettings).
- Preserve settings backward/forward compatibility (`PanelVideoFiles` name-list mirror must
  stay in sync — session re-assert `OnSessionOpened` reads it).
- Bump `<Version>` in the csproj on user-visible change.
- There are unrelated uncommitted changes in `StartupRegistrationService.cs` and
  `SystemMetricsService.cs` — leave them untouched.
