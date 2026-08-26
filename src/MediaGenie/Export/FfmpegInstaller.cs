using System.IO.Compression;
using System.Net.Http;

namespace MediaGenie.Export;

public sealed class FfmpegInstallProgress
{
    public FfmpegInstallProgress(string stage, long bytesReceived, long? totalBytes)
    {
        Stage = stage;
        BytesReceived = bytesReceived;
        TotalBytes = totalBytes;
    }

    public string Stage { get; }

    public long BytesReceived { get; }

    public long? TotalBytes { get; }

    public double? Fraction =>
        TotalBytes is > 0 ? Math.Clamp((double)BytesReceived / TotalBytes.Value, 0, 1) : null;
}

/// <summary>
/// Fetches a ready-made Windows FFmpeg build so the user does not have to install
/// anything by hand. Only ffmpeg.exe and ffprobe.exe are kept, in a per-user folder
/// that needs no administrator rights.
/// </summary>
public static class FfmpegInstaller
{
    /// <summary>Official BtbN builds; "latest" is a stable, permanent download link.</summary>
    private const string DownloadUrl =
        "https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/ffmpeg-master-latest-win64-gpl.zip";

    private static readonly string[] WantedFiles = { "ffmpeg.exe", "ffprobe.exe" };

    public static string InstallFolder => FfmpegLocator.PrivateFolder;

    /// <summary>Downloads and unpacks FFmpeg, returning the full path to ffmpeg.exe.</summary>
    public static async Task<string> InstallAsync(
        IProgress<FfmpegInstallProgress>? progress = null,
        CancellationToken token = default)
    {
        Directory.CreateDirectory(InstallFolder);

        string workFolder = Path.Combine(Path.GetTempPath(), "MediaGenie");
        Directory.CreateDirectory(workFolder);
        string zipPath = Path.Combine(workFolder, "ffmpeg-download.zip");

        try
        {
            await DownloadAsync(zipPath, progress, token).ConfigureAwait(false);

            progress?.Report(new FfmpegInstallProgress("Unpacking...", 0, null));
            string ffmpegPath = Extract(zipPath);

            progress?.Report(new FfmpegInstallProgress("Ready.", 0, null));
            return ffmpegPath;
        }
        finally
        {
            TryDelete(zipPath);
        }
    }

    private static async Task DownloadAsync(
        string zipPath,
        IProgress<FfmpegInstallProgress>? progress,
        CancellationToken token)
    {
        using var handler = new HttpClientHandler { AllowAutoRedirect = true };
        using var http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("MediaGenie/1.0");

        using HttpResponseMessage response = await http
            .GetAsync(DownloadUrl, HttpCompletionOption.ResponseHeadersRead, token)
            .ConfigureAwait(false);

        response.EnsureSuccessStatusCode();

        long? total = response.Content.Headers.ContentLength;

        await using Stream source = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        await using var destination = new FileStream(
            zipPath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 128 * 1024,
            useAsync: true);

        byte[] buffer = new byte[128 * 1024];
        long received = 0;

        // -2 rather than -1, so that a server which sends no Content-Length (percent stays
        // at -1) still reports progress on the first chunk instead of never reporting.
        int lastReported = -2;

        while (true)
        {
            int read = await source.ReadAsync(buffer, token).ConfigureAwait(false);

            if (read == 0)
            {
                break;
            }

            await destination.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
            received += read;

            // Report at most once per percent so the UI is not flooded.
            int percent = total is > 0 ? (int)(received * 100 / total.Value) : -1;

            if (percent != lastReported)
            {
                lastReported = percent;
                progress?.Report(new FfmpegInstallProgress("Downloading FFmpeg...", received, total));
            }
        }

        progress?.Report(new FfmpegInstallProgress("Downloading FFmpeg...", received, total));
    }

    /// <summary>Pulls just the two executables out of the archive, wherever they sit inside it.</summary>
    private static string Extract(string zipPath)
    {
        using ZipArchive archive = ZipFile.OpenRead(zipPath);

        foreach (string wanted in WantedFiles)
        {
            ZipArchiveEntry? entry = archive.Entries.FirstOrDefault(e =>
                string.Equals(e.Name, wanted, StringComparison.OrdinalIgnoreCase));

            if (entry is null)
            {
                continue;
            }

            string destination = Path.Combine(InstallFolder, wanted);
            entry.ExtractToFile(destination, overwrite: true);
        }

        string ffmpegPath = Path.Combine(InstallFolder, "ffmpeg.exe");

        if (!File.Exists(ffmpegPath))
        {
            throw new InvalidOperationException(
                "The download completed but no ffmpeg.exe was found inside it. " +
                "Please install FFmpeg manually and point the app at it.");
        }

        return ffmpegPath;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // A leftover download in the temp folder is harmless.
        }
    }
}
