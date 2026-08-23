using System.Globalization;
using System.Text;
using VideoGridStudio.Models;
using VideoGridStudio.Rendering;

namespace VideoGridStudio.Export;

/// <summary>
/// Turns the grid into a single FFmpeg job where every clip plays in its own tile position,
/// one after another in grid order, instead of every tile starting together (see
/// GridFilterGraphBuilder for that). The canvas is identical -- same background PNG, same
/// tile positions -- but each clip's overlay is time-shifted so it only appears once the
/// previous clip has finished, and is held on its last frame afterwards until the whole run
/// ends, mirroring what SequentialGridPlayer shows on screen tile by tile.
/// </summary>
public static class SequentialGridFilterGraphBuilder
{
    private const int AudioSampleRate = 48000;

    public static GridCompositionPlan Build(
        GridSettings settings,
        IReadOnlyList<ClipSlot> slots,
        string backgroundImagePath,
        string outputPath)
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
        float zoom = Math.Max(1f, settings.ActiveTileZoom);
        int zoomWidth = MakeEven((int)Math.Round(cellWidth * zoom));
        int zoomHeight = MakeEven((int)Math.Round(cellHeight * zoom));

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
            double start = cursor;
            double end = start + clip.DurationSeconds;
            double holdAfterEnd = Math.Max(0, total - end);
            int zoomX = settings.CellX(column) - (zoomWidth - cellWidth) / 2;
            int zoomY = settings.CellY(row) - (zoomHeight - cellHeight) / 2;

            // While this clip is actually playing, it's shown zoomed in (bigger than its own
            // cell, overlapping neighbors) instead of at normal tile size -- mirrors the zoom
            // SequencePlayerForm applies live for the same tile while it plays.
            videoParts.Add(string.Concat(
                $"[{input}:v]",
                $"scale={zoomWidth}:{zoomHeight}:force_original_aspect_ratio=decrease,",
                $"pad={zoomWidth}:{zoomHeight}:(ow-iw)/2:(oh-ih)/2:color={Theme.LetterboxHex},",
                $"setsar=1,fps={fps},format=rgba,",
                $"setpts=PTS+{Num(start)}/TB",
                $"[vz{input}];"));

            videoParts.Add(string.Concat(
                $"[base{i}][vz{input}]",
                $"overlay=x={zoomX}:y={zoomY}",
                $":enable='between(t,{Num(start)},{Num(end)})'",
                ":eof_action=repeat:shortest=0",
                $"[basez{i}];"));

            // Fit into the tile, keep the aspect ratio, letterbox the remainder, hold the
            // final frame once this clip ends, then shift the whole stream so it begins
            // exactly when the previous clip's held tail ends. Only shown once the zoomed-in
            // playback window above ends, so the tile shrinks back to normal size right as it
            // settles on its frozen last frame.
            videoParts.Add(string.Concat(
                $"[{input}:v]",
                $"scale={cellWidth}:{cellHeight}:force_original_aspect_ratio=decrease,",
                $"pad={cellWidth}:{cellHeight}:(ow-iw)/2:(oh-ih)/2:color={Theme.LetterboxHex},",
                $"setsar=1,fps={fps},format=rgba,",
                $"tpad=stop_duration={Num(holdAfterEnd)}:stop_mode=clone,",
                $"setpts=PTS+{Num(start)}/TB",
                $"[v{input}];"));

            // enable=... keeps the zoomed-in layer showing through until this tile has
            // finished playing; eof_action=repeat covers a decoder that runs a hair short.
            videoParts.Add(string.Concat(
                $"[basez{i}][v{input}]",
                $"overlay=x={settings.CellX(column)}:y={settings.CellY(row)}",
                $":enable='gte(t,{Num(end)})'",
                ":eof_action=repeat:shortest=0",
                $"[base{i + 1}];"));

            if (clip.HasAudio && WantsAudioFrom(settings, clip.Index))
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
