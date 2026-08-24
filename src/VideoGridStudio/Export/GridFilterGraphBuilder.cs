using System.Globalization;
using System.Text;
using VideoGridStudio.Models;
using VideoGridStudio.Rendering;

namespace VideoGridStudio.Export;

/// <summary>
/// Turns the grid into a single FFmpeg job.
///
/// Every clip starts at the same moment, exactly like a meeting where all the cameras
/// are already on. The picture is built in layers:
///   * bottom layer - one PNG of the whole grid, with a placeholder tile drawn in every
///     position, so empty tiles and the gaps between tiles look right;
///   * one layer per clip - scaled into its own tile and held on its last frame once it
///     runs out, so short clips stay on screen while longer ones keep playing.
///
/// The finished video is therefore as long as the longest clip.
/// </summary>
public static class GridFilterGraphBuilder
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

        // Everything starts together, so the running time is the longest clip.
        double total = clips.Max(c => c.DurationSeconds);
        int cellWidth = settings.CellWidth;
        int cellHeight = settings.CellHeight;
        int fps = Math.Max(1, settings.FrameRate);
        string? musicPath = string.IsNullOrWhiteSpace(settings.BackgroundMusicPath) ? null : settings.BackgroundMusicPath;
        int musicInput = clips.Count + 1;

        var filter = new StringBuilder();
        var videoParts = new List<string>();
        var audioLabels = new List<string>();

        // Layer 0: the placeholder grid, looped for the whole running time.
        filter.Append("[0:v]fps=").Append(fps).Append(",format=rgba,setsar=1[base0];").Append('\n');

        for (int i = 0; i < clips.Count; i++)
        {
            ClipSlot clip = clips[i];
            int input = i + 1;
            int row = clip.Index / settings.Columns;
            int column = clip.Index % settings.Columns;
            double tail = Math.Max(0, total - clip.DurationSeconds);

            // Fit into the tile, keep the aspect ratio, letterbox the remainder, then hold
            // the final frame for as long as the longer clips keep running.
            videoParts.Add(string.Concat(
                $"[{input}:v]",
                $"scale={cellWidth}:{cellHeight}:force_original_aspect_ratio=decrease,",
                $"pad={cellWidth}:{cellHeight}:(ow-iw)/2:(oh-ih)/2:color={Theme.LetterboxHex},",
                $"setsar=1,fps={fps},format=rgba,",
                $"tpad=stop_duration={Num(tail)}:stop_mode=clone",
                $"[v{input}];"));

            // eof_action=repeat keeps the last frame on screen if a decoder runs slightly short.
            videoParts.Add(string.Concat(
                $"[base{i}][v{input}]",
                $"overlay=x={settings.CellX(column)}:y={settings.CellY(row)}",
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
                    "apad,",
                    $"atrim=duration={Num(total)},asetpts=N/SR/TB",
                    $"[a{input}];")).Append('\n');
            }
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
                // normalize=0 keeps each clip at its own level; the limiter catches the peaks
                // that appear once several tiles are talking at once.
                filter.Append("[silence]").Append(string.Concat(audioLabels))
                      .Append($"amix=inputs={audioLabels.Count + 1}:normalize=0:dropout_transition=0")
                      .Append(audioLabels.Count > 1 ? ",alimiter=limit=0.95:attack=5:release=50" : string.Empty)
                      .Append("[aout]");
            }
        }

        // Passed to FFmpeg as one -filter_complex argument rather than written to a script
        // file: -filter_complex_script was dropped by recent FFmpeg builds ("Unrecognized
        // option 'filter_complex_script'"), while plain -filter_complex has been there since
        // filter_complex itself was introduced and isn't going anywhere. ArgumentList hands
        // this to Windows as a single argv entry (no shell involved), so there is no quoting
        // concern, and even a 5x5 grid's graph is a few KB -- nowhere near the ~32K command
        // line limit.
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

    private static bool WantsAudioFrom(GridSettings settings, int cellIndex) => settings.AudioMode switch
    {
        AudioMode.MixAll => true,
        AudioMode.SingleTile => settings.AudioTileIndex == cellIndex,
        _ => false
    };

    /// <summary>FFmpeg wants dots, never commas, whatever the machine's locale says.</summary>
    private static string Num(double value) =>
        value.ToString("0.####", CultureInfo.InvariantCulture);
}
