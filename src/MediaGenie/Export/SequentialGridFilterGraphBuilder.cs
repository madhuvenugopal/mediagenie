using System.Globalization;
using System.Text;
using MediaGenie.Models;
using MediaGenie.Rendering;

namespace MediaGenie.Export;

/// <summary>
/// Turns the grid into a single FFmpeg job where every clip plays in its own tile position,
/// one after another in grid order, instead of every tile starting together (see
/// GridFilterGraphBuilder for that). The canvas is identical -- same background PNG, same
/// tile positions -- but each clip's overlay is time-shifted so it only appears once the
/// previous clip has finished, and is held on its last frame afterwards until the whole run
/// ends, mirroring what SequentialGridPlayer shows on screen tile by tile. While a clip plays
/// it also grows/shrinks between its own tile and the centered spotlight rect via
/// <see cref="AppendZoomTransition"/>, baking in a transition that mirrors (though linearly
/// rather than eased -- see that method) the live zoom SequencePlayerForm plays back for the
/// same tile.
/// </summary>
public static class SequentialGridFilterGraphBuilder
{
    private const int AudioSampleRate = 48000;

    /// <summary>Filter graph text length above which FFmpeg's own command line (see FfmpegRunner,
    /// which passes it inline -- this build of FFmpeg has no "read the graph from a file" option
    /// to fall back on) risks exceeding Windows' ~32K command-line limit once the rest of the
    /// arguments (every clip's path, the background PNG, codec options) are added on top.</summary>
    private const int SafeFilterGraphLength = 24000;

    public static GridCompositionPlan Build(
        GridSettings settings,
        IReadOnlyList<ClipSlot> slots,
        string backgroundImagePath,
        string outputPath)
    {
        GridCompositionPlan plan = BuildInternal(settings, slots, backgroundImagePath, outputPath, includeZoom: true);

        // A big enough grid's animated zoom transitions can push the graph past the safe length
        // on their own -- falling back to the plain instant-cut zoom keeps every grid size
        // exportable instead of failing outright once there are enough clips.
        if (plan.FilterScript.Length > SafeFilterGraphLength)
        {
            plan = BuildInternal(settings, slots, backgroundImagePath, outputPath, includeZoom: false);
        }

        return plan;
    }

    private static GridCompositionPlan BuildInternal(
        GridSettings settings,
        IReadOnlyList<ClipSlot> slots,
        string backgroundImagePath,
        string outputPath,
        bool includeZoom)
    {
        List<ClipSlot> clips = slots
            .Where(s => s.HasClip && s.DurationSeconds > 0)
            .OrderBy(s => s.Index)
            .ToList();

        if (clips.Count == 0)
        {
            throw new InvalidOperationException(
                "There is nothing to export yet. Add at least one video to the grid.");
        }

        // Clips play one after another, so the running time is every clip's length added together.
        double total = clips.Sum(c => c.DurationSeconds);
        int cellWidth = settings.CellWidth;
        int cellHeight = settings.CellHeight;
        int fps = Math.Max(1, settings.FrameRate);
        int canvasWidth = settings.OutputWidth;
        int canvasHeight = settings.OutputHeight;
        float spotlight = Math.Clamp(settings.SpotlightScale, 0.1f, 1f);
        int spotlightWidth = MakeEven((int)Math.Round(canvasWidth * spotlight));
        int spotlightHeight = MakeEven((int)Math.Round(canvasHeight * spotlight));
        int spotlightX = (canvasWidth - spotlightWidth) / 2;
        int spotlightY = (canvasHeight - spotlightHeight) / 2;
        string? musicPath = string.IsNullOrWhiteSpace(settings.BackgroundMusicPath) ? null : settings.BackgroundMusicPath;
        int musicInput = clips.Count + 1;

        var filter = new StringBuilder();
        var videoParts = new List<string>();
        var audioLabels = new List<string>();

        // Layer 0: the placeholder grid, looped for the whole running time -- a tile whose
        // turn hasn't come yet simply shows through here, same as before Play was clicked.
        filter.Append("[0:v]fps=").Append(fps).Append(",format=rgba,setsar=1[base0];").Append('\n');

        double cursor = 0;

        for (int i = 0; i < clips.Count; i++)
        {
            ClipSlot clip = clips[i];
            int input = i + 1;
            int row = clip.Index / settings.Columns;
            int column = clip.Index % settings.Columns;
            int cellX = settings.CellX(column);
            int cellY = settings.CellY(row);
            double start = cursor;
            double end = start + clip.DurationSeconds;
            double holdAfterEnd = Math.Max(0, total - end);

            // Grow/shrink transition length can't exceed half the clip, so a very short clip
            // still gets a symmetric (if abbreviated) ramp instead of the two overlapping.
            double rampSeconds = Math.Min(GridSettings.ZoomAnimationMs / 1000.0, clip.DurationSeconds / 2.0);
            double holdStart = start + rampSeconds;
            double holdEnd = Math.Max(holdStart, end - rampSeconds);

            string current = $"base{i}";

            // When the grid has too many clips for the animated version to stay within FFmpeg's
            // command-line budget (see Build/SafeFilterGraphLength), zoomWindowStart/End collapse
            // to the clip's whole active span and no ramp layers are added below -- same instant
            // cut to/from spotlight size as before this feature existed.
            double zoomWindowStart = start;
            double zoomWindowEnd = end;

            if (includeZoom)
            {
                // Grow from the tile's own cell rect up to the centered spotlight rect -- mirrors
                // SequencePlayerForm's eased ZoomIn animation, baked into the render as a
                // continuous per-frame expression (see AppendZoomTransition) rather than the live
                // version's timer-driven ticks, so it stays one filter-graph layer regardless of
                // frame rate.
                current = AppendZoomTransition(
                    videoParts, current, input, i, "in", start, rampSeconds, fps,
                    cellWidth, cellHeight, cellX, cellY,
                    spotlightWidth, spotlightHeight, spotlightX, spotlightY);

                zoomWindowStart = holdStart;
                zoomWindowEnd = holdEnd;
            }

            // Hold at full spotlight size while the clip plays out its steady middle (or, with no
            // zoom transition, its entire turn).
            videoParts.Add(string.Concat(
                $"[{input}:v]",
                $"scale={spotlightWidth}:{spotlightHeight}:force_original_aspect_ratio=decrease,",
                $"pad={spotlightWidth}:{spotlightHeight}:(ow-iw)/2:(oh-ih)/2:color={Theme.LetterboxHex},",
                $"setsar=1,fps={fps},format=rgba,",
                $"setpts=PTS+{Num(start)}/TB",
                $"[vz{input}];"));

            videoParts.Add(string.Concat(
                $"[{current}][vz{input}]",
                $"overlay=x={spotlightX}:y={spotlightY}",
                $":enable='between(t,{Num(zoomWindowStart)},{Num(zoomWindowEnd)})'",
                ":eof_action=repeat:shortest=0",
                $"[hz{i}];"));
            current = $"hz{i}";

            if (includeZoom)
            {
                // Shrink back from the spotlight rect down to the tile's own cell rect, ending
                // exactly at `end` so the frozen-frame overlay below can take over without a gap.
                current = AppendZoomTransition(
                    videoParts, current, input, i, "out", holdEnd, rampSeconds, fps,
                    spotlightWidth, spotlightHeight, spotlightX, spotlightY,
                    cellWidth, cellHeight, cellX, cellY);
            }

            // Fit into the tile, keep the aspect ratio, letterbox the remainder, hold the
            // final frame once this clip ends, then shift the whole stream so it begins
            // exactly when the previous clip's held tail ends. Only shown once the shrink-back
            // ramp above ends, so the tile settles into place right as it freezes.
            videoParts.Add(string.Concat(
                $"[{input}:v]",
                $"scale={cellWidth}:{cellHeight}:force_original_aspect_ratio=decrease,",
                $"pad={cellWidth}:{cellHeight}:(ow-iw)/2:(oh-ih)/2:color={Theme.LetterboxHex},",
                $"setsar=1,fps={fps},format=rgba,",
                $"tpad=stop_duration={Num(holdAfterEnd)}:stop_mode=clone,",
                $"setpts=PTS+{Num(start)}/TB",
                $"[v{input}];"));

            // enable=... keeps the zoom layers showing through until this tile has finished
            // playing; eof_action=repeat covers a decoder that runs a hair short.
            videoParts.Add(string.Concat(
                $"[{current}][v{input}]",
                $"overlay=x={cellX}:y={cellY}",
                $":enable='gte(t,{Num(end)})'",
                ":eof_action=repeat:shortest=0",
                $"[base{i + 1}];"));

            // Background music completely replaces the clips' own audio, so there is no point
            // extracting it -- skip straight past every clip's audio branch when it's set.
            if (musicPath is null && clip.HasAudio && WantsAudioFrom(settings, clip.Index))
            {
                audioLabels.Add($"[a{input}]");

                filter.Append(string.Concat(
                    $"[{input}:a]",
                    $"aresample={AudioSampleRate},",
                    "aformat=sample_fmts=fltp:channel_layouts=stereo,",
                    $"adelay={Ms(start)}|{Ms(start)},",
                    "apad,",
                    $"atrim=duration={Num(total)},asetpts=N/SR/TB",
                    $"[a{input}];")).Append('\n');
            }

            cursor += clip.DurationSeconds;
        }

        foreach (string part in videoParts)
        {
            filter.Append(part).Append('\n');
        }

        filter.Append($"[base{clips.Count}]format=yuv420p[vout];").Append('\n');

        if (musicPath is not null)
        {
            // -stream_loop -1 on the input (see arguments below) repeats the file for as long
            // as ffmpeg keeps reading; atrim here is what actually cuts that down to size, so
            // music shorter than the export loops seamlessly and music longer than it just
            // gets cut off -- either way every clip's own track is muted, not mixed in.
            filter.Append(string.Concat(
                $"[{musicInput}:a]",
                $"aresample={AudioSampleRate},",
                "aformat=sample_fmts=fltp:channel_layouts=stereo,",
                $"atrim=duration={Num(total)},asetpts=N/SR/TB",
                "[aout];")).Append('\n');
        }
        else
        {
            // A silent bed guarantees a full length audio track even when no clip has sound.
            filter.Append(string.Concat(
                $"anullsrc=channel_layout=stereo:sample_rate={AudioSampleRate},",
                $"atrim=duration={Num(total)},asetpts=N/SR/TB[silence];")).Append('\n');

            if (audioLabels.Count == 0)
            {
                filter.Append("[silence]anull[aout]");
            }
            else
            {
                // The clips never overlap in time, so this "mix" is really just stitching each
                // clip's own audio into its own window; the limiter is a safety net in case two
                // durations were estimated slightly long.
                filter.Append("[silence]").Append(string.Concat(audioLabels))
                      .Append($"amix=inputs={audioLabels.Count + 1}:normalize=0:dropout_transition=0")
                      .Append(audioLabels.Count > 1 ? ",alimiter=limit=0.95:attack=5:release=50" : string.Empty)
                      .Append("[aout]");
            }
        }

        string filterGraph = filter.ToString();

        var arguments = new List<string>
        {
            "-hide_banner",
            "-nostdin",
            "-y",
            "-loglevel", "error",
            "-progress", "pipe:1",
            "-nostats",
            "-loop", "1",
            "-framerate", fps.ToString(CultureInfo.InvariantCulture),
            "-t", Num(total),
            "-i", backgroundImagePath
        };

        foreach (ClipSlot clip in clips)
        {
            arguments.Add("-i");
            arguments.Add(clip.FilePath!);
        }

        if (musicPath is not null)
        {
            // -stream_loop -1 must precede this specific -i to apply to it (it's a per-input
            // option), not the clip inputs already added above.
            arguments.Add("-stream_loop");
            arguments.Add("-1");
            arguments.Add("-i");
            arguments.Add(musicPath);
        }

        arguments.AddRange(new[]
        {
            "-filter_complex", filterGraph,
            "-map", "[vout]",
            "-map", "[aout]",
            "-c:v", "libx264",
            "-preset", settings.Preset,
            "-crf", settings.Crf.ToString(CultureInfo.InvariantCulture),
            "-pix_fmt", "yuv420p",
            "-r", fps.ToString(CultureInfo.InvariantCulture),
            "-c:a", "aac",
            "-b:a", "192k",
            "-ar", AudioSampleRate.ToString(CultureInfo.InvariantCulture),
            "-movflags", "+faststart",
            "-t", Num(total),
            outputPath
        });

        return new GridCompositionPlan(
            arguments,
            filterGraph,
            total,
            clips.Select(c => c.Index).ToList());
    }

    /// <summary>
    /// Appends one overlay layer onto <paramref name="current"/> that grows/shrinks continuously
    /// between the "from" and "to" rect (cell to spotlight when growing, spotlight to cell when
    /// shrinking) over <paramref name="rampSeconds"/>, as a single per-frame FFmpeg expression
    /// (scale's and overlay's <c>eval=frame</c>) rather than SequencePlayerForm's timer ticks, so
    /// the filter graph stays one layer regardless of frame rate or ramp length. Unlike the
    /// padded hold/tail layers, this one has no letterbox pad -- <c>pad</c>'s own width/height
    /// expressions can't see <c>t</c>, so a time-varying pad box isn't possible here -- but the
    /// transition is brief enough (a fraction of a second) that the source's own edges showing
    /// through in place of letterbox bars isn't noticeable. Returns the label of the resulting
    /// composite.
    /// </summary>
    private static string AppendZoomTransition(
        List<string> videoParts, string current, int input, int clipIndex, string direction,
        double windowStart, double rampSeconds, int fps,
        int fromWidth, int fromHeight, int fromX, int fromY,
        int toWidth, int toHeight, int toX, int toY)
    {
        if (rampSeconds <= 0)
        {
            return current;
        }

        double windowEnd = windowStart + rampSeconds;

        // enable='between(...)' on the overlay below confines *compositing* to [windowStart,
        // windowEnd], but scale has no such gate of its own -- it keeps evaluating this
        // expression for the clip's entire duration, so outside this layer's own window raw
        // swings far past [0,1] (very negative before windowStart, far above 1 after windowEnd).
        // Asking scale for the resulting wildly out-of-range target sizes destabilizes it enough
        // to corrupt frames even inside the intended window, so raw is clamped here despite the
        // overlay's enable already restricting where the result is actually seen.
        //
        // Progress is linear rather than eased like SequencePlayerForm's live version: an eased
        // curve needs its raw sub-expression written out three times (the branch test plus both
        // branches), and with four of these expressions needed per direction
        // (width/height/centerX/centerY), that multiplies into a filter graph too large for
        // FFmpeg's command line on a full grid -- this build has no file-based alternative to
        // fall back on (see FfmpegRunner). A linear grow/shrink is still a continuous animation,
        // just without the slow-fast-slow feel.
        string raw = $"min(1,max(0,(t-{Num(windowStart)})/{Num(rampSeconds)}))";

        string widthExpr = LerpExpr(fromWidth, toWidth, raw);
        string heightExpr = LerpExpr(fromHeight, toHeight, raw);
        // Centered on the interpolated target rect's own center rather than its corner, so the
        // actual (possibly letterbox-less, aspect-preserved) scaled frame size doesn't matter --
        // overlay_w/overlay_h always land it in the middle of where the rect currently is.
        string centerXExpr = LerpExpr(fromX + (fromWidth / 2.0), toX + (toWidth / 2.0), raw);
        string centerYExpr = LerpExpr(fromY + (fromHeight / 2.0), toY + (toHeight / 2.0), raw);

        string videoLabel = $"vz{direction}{input}";
        string nextLabel = $"z{direction}{clipIndex}";

        videoParts.Add(string.Concat(
            $"[{input}:v]",
            $"scale=w='trunc(({widthExpr})/2)*2':h='trunc(({heightExpr})/2)*2':eval=frame:force_original_aspect_ratio=decrease,",
            $"setsar=1,fps={fps},format=rgba,",
            $"setpts=PTS+{Num(windowStart)}/TB",
            $"[{videoLabel}];"));

        videoParts.Add(string.Concat(
            $"[{current}][{videoLabel}]",
            $"overlay=x='({centerXExpr})-overlay_w/2':y='({centerYExpr})-overlay_h/2':eval=frame",
            $":enable='between(t,{Num(windowStart)},{Num(windowEnd)})'",
            ":eof_action=repeat:shortest=0",
            $"[{nextLabel}];"));

        return nextLabel;
    }

    /// <summary>FFmpeg-expression linear interpolation between two constants, driven by a 0..1 progress sub-expression.</summary>
    private static string LerpExpr(double from, double to, string progressExpr) =>
        $"({Num(from)}+({Num(to)}-{Num(from)})*{progressExpr})";

    private static bool WantsAudioFrom(GridSettings settings, int cellIndex) => settings.AudioMode switch
    {
        AudioMode.MixAll => true,
        AudioMode.SingleTile => settings.AudioTileIndex == cellIndex,
        _ => false
    };

    /// <summary>FFmpeg wants dots, never commas, whatever the machine's locale says.</summary>
    private static string Num(double value) =>
        value.ToString("0.####", CultureInfo.InvariantCulture);

    /// <summary>adelay wants a whole number of milliseconds per channel.</summary>
    private static string Ms(double seconds) =>
        ((long)Math.Round(seconds * 1000.0)).ToString(CultureInfo.InvariantCulture);

    /// <summary>H.264-friendly filter chains stay happiest with even dimensions throughout.</summary>
    private static int MakeEven(int value) => value % 2 == 0 ? value : value + 1;
}
