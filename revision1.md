# Revision 1

## Fix: export failing with "Unrecognized option 'filter_complex_script'"

**Symptom**

Export always failed immediately with:

```
FFmpeg exited with code -1414549496.
Unrecognized option 'filter_complex_script'.
Error splitting the argument list: Option not found
```

**Root cause**

The app's built-in FFmpeg downloader (`FfmpegInstaller`) fetches BtbN's
`ffmpeg-master-latest-win64-gpl.zip` — a continuous build off FFmpeg's git
`master` branch, not a numbered stable release. The build downloaded at the
time of this fix (dated 2026-08-21) had dropped the `-filter_complex_script`
command-line option entirely; it no longer appears even in `ffmpeg -h full`.
`GridFilterGraphBuilder` relied on that flag to point FFmpeg at the filter
graph written to a temp file, so every export failed at argument parsing
before any frames were processed.

Confirmed by reproducing the exact error (including the same exit code)
directly against the downloaded `ffmpeg.exe`, and by checking its `-h full`
output, which lists `-filter_complex`, `-filter_complex_threads`, `-lavfi`,
etc. but no `_script` variant.

**Fix**

[`GridFilterGraphBuilder.Build`](src/VideoGridStudio/Export/GridFilterGraphBuilder.cs)
now passes the filter graph directly as the value of `-filter_complex`
(one entry in the `ProcessStartInfo.ArgumentList` array — no shell involved,
so no quoting/length concerns even for a 5x5 grid) instead of writing it to
a file and passing `-filter_complex_script <path>`. `-filter_complex` is the
base flag the removed option was shorthand for, so this works on both old
and new FFmpeg builds.

The filter script is still written to
`%TEMP%\VideoGridStudio\export\filter.txt` by
[`GridExporter.RunAsync`](src/VideoGridStudio/Export/GridExporter.cs) — it's
just no longer what FFmpeg reads; it's kept purely so a failed export leaves
the exact graph on disk to inspect.

**Files changed**

- `src/VideoGridStudio/Export/GridFilterGraphBuilder.cs` — build the filter
  graph text once, pass it inline via `-filter_complex`; dropped the now-unused
  `filterScriptPath` parameter from `Build(...)`.
- `src/VideoGridStudio/Forms/ExportDialog.cs` — updated the `Build(...)` call
  site to match the new signature.
- `src/VideoGridStudio/Export/GridExporter.cs` — updated the comment above the
  file write to reflect that it's a diagnostic dump, not FFmpeg's input.
- `src/VideoGridStudio/Export/GridCompositionPlan.cs` — updated the
  `FilterScript` doc comment (was referencing the removed flag).

**Validated**

- Reproduced the original failure against the real downloaded `ffmpeg.exe`.
- Built a realistic 2-clip filter graph (scale/pad/freeze-hold/overlay +
  audio mix) matching what the app generates, ran it with the new inline
  `-filter_complex` argument — full export succeeded, produced a valid MP4
  with correct video/audio streams and duration.
- `dotnet build VideoGridStudio.sln` — succeeds, 0 errors (same pre-existing
  `WFAC010` high-DPI warning as before, unrelated to this change).
- User confirmed export now works.

**Known follow-up (not changed in this revision)**

- The app always re-downloads BtbN's rolling `master-latest` build, so
  another master-branch regression like this one could surface again later.
  Pointing the app at a stable build instead (e.g. `winget install
  Gyan.FFmpeg`, via "FFmpeg..." → "I already have it...") avoids that.
- `src/VideoGridStudio/Forms/FfmpegLocator.cs` is a byte-for-byte duplicate
  of `src/VideoGridStudio/Export/FfmpegLocator.cs`, excluded from
  compilation via `<Compile Remove>` in the csproj — inert, but worth
  deleting.
