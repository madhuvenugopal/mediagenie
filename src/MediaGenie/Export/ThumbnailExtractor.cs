using System.Diagnostics;

namespace MediaGenie.Export;

/// <summary>Grabs a single frame near the start of a clip via FFmpeg, for use as a "waiting its turn" preview.</summary>
public static class ThumbnailExtractor
{
    public static async Task<bool> ExtractFirstFrameAsync(
        string ffmpegPath,
        string sourcePath,
        string outputPngPath,
        CancellationToken token = default)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = ffmpegPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        foreach (string argument in new[]
                 {
                     "-hide_banner", "-loglevel", "error", "-y",
                     "-i", sourcePath,
                     "-frames:v", "1",
                     "-vf", "scale=320:-2",
                     outputPngPath
                 })
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };

        if (!process.Start())
        {
            return false;
        }

        // Both pipes have to be drained at the same time; reading them one after the other
        // deadlocks as soon as ffmpeg fills the buffer of the one we are not reading.
        Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync(token);
        Task<string> stderrTask = process.StandardError.ReadToEndAsync(token);

        await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);
        await process.WaitForExitAsync(token).ConfigureAwait(false);

        return process.ExitCode == 0 && File.Exists(outputPngPath);
    }
}
