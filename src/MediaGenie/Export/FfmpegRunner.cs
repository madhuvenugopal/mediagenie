using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace VideoGridStudio.Export;

/// <summary>
/// Runs an FFmpeg job and turns its -progress output into a percentage. Shared by both the
/// grid export (GridFilterGraphBuilder) and the sequential export (SequenceFilterGraphBuilder)
/// -- neither cares how the arguments/filter graph were built, only how to run them.
/// </summary>
public static class FfmpegRunner
{
    public static async Task RunAsync(
        string ffmpegPath,
        IReadOnlyList<string> arguments,
        string filterGraph,
        double totalSeconds,
        string filterScriptPath,
        IProgress<ExportProgress>? progress = null,
        CancellationToken token = default)
    {
        // FFmpeg is run with the filter graph inline (see GridFilterGraphBuilder /
        // SequenceFilterGraphBuilder), not by reading this file -- it is written purely so a
        // failed export leaves the exact graph on disk to inspect.
        await File.WriteAllTextAsync(filterScriptPath, filterGraph, new UTF8Encoding(false), token)
                  .ConfigureAwait(false);

        var startInfo = new ProcessStartInfo
        {
            FileName = ffmpegPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        var errorBuffer = new StringBuilder();

        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                errorBuffer.AppendLine(e.Data);
            }
        };

        progress?.Report(new ExportProgress(0, totalSeconds, "Starting FFmpeg..."));

        if (!process.Start())
        {
            throw new InvalidOperationException("FFmpeg could not be started.");
        }

        process.BeginErrorReadLine();

        try
        {
            await ReadProgressAsync(process, totalSeconds, progress, token).ConfigureAwait(false);
            await process.WaitForExitAsync(token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            throw;
        }

        if (process.ExitCode != 0)
        {
            string details = errorBuffer.ToString().Trim();

            throw new InvalidOperationException(
                $"FFmpeg exited with code {process.ExitCode}." +
                (details.Length > 0 ? Environment.NewLine + Environment.NewLine + Trim(details) : string.Empty));
        }

        progress?.Report(new ExportProgress(totalSeconds, totalSeconds, "Finished."));
    }

    private static async Task ReadProgressAsync(
        Process process,
        double totalSeconds,
        IProgress<ExportProgress>? progress,
        CancellationToken token)
    {
        double seconds = 0;

        while (await process.StandardOutput.ReadLineAsync(token).ConfigureAwait(false) is { } line)
        {
            int split = line.IndexOf('=');

            if (split <= 0)
            {
                continue;
            }

            string key = line[..split].Trim();
            string value = line[(split + 1)..].Trim();

            // out_time_ms is actually microseconds, and it reads "N/A" until the first frame lands.
            if (key is "out_time_ms" or "out_time_us")
            {
                if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long micro))
                {
                    seconds = micro / 1_000_000.0;
                    progress?.Report(new ExportProgress(seconds, totalSeconds, "Rendering..."));
                }
            }
            else if (key == "progress" && value == "end")
            {
                progress?.Report(new ExportProgress(totalSeconds, totalSeconds, "Writing file..."));
            }
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // The process may have exited on its own between the check and the kill.
        }
    }

    private static string Trim(string text)
    {
        const int max = 1500;
        return text.Length <= max ? text : string.Concat("...", text.AsSpan(text.Length - max));
    }
}
