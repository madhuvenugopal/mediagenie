# Video Grid Studio

A Windows desktop app (C# / .NET 8, Visual Studio 2022) that combines two tools in one process:

- **MkvPlayer** (WPF) — a video/audio player with three tabs (Video via LibVLC, Audio and Voice
  Record via NAudio), a shared transport bar, and a 5-band equalizer. **This is what opens on
  launch.**
- **Video Grid Studio** (WinForms) — reachable from the player's **VideoCreator** menu — combines
  several video clips into one, in one of two modes:
  - **Play Together** — a Google-Meet-style grid. **Every clip starts at the same moment**; a clip
    that runs out before the others keeps showing its last frame until the longest one ends. The
    whole frame — tiles, gaps and placeholders — can be rendered to a **single video file**.
  - **Play Sequentially** — the same grid of tiles, but clips play **one at a time, in grid
    order**: tile 1 plays through to the end, then tile 2 starts, and so on, with finished tiles
    freezing on their last frame just like the grid does for a clip that ends early. Export
    renders the same full canvas as Play Together — every tile in its position — with each clip
    appearing in its own tile only during its own turn, into a single video file.

The two started out as separate apps (MkvPlayer, VideoEditor) and were merged into this one
project — see `revision3.md` for how and why. One `.csproj` hosts both UI frameworks
(`UseWPF` + `UseWindowsForms`); the player's WPF `MainWindow` is the startup window, and clicking
**VideoCreator** opens the grid tools' own startup picker (`LauncherForm`) as an independent
top-level window, non-modally — the player stays usable alongside it.

---

## Opening it

1. Open `VideoGridStudio.sln` in Visual Studio 2022 (17.8 or newer, .NET 8 SDK installed).
2. Restore happens on first build — the LibVLC binaries come down with the NuGet packages.
3. Set the configuration to **x64** and press F5.

The app opens on the MKV/MP4 player. Its menu bar has **File**, **Tools**, **Help**, and
**VideoCreator** — click **VideoCreator** to open the grid tools' own startup picker, with two
buttons, **Play Together (Grid)** and **Play Sequentially**; pick one to open that mode's window.
Closing a VideoCreator window doesn't affect the player; closing the player exits the whole app.

The project is deliberately x64: `VideoLAN.LibVLC.Windows` ships architecture-specific native
binaries, and mixing them with AnyCPU is the usual cause of "libvlc could not be loaded".

### FFmpeg (needed only for exporting)

Playback works out of the box. Saving the grid as one video is done by `ffmpeg.exe` — **you do not
have to install it yourself.** The first time you export without it, the app offers to fetch an
official Windows build (BtbN's GPL static build, about 80 MB). It lands in

```
%LocalAppData%\VideoGridStudio\ffmpeg
```

which needs no administrator rights and touches nothing else on the machine. The **FFmpeg…** toolbar
button opens the same dialog at any time, and also lets you point at a copy you already have.

If you would rather install it system-wide: `winget install Gyan.FFmpeg`, `choco install ffmpeg`, or
`scoop install ffmpeg`. The app searches its own download folder, the folder next to the executable,
the process **and** machine/user `PATH` (so an FFmpeg installed while the app was open is still
found), winget's package and shim folders, Chocolatey, Scoop, and `C:\ffmpeg\bin`. Whatever it ends
up using is remembered in `%AppData%\VideoGridStudio\settings.json`.

The downloaded build is GPL-licensed; if you redistribute this app with FFmpeg bundled, that licence
applies to the bundle.

---

## Using it

**Play Together (grid):**

| Action | How |
| --- | --- |
| Fill tiles | **Add videos…**, or drag files onto the window / onto one tile |
| Change a single tile | Double-click it, or right-click → *Choose video…* |
| Empty a tile | Right-click → *Clear this slot* |
| Grid shape | Toolbar dropdown: 2×2, 3×3, **4×4**, 5×5, 3×2, 4×3 |
| Play | **Play all** (or `Space`); `Esc` stops |
| Save one video | **Export video…** |

Placeholder tiles show a numbered avatar, the file name and the duration, so an unfilled grid still
looks like a call with everyone's camera off.

**Play Sequentially (same grid, one clip at a time):**

| Action | How |
| --- | --- |
| Fill tiles | **Add videos…**, or drag files onto the window / onto one tile |
| Change a single tile | Double-click it, or right-click → *Choose video…* |
| Empty a tile | Right-click → *Clear this slot* |
| Grid shape | Toolbar dropdown: 2×2, 3×3, **4×4**, 5×5, 3×2, 4×3 |
| Play | **Play in order** (or `Space`) — plays tile 1, then tile 2, and so on; `Esc` stops |
| Save one video | **Export video…** — concatenates every filled tile's clip, in grid order, into one file |

The tile currently playing is the only one showing live video; tiles not reached yet show their
placeholder, and finished tiles freeze on their last frame — so a run in progress reads like the
grid filling in one tile at a time.

---

## How the export works

The exported frame is composited by FFmpeg rather than screen-recorded, so the result is
frame-accurate and independent of how fast the preview happened to run.

1. The placeholder grid — background, gaps, and every empty tile — is drawn **once** to a PNG by the
   very same code that paints the on-screen tiles.
2. That PNG becomes the bottom layer, looped for the length of the longest clip.
3. Each clip is scaled into its own tile (aspect ratio preserved, letterboxed), then
   `tpad=stop_mode=clone` holds its final frame for the remainder of the timeline.
4. The clips are overlaid onto the background at their exact tile coordinates.
5. Audio is resampled, padded to full length and mixed; a limiter catches the peaks that appear when
   several tiles have sound at once.

The finished file is H.264 + AAC in MP4, with `+faststart`.

**Audio choices** in the export dialog: mix every clip, use only one tile's audio, or export silent.
The toolbar has a simpler *Mix all / Muted* switch that affects the live preview.

**Output options:** 720p / 1080p / 2K / 4K, 24–60 fps, three quality presets, and an adjustable gap
between tiles. Progress is read from FFmpeg's own `-progress` stream, and the export can be
cancelled mid-run.

**Sequential export** composites the exact same canvas as the grid export — same background PNG,
same per-tile `scale`/`pad`/letterbox, same `overlay` at each tile's coordinates — but each clip's
overlay is time-shifted (`setpts=PTS+start/TB`) to appear only once the previous clip's tail ends,
gated by `overlay=...:enable='gte(t,start)'` so the placeholder canvas shows through until then,
and held on its last frame afterwards (`tpad=stop_mode=clone`) for the rest of the run. Audio is
shifted the same way per clip (`adelay`) and mixed together — harmless since the clips never
overlap in time. The finished file is as long as every clip's duration added together, and uses
the same output options (resolution/fps/quality/tile gap/audio choice) as the grid export.

---

## Project layout

```
src/VideoGridStudio/
  App.xaml(.cs)                    WPF app entry point (StartupUri -> MainWindow); bootstraps
                                    WinForms visual styles/high-DPI once at startup
  MainWindow.xaml(.cs)             MkvPlayer: menu (incl. VideoCreator), tabs, transport bar
  PreferencesWindow.xaml(.cs)      MkvPlayer: equalizer + default media folder settings
  MediaItem.cs                     MkvPlayer: one playlist entry (file path + display name)
  Audio/                           MkvPlayer: NAudio playback engine, recorder, .m3u playlists
  Equalizer/                       MkvPlayer: shared 5-band model + per-engine adapters
  Settings/                        MkvPlayer: HKCU/HKLM registry read/write wrapper
  Models/
    GridSettings.cs                grid shape, output size, tile geometry, audio mode
    ClipSlot.cs, CellState.cs      one tile's clip and lifecycle
    AudioMode.cs
  Rendering/
    PlaceholderRenderer.cs         draws tiles for BOTH the UI and the FFmpeg background
    Theme.cs                       shared colours
  Controls/
    VideoCellControl.cs            one tile: painted surface + LibVLC surface, drag and drop
    OscilloscopeControl.cs, OscilloscopeLineControl.cs   MkvPlayer: live waveform (WPF, unrelated
                                    namespace -- co-located here, no relation to VideoCellControl)
  Playback/
    GridPlayer.cs                  starts every tile together, freezes each on its last frame
    SequentialGridPlayer.cs        starts one tile at a time in grid order, freezing each as it ends
    PlaybackProgressEventArgs.cs
  Export/
    GridFilterGraphBuilder.cs      builds the FFmpeg filter graph -- every tile live from t=0
    GridCompositionPlan.cs
    SequentialGridFilterGraphBuilder.cs  same canvas, but each tile's overlay is time-shifted to
                                    its own turn (setpts/tpad/enable) instead of starting at t=0
    FfmpegRunner.cs                runs FFmpeg, reports progress, supports cancel (shared by both)
    ExportPresets.cs               resolution/fps/quality choices shared by both export modes
    MediaProbe.cs                  ffprobe wrapper (duration, audio present)
    FfmpegLocator.cs               finds an existing FFmpeg in all the usual places
    FfmpegInstaller.cs             downloads and unpacks one if there is none
    AppSettings.cs
  Forms/
    LauncherForm.cs                opened from MainWindow's VideoCreator menu: pick Play
                                    Together or Play Sequentially
    MainForm.cs                    grid, toolbar, status bar -- clips start together
    SequencePlayerForm.cs          same grid, toolbar, status bar -- clips play one after another
    ExportDialog.cs                output settings and export progress for BOTH modes
                                    (constructor's `sequential` flag picks which builder to call)
    FfmpegSetupDialog.cs           download / locate FFmpeg
```

---

## Notes and limits

- **Preview load.** Sixteen simultaneous HD decodes is genuinely heavy. Hardware decoding is on, but
  on a modest machine the preview may stutter — this affects only what you see live. The exported
  file is rendered offline and is unaffected.
- **Start skew.** In Play Together, tiles are started in a loop, so the live preview can be a few
  tens of milliseconds out of step between tiles; export always aligns every clip exactly at t = 0.
- **Frozen frames in the preview** come from a VLC snapshot taken just before the end of each clip.
  If a snapshot fails, that tile falls back to its placeholder; the export is not affected.
- **Length.** In Play Together, the finished video is as long as the longest clip. In Play
  Sequentially, it's every clip's length added together.
- The forms are built in code rather than with `.Designer.cs` files, so the Visual Studio designer
  surface will not open them — the layout lives in `BuildToolStrip` / `BuildGridHost` / `BuildLayout`.
"# mediagenie" 
