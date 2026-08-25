# Revision 5 -- Winamp-style transport chrome, LED spectrum analyzer, click-to-play tiles, and a common mute/EQ button

Changes made to `VideoGridStudio` in this revision, in the order they were made. Every step was
verified with `dotnet build` after each change; most of the visual work was additionally verified
by rendering the affected controls off-screen through small throwaway harnesses (outside the repo)
rather than driving the real app interactively -- see **Validated** at the end for exactly what
that did and didn't exercise.

## 1. Volume controls and Winamp-style toolbar chrome for the grid tools (WinForms)

**New files:** `Rendering/WinampToolStripRenderer.cs`, `Controls/WinampVolumeSlider.cs`.
**Files:** `Playback/GridPlayer.cs`, `Playback/SequentialGridPlayer.cs`, `Forms/ClipPlayerForm.cs`,
`Forms/SequencePlayerForm.cs`

`ClipPlayerForm` ("Play Together") and `SequencePlayerForm` ("Play Sequentially") gained a master
volume slider in the toolbar, positioned immediately before **Export video...**, with a live "N%"
readout next to it. `WinampVolumeSlider` is a small owner-drawn `Control` (sunken dark groove,
green fill, raised beveled metal thumb) used instead of the stock `TrackBar`, which is a native
common control that can't be recolored to fit the dark theme.

`GridPlayer` and `SequentialGridPlayer` both gained a `Volume`/`SetVolume(int)` pair. The value is
applied to each tile's `MediaPlayer.Volume` right after `Play()` and re-applied on every tick -- the
same pattern the existing per-tile mute logic already used, since setting audio properties
immediately after `Play()` is unreliable until the LibVLC audio output actually exists yet.

Every toolbar button (Add videos..., Clear all, Play/Pause/Stop, Export video..., FFmpeg...) was
restyled via `WinampToolStripRenderer`, a `ToolStripProfessionalRenderer` subclass: a chunky beveled
cap (raised when idle, sunken when pressed, a lime-green glow on hover) on a brushed dark-metal
toolbar, with bold LCD-green caption text, dimming when disabled.

**Bug caught and fixed mid-revision:** the first pass set `_toolStrip.RenderMode =
ToolStripRenderMode.Custom` explicitly alongside assigning `Renderer`, which throws
`NotSupportedException` at runtime -- WinForms forbids setting `RenderMode` to `Custom` directly;
assigning `Renderer` flips `RenderMode` to `Custom` automatically. Fixed by removing the explicit
`RenderMode` assignment in both forms.

## 2. Audio tab helper text tweak

**File:** `MainWindow.xaml`

The Audio tab's helper line under the file-list buttons changed from "Select one or more files,
then Play Selected or Create Playlist" to "Select one or more files, then Play or Create Playlist"
(applied twice in this revision -- the first edit was reverted by the user outside this session and
reapplied identically on request).

## 3. LED spectrum analyzer replaces the CRT line-trace oscilloscope

**New file:** `Controls/OscilloscopeSpectrumControl.cs`. **Removed:**
`Controls/OscilloscopeLineControl.cs`. **Files:** `MainWindow.xaml`, `MainWindow.xaml.cs`

The Audio tab's oscilloscope panel cycles between three views on click; the classic CRT-style
waveform trace ("line" mode) was replaced with a classic LED-style spectrum analyzer. The cycle is
now **fire -> spectrum -> off** (was fire -> line -> off) -- `OscilloscopeMode.Line` became
`OscilloscopeMode.Spectrum`, and `OscilloscopeLine`/`ApplyOscilloscopeMode`/`ClearOscilloscopes`
were renamed to match throughout `MainWindow.xaml.cs`.

`OscilloscopeSpectrumControl` renders 20 log-spaced frequency bands (55 Hz-14 kHz), each a column of
14 discrete "LED" segments colored green/yellow/red, plus a slowly-decaying white peak-hold dot per
column. Each band's energy comes from a single-frequency Goertzel filter run directly against the
live PCM buffer -- effectively one DFT bin computed only where needed, O(bands x samples) per frame,
cheap enough to run every repaint without a full FFT since the LED look only needs a fixed, modest
band count rather than fine frequency resolution. The exact sample rate is assumed to be 44100 Hz
for the band-frequency mapping -- the real `AudioFileReader`'s rate isn't plumbed through
`PushSamples`, and a few percent of frequency error is invisible at 20-band resolution.

**Follow-up change:** the LEDs originally lit from the bottom up (green fills first, red only at
the loudest). Per request, this was reversed so each column now fills **down from the top** as that
band gets louder -- `lit: s < litSegments` became `lit: s >= SegmentsPerBand - litSegments`, and the
peak marker now tracks the lowest point each column has reached (creeping back up as it decays)
instead of the highest. The color-per-position mapping (green near the bottom, red near the top)
was deliberately left unchanged, so a quiet band now lights a small red segment at the top first --
an explicit tradeoff the user chose (reverse growth direction only, not color order).

## 4. Winamp-style sliders, a common mute button, and an EQ shortcut in MainWindow (WPF)

**File:** `MainWindow.xaml`, `MainWindow.xaml.cs`

Added an implicit `Style TargetType="Slider"` (no `x:Key`, so -- like the existing implicit `Button`
style -- it applies to every `Slider` in the window automatically) built from `Track`/`Thumb`/
`RepeatButton` templates: a slim sunken dark groove, a green gradient fill up to the thumb, and a
beveled metal thumb matching the transport buttons' brushed-metal chrome. Covers `SeekSlider`,
`VolumeSlider`, and `KaraokeSlider`.

The top menu bar (previously a bare `Menu`) was wrapped in a two-column `Grid` so a small button
group could sit to its right:

- **MuteButton** -- a Segoe MDL2 Assets speaker icon (U+E767 "Volume" unmuted / U+E74F "Mute" muted) that toggles
  `VolumeSlider.Value` between 0 and the last non-zero value. The existing
  `VolumeSlider_ValueChanged` handler already applies that to whichever engine (LibVLC video or
  NAudio audio) is behind the active tab, so mute needed no new engine-plumbing -- it just drives the
  same slider the volume/karaoke controls already do, and follows the same tab-scoped convention.
  The icon also refreshes on tab switches and on manual drags to zero.
- **EqualizerButton** -- labeled "EQ" (classic Winamp naming), placed to the left of the mute button.
  Reuses the existing `PreferencesMenuItem_Click` handler verbatim, so it opens the same
  `PreferencesWindow` the Tools menu already does (where the 7-band equalizer lives) -- no new
  logic, just a one-click shortcut.

**Bug caught and fixed mid-revision:** an in-editor string replace on `MainWindow.xaml.cs` silently
wrote empty strings in place of the two Unicode escapes for the mute icon (`"\uE74F"`/`"\uE767"` became
`""`/`""`), and a first attempt to fix it via a PowerShell regex replace made it worse by running
`Regex.Escape` on the *replacement* text, corrupting the line further with literal backslashes.
Caught both times by re-reading the file after editing rather than trusting the edit result; fixed
cleanly by replacing the exact line by line number.

## 5. Click a tile to play its clip in place

**Files:** `Controls/VideoCellControl.cs`, `Forms/ClipPlayerForm.cs`, `Forms/SequencePlayerForm.cs`

`VideoCellControl` gained a `PlayRequested` event, raised on a plain left-click (double-click still
opens the file browser, unchanged). Both `ClipPlayerForm` and `SequencePlayerForm` wire it to a new
`OnCellPlayRequested`/`StopPreview` pair that plays a single tile's clip in place using its own
`MediaPlayer`, independent of `GridPlayer`/`SequentialGridPlayer`. It is a no-op while the grid-wide
player is running (`_player.IsRunning`), since swapping a tile's video surface out from under an
active `GridPlayer`/`SequentialGridPlayer` would leave it holding a dangling player reference.
Clicking the tile already previewing stops it; clicking a different one switches to that tile
instead. At most one tile previews at a time.

`StopPreview()` is called everywhere a previewing tile's clip could change out from under it or
conflict with something else claiming the grid: `RebuildGrid`, `ClearAll`, `ClearCell` (when
clearing the previewing tile), `AssignClipAsync`/`BrowseForCellAsync` (when reassigning the
previewing tile), `StartPlayback`, `ShowExportDialog`, and `OnFormClosing`.

## 6. Fixed: Grid/Audio/Vol toolbar labels reading as unreadable dark-on-dark

**File:** `Rendering/WinampToolStripRenderer.cs`

**Symptom:** after the Winamp toolbar restyle in Section 1, the "Grid", "Audio", and "Vol" labels
-- explicitly set to a light `WhiteSmoke` background with black text for contrast, per
`ClipPlayerForm`/`SequencePlayerForm`'s own code -- rendered dark instead, unreadable against the
dark toolbar around them.

**Root cause:** `WinampToolStripRenderer` (a `ToolStripProfessionalRenderer` subclass) never
overrode `OnRenderLabelBackground`. The base implementation doesn't paint a `ToolStripLabel`'s
explicit `BackColor`, so the label's own light background never actually got drawn -- the dark
toolstrip gradient painted underneath by `OnRenderToolStripBackground` showed straight through
behind the (correctly black) text, reading as a dark, low-contrast label.

**Fix:** added an `OnRenderLabelBackground` override that fills the label's bounds with its own
`BackColor` whenever that color isn't the ambient default, falling back to the base behavior
otherwise. Since both forms share this one renderer, the fix applies to both at once.

## Validated

- `dotnet build src/VideoGridStudio/VideoGridStudio.csproj -c Debug` -- run after every change in
  this revision, succeeded every time, 0 errors, only the pre-existing unrelated `WFAC010`
  high-DPI warning.
- **Toolbar/volume slider (Section 1):** rendered `ClipPlayerForm`/`SequencePlayerForm` off-screen via
  `Control.DrawToBitmap` in a standalone WinForms harness (outside the repo) that instantiates the
  real form classes against the built `VideoGridStudio.dll` -- confirmed button bevels, LCD-green
  text, disabled dimming, and the volume slider/readout sitting directly before Export video...
- **LED spectrum (Section 3):** rendered `OscilloscopeSpectrumControl` off-screen via
  `RenderTargetBitmap` in a standalone WPF harness, fed a synthetic bass/mid/treble/noise signal --
  confirmed band response, LED segment coloring, and (after the follow-up) the top-down fill
  direction and repositioned peak marker.
- **MainWindow sliders/mute/EQ (Section 4):** rendered an isolated copy of the new `Window.Resources` and
  the top button row (not the real `MainWindow`, which triggers LibVLC/NAudio initialization on
  `Loaded`) via `RenderTargetBitmap` in a standalone WPF harness -- confirmed slider groove/fill/
  thumb rendering at several values including a disabled slider, both mute-icon states, and the EQ
  button's placement and chrome.
- **Label background fix (Section 6):** re-ran the same Section 1 WinForms harness against the
  rebuilt DLL -- confirmed "Grid", "Audio", and "Vol" now render with a light background and dark
  text instead of dark-on-dark.
- **Click-to-play (Section 5):** not verified visually or interactively -- this session had no
  video file to load and no way to drive real mouse clicks against a native WinForms window, so
  this is build-verified only (`dotnet build` succeeded, 0 errors). Worth a manual pass before
  relying on it, especially: clicking a tile immediately after grid-wide playback stops, clicking
  while a different tile is already previewing, and reassigning/clearing the previewing tile's clip
  mid-preview.
- **Not run interactively this revision:** no click-through of the actual running app for most of
  the above -- this session had no way to drive a native WinForms/WPF window (the available browser
  automation tools only handle web content), so the harness renders substitute for real user
  interaction except where noted. Worth a manual pass before relying on any of this, particularly:
  dragging the new sliders (only static values were rendered), toggling mute/EQ from the real menu
  bar layout at different window widths, and the spectrum analyzer against real music rather than
  synthetic tones (its `* 6f` energy normalization is a heuristic, same as the existing fire
  visualization's RMS scaling, and hasn't been ear-and-eye-checked against an actual track). Also
  worth noting: a running `VideoGridStudio.exe` instance blocked the build partway through this
  revision (locked output file) and had to be closed with the user's OK before work could continue.
