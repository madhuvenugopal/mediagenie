using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace MkvPlayer.Audio;

/// <summary>Thrown when no working `audio-separator` install could be found (see <see cref="AiVocalSeparator"/>).</summary>
public sealed class AudioSeparatorNotFoundException : Exception
{
    public AudioSeparatorNotFoundException(string message) : base(message)
    {
    }
}

/// <summary>
/// Shells out to the external, actively-maintained `audio-separator` Python CLI
/// (https://github.com/nomadkaraoke/python-audio-separator) for real neural vocal/instrumental
/// separation, rather than reimplementing its model's inference pipeline in-process. That
/// pipeline (chunked STFT with a specific windowing convention, a particular tensor packing
/// scheme, overlap-add across chunks) is delicate, and its correctness can't be verified without
/// actually listening to the output -- reusing an already-correct, already-tested tool is far
/// safer than hand-rolling it. This is intentionally offline-only: neural separation isn't
/// streaming/real-time friendly, so there's no live-preview equivalent of the DSP sliders here.
/// </summary>
public static class AiVocalSeparator
{
    private const string CliBootstrap = "from audio_separator.utils.cli import main; main()";

    /// <summary>
    /// Every attempt (all candidates, not just the last) gets written here on failure -- a
    /// Python traceback is often 15-20+ lines, too long to usefully surface in a one-line status
    /// label, so the label just points here instead of trying to cram it all in.
    /// </summary>
    public static readonly string LogFilePath = Path.Combine(Path.GetTempPath(), "MediaGenie", "ai-separation-log.txt");

    // pip's own "audio-separator.exe" console script frequently ends up in a Scripts folder
    // that isn't on PATH (observed firsthand on a fresh per-user Windows Store Python install),
    // so alongside trying it directly, fall back to running the same entry point as a module
    // through each Python interpreter that might have it installed, until one actually works.
    private static readonly (string FileName, string[] PrefixArgs)[] Candidates =
    {
        ("audio-separator", Array.Empty<string>()),
        ("py", new[] { "-3.13", "-c", CliBootstrap }),
        ("py", new[] { "-3.12", "-c", CliBootstrap }),
        ("py", new[] { "-3.11", "-c", CliBootstrap }),
        ("py", new[] { "-3.10", "-c", CliBootstrap }),
        ("py", new[] { "-c", CliBootstrap }),
        ("python", new[] { "-c", CliBootstrap }),
    };

    /// <summary>
    /// Runs audio-separator on <paramref name="sourceFilePath"/>, writing its output (by default,
    /// a "(Vocals)" and an "(Instrumental)" file) into <paramref name="outputDirectory"/>. If
    /// every candidate way of invoking it fails, the exception message is short (points at
    /// <see cref="LogFilePath"/>) but that file gets the full command line, exit code, and
    /// stdout/stderr for every single attempt.
    /// </summary>
    public static async Task SeparateAsync(string sourceFilePath, string outputDirectory)
    {
        var cliArgs = new[] { sourceFilePath, "--output_dir", outputDirectory, "--output_format", "MP3" };
        var log = new StringBuilder();
        log.AppendLine($"=== AI separation attempt at {DateTime.Now:yyyy-MM-dd HH:mm:ss} ===");
        log.AppendLine($"Source: {sourceFilePath}");
        log.AppendLine($"Output dir: {outputDirectory}");
        log.AppendLine();

        foreach (var (fileName, prefixArgs) in Candidates)
        {
            var args = prefixArgs.Concat(cliArgs).ToList();
            log.AppendLine($"--- Trying: {fileName} {string.Join(' ', args)} ---");

            try
            {
                var (exitCode, stdout, stderr) = await RunAsync(fileName, args);
                log.AppendLine($"Exit code: {exitCode}");
                if (!string.IsNullOrWhiteSpace(stdout)) log.AppendLine($"stdout:\n{stdout}");
                if (!string.IsNullOrWhiteSpace(stderr)) log.AppendLine($"stderr:\n{stderr}");

                if (exitCode == 0)
                {
                    log.AppendLine("SUCCESS");
                    WriteLog(log);
                    return;
                }
            }
            catch (Win32Exception ex)
            {
                // This command doesn't exist on this machine at all -- try the next candidate.
                log.AppendLine($"Not found: {ex.Message}");
            }

            log.AppendLine();
        }

        WriteLog(log);
        throw new AudioSeparatorNotFoundException(
            $"AI separation failed -- every way of running audio-separator was tried and none worked. Full details written to {LogFilePath}");
    }

    private static void WriteLog(StringBuilder log)
    {
        try
        {
            var dir = Path.GetDirectoryName(LogFilePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(LogFilePath, log.ToString());
        }
        catch
        {
            // Logging is best-effort -- never let a logging failure mask the real one.
        }
    }

    private static async Task<(int ExitCode, string Stdout, string Stderr)> RunAsync(string fileName, List<string> args)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var arg in args)
        {
            startInfo.ArgumentList.Add(arg);
        }

        using var process = Process.Start(startInfo)
            ?? throw new AudioSeparatorNotFoundException($"Could not start {fileName}.");

        // Drain both streams concurrently with waiting for exit -- audio-separator can print a
        // fair amount while it works (model download progress, per-chunk status), and an unread
        // pipe can fill its OS buffer and deadlock the child process.
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        return (process.ExitCode, await stdoutTask, await stderrTask);
    }
}
