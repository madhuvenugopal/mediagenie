using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace VideoGridStudio.Export;

/// <summary>Result of inspecting a media file with ffprobe.</summary>
public sealed record MediaInfo(double DurationSeconds, bool HasAudio, bool HasVideo);

/// <summary>Thin ffprobe wrapper: we only need duration and whether there is audio.</summary>
public static class MediaProbe
{
    public static async Task<MediaInfo> ProbeAsync(string ffprobePath, string file, CancellationToken token = default)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = ffprobePath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8
        };

        foreach (string argument in new[]
                 {
                     "-v", "error",
                     "-show_entries", "format=duration",
                     "-show_entries", "stream=codec_type",
                     "-of", "json",
                     file
                 })
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };

        if (!process.Start())
        {
            throw new InvalidOperationException("ffprobe could not be started.");
        }

        // Both pipes have to be drained at the same time; reading them one after the
        // other deadlocks as soon as ffprobe fills the buffer of the one we are not reading.
        Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync(token);
        Task<string> stderrTask = process.StandardError.ReadToEndAsync(token);

        await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);
        await process.WaitForExitAsync(token).ConfigureAwait(false);

        string stdout = await stdoutTask.ConfigureAwait(false);
        string stderr = await stderrTask.ConfigureAwait(false);

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"ffprobe could not read \"{Path.GetFileName(file)}\".{Environment.NewLine}{stderr.Trim()}");
        }

        return Parse(stdout);
    }

    /// <summary>Exposed for tests: turns ffprobe's JSON into a <see cref="MediaInfo"/>.</summary>
    public static MediaInfo Parse(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;

        double duration = 0;

        if (root.TryGetProperty("format", out JsonElement format) &&
            format.TryGetProperty("duration", out JsonElement durationElement) &&
            durationElement.ValueKind == JsonValueKind.String &&
            double.TryParse(durationElement.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed))
        {
            duration = parsed;
        }

        bool hasAudio = false;
        bool hasVideo = false;

        if (root.TryGetProperty("streams", out JsonElement streams) && streams.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement stream in streams.EnumerateArray())
            {
                if (!stream.TryGetProperty("codec_type", out JsonElement typeElement))
                {
                    continue;
                }

                string? type = typeElement.GetString();

                if (string.Equals(type, "audio", StringComparison.OrdinalIgnoreCase))
                {
                    hasAudio = true;
                }
                else if (string.Equals(type, "video", StringComparison.OrdinalIgnoreCase))
                {
                    hasVideo = true;
                }
            }
        }

        return new MediaInfo(duration, hasAudio, hasVideo);
    }
}
