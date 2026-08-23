# Revision 2 — Sequential playback mode + startup mode picker

Changes made to `VideoGridStudio` in this revision, in the order they were made.

## 1. First attempt: a separate playlist UI (reverted)

The first version of this revision added a second mode built around a single video view plus a
`ListBox` playlist — a different visual design from `MainForm`'s grid. After review this was not
what was wanted: the request was for **the same grid of tiles**, just with clips playing one after
another instead of all at once. That version (`Models/PlaylistClip.cs`, `Playback/SequencePlayer.cs`,
and a list-based `SequencePlayerForm.cs`) was discarded and replaced with the grid-based design
below. `Export/FfmpegRunner.cs` (renamed from `GridExporter.cs`) and `Export/ExportPresets.cs`
survived from that first pass since they're generic. A first version of the export (a plain
`concat`-filter job, `Export/SequenceFilterGraphBuilder.cs` / `SequenceCompositionPlan.cs` /
`Forms/SequenceExportDialog.cs`) was itself later replaced in step 3 below, once it turned out
export needed to look like the grid's canvas too, not just be a concatenated clip.

## 2. Added a second playback mode: Play Sequentially, same grid design

**New file:** `Playback/SequentialGridPlayer.cs`. **Rewritten:** `Forms/SequencePlayerForm.cs`

`SequencePlayerForm` is now built the same way as `MainForm` — same toolbar shape (Add videos...,
Clear all, grid-size dropdown, Audio Mix all/Muted switch, Export video..., FFmpeg...), same
`TableLayoutPanel` of `VideoCellControl` tiles, same `ClipSlot`/`GridSettings` models, same
drag-and-drop and probing logic. The only real difference is which player class drives the tiles.

`SequentialGridPlayer` mirrors `GridPlayer`'s per-tile mechanics closely (one `MediaPlayer` per
tile, the same near-the-end `TakeSnapshot`/`FreezeOn` dance so a finished tile holds its last
frame, the same off-UI-thread `Release`) but starts tiles **one at a time in grid order** instead
of all at once: `Start` begins the first cell with a clip, and each tile's `EndReached` advances to
the next one in the queue via `AdvanceQueue`, until the last tile finishes and `SequenceCompleted`
fires. A cell not reached yet stays on its placeholder exactly as it would before any playback
started; a cell that has already played freezes on its last frame exactly like a short clip does
in the grid mode. The toolbar's "Play in order" button and status bar wording reflect this ("N of
M clips finished" instead of "N playing, M holding last frame"; total length is every clip's
duration summed, not the longest one).

## 3. Sequential export: the same composited canvas as the grid, timed one tile at a time

**New file:** `Export/SequentialGridFilterGraphBuilder.cs`. **Removed:**
`Export/SequenceFilterGraphBuilder.cs`, `Export/SequenceCompositionPlan.cs`,
`Forms/SequenceExportDialog.cs`, `Models/SequenceSettings.cs`. **File:** `Forms/ExportDialog.cs`

The first pass's export just concatenated the clips (via FFmpeg's `concat` filter) into a plain
single-frame video — no grid canvas at all. That wasn't what was wanted: the request was for the
export to look exactly like the grid's own export (the full canvas, every tile in its position),
just with each clip's video appearing in its tile only during its own turn instead of all tiles
being simultaneously live.

`SequentialGridFilterGraphBuilder` is now structured like `GridFilterGraphBuilder` — same
background-PNG bottom layer (`PlaceholderRenderer.SaveCanvasPng`, unchanged), same per-cell
`scale`/`pad`/letterbox chain, same `overlay` compositing at each tile's `CellX`/`CellY`. The
difference is temporal: each clip's overlay stream is shifted forward with
`setpts=PTS+{start}/TB` (`start` = the sum of every earlier clip's duration) and held on its last
frame after its own end with `tpad=stop_duration={total-start-duration}:stop_mode=clone` — so
locally the stream spans `[0, total-start]`, and after the shift, globally spans
`[start, total]`. The `overlay` filter's `enable='gte(t,{start})'` keeps the tile showing the
placeholder canvas underneath until that global time is reached, so a tile whose turn hasn't come
yet reads exactly as it does before Play is clicked, a tile mid-turn shows live video, and a tile
already done shows its frozen last frame — matching `SequentialGridPlayer`'s on-screen behavior
tile for tile. Audio uses the same per-clip `adelay`/`apad`/`atrim` shift so each clip's own sound
lands in its own window, mixed with the others via the same silence-bed + `amix` + limiter pattern
`GridFilterGraphBuilder` already uses (harmless here since the clips never overlap in time).
`WantsAudioFrom` (mix all / one tile / silent) is reused unchanged from the grid's own audio-mode
logic.

Because the output is now a `GridCompositionPlan` (not a separate `SequenceCompositionPlan`) built
from the same `GridSettings`, `ExportDialog` itself grew a `sequential: bool` constructor
parameter instead of a second dialog class: it picks `SequentialGridFilterGraphBuilder` vs.
`GridFilterGraphBuilder` and swaps the title/summary text, but everything else (resolution/fps/
quality/tile-gap/audio controls, the placeholder-PNG generation step, progress/cancel handling) is
shared verbatim between the two modes.

## 4. Generalized the FFmpeg runner and output-preset lists for reuse

**Files:** `Export/GridExporter.cs` → renamed `Export/FfmpegRunner.cs`; new
`Export/ExportPresets.cs`; `Forms/ExportDialog.cs`

`GridExporter.RunAsync` took a `GridCompositionPlan` directly, which the sequential export doesn't
have. Renamed to `FfmpegRunner` and changed to take the arguments/filter graph/total seconds as
plain parameters instead — the process-running internals (the `-progress pipe:1` parsing loop,
cancel/kill handling) are unchanged, so both export dialogs now share one already-proven
implementation instead of a second copy.

Likewise, `ExportDialog`'s private `Resolutions`/`Qualities` arrays and inline frame-rate list
were pulled out into `ExportPresets`, so `SequenceExportDialog` doesn't carry its own
independently-maintained copy that could drift from the grid's.

## 5. Added the startup mode picker

**New file:** `Forms/LauncherForm.cs`. **Files:** `Program.cs`,
`Controls/VideoCellControl.cs`, `Forms/MainForm.cs`

`Program.Main` now runs `LauncherForm` instead of `MainForm` directly. `LauncherForm` shows two
buttons, "Play Together (Grid)" and "Play Sequentially"; clicking one hides the launcher and shows
the chosen form, wiring that form's `FormClosed` to close the (hidden) launcher — which is what
actually ends `Application.Run`'s message loop, so the app exits once the chosen mode's window
closes.

`VideoCellControl` gained a public `OpenFileDialogFilter` constant (the video-extensions filter
string used to be duplicated inline in `MainForm`) so both `MainForm` and `SequencePlayerForm`
stay in sync on which file types are offered.

## Validated

- `dotnet build VideoGridStudio.sln -c Debug` — succeeds, 0 errors, same pre-existing unrelated
  `WFAC010` high-DPI warning as before.
- Ran the built app: the launcher shows both buttons; each opens the correct window; closing that
  window exits the process.
- **Play Together**: added two generated test clips (one with audio, one silent) to the grid,
  confirmed tiles filled and probed correctly, ran **Export video...** end to end — FFmpeg produced
  a playable file. Confirms the `FfmpegRunner`/`ExportPresets` refactor didn't regress the
  existing grid path.
- **Play Sequentially (playback)**: added the same two clips to tiles 1 and 2, clicked **Play in
  order**, and confirmed on screen that tile 1 played alone while tile 2 stayed on its placeholder
  (not simultaneous), then tile 1 froze on its last frame and tile 2 started automatically, then
  froze too, with the status bar reporting "All clips finished playing in order."
- **Play Sequentially (export, canvas version)**: verified via a standalone harness that calls
  `SequentialGridFilterGraphBuilder.Build` and `FfmpegRunner.RunAsync` directly (same code path
  the app uses, avoiding flaky UI automation) against a 2x2 grid with the same two clips in tiles
  0 and 1. Inspected the generated filter graph (correct `setpts`/`tpad`/`enable` values per tile)
  and the rendered output: at t=1s tile 0 shows live video and tile 1 still shows its "Ready · 0:02"
  placeholder; at t=4s and t=4.9s tile 0 is frozen on its last frame and tile 1 is playing live —
  confirming the full canvas stays visible throughout with each clip appearing only in its own
  tile during its own turn, exactly matching the grid export's visual format. Output duration
  measured exactly 3s + 2s = 5.0 seconds via `ffprobe`, also confirmed from the *actual app* one
  export earlier in this pass (before switching to the harness) with the same 5.0s result.
- Confirmed the export dialog itself (opened from `SequencePlayerForm`) now shows the full grid
  parity UI — Frame size/rate/Quality/**Audio** (mix all / one tile / silent)/**Tile gap** — with a
  summary reading "N clip(s) play one after another, in grid order · finished video is Xs long"
  plus the grid dimensions, instead of the earlier concat version's simpler audio switch.
