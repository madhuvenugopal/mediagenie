namespace MediaGenie.Export;

/// <summary>Finds ffmpeg.exe / ffprobe.exe without making the user configure anything.</summary>
public static class FfmpegLocator
{
    /// <summary>Where the built-in downloader puts its copy.</summary>
    public static string PrivateFolder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MediaGenie",
        "ffmpeg");

    /// <summary>
    /// Looks in the saved setting, the app's own download folder, next to the app, on PATH
    /// (including the machine and user PATH, which a fresh install updates without the
    /// running process ever seeing it), and in the usual winget / choco / scoop locations.
    /// </summary>
    public static string? FindFfmpeg(string? configuredPath) => Find("ffmpeg.exe", configuredPath);

    /// <summary>ffprobe normally sits right next to ffmpeg.</summary>
    public static string? FindFfprobe(string? ffmpegPath)
    {
        if (IsUsable(ffmpegPath))
        {
            string? folder = Path.GetDirectoryName(ffmpegPath!);

            if (!string.IsNullOrEmpty(folder))
            {
                string sibling = Path.Combine(folder, "ffprobe.exe");

                if (IsUsable(sibling))
                {
                    return sibling;
                }
            }
        }

        return Find("ffprobe.exe", null);
    }

    private static string? Find(string exeName, string? configuredPath)
    {
        if (IsUsable(configuredPath))
        {
            return configuredPath;
        }

        foreach (string candidate in CandidatePaths(exeName))
        {
            if (IsUsable(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private static IEnumerable<string> CandidatePaths(string exeName)
    {
        yield return Path.Combine(PrivateFolder, exeName);

        string appDir = AppContext.BaseDirectory;
        yield return Path.Combine(appDir, exeName);
        yield return Path.Combine(appDir, "ffmpeg", exeName);
        yield return Path.Combine(appDir, "ffmpeg", "bin", exeName);
        yield return Path.Combine(appDir, "tools", "ffmpeg", exeName);

        // The process PATH is a snapshot from when the app started. Reading the machine and
        // user values as well means an FFmpeg installed a minute ago is still found.
        foreach (EnvironmentVariableTarget target in new[]
                 {
                     EnvironmentVariableTarget.Process,
                     EnvironmentVariableTarget.User,
                     EnvironmentVariableTarget.Machine
                 })
        {
            string? pathVariable = null;

            try
            {
                pathVariable = Environment.GetEnvironmentVariable("PATH", target);
            }
            catch
            {
                // Reading the registry-backed values can fail in restricted environments.
            }

            if (string.IsNullOrEmpty(pathVariable))
            {
                continue;
            }

            foreach (string segment in pathVariable.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                string trimmed = segment.Trim().Trim('"');

                if (trimmed.Length == 0)
                {
                    continue;
                }

                string combined;

                try
                {
                    combined = Path.Combine(trimmed, exeName);
                }
                catch (ArgumentException)
                {
                    continue;
                }

                yield return combined;
            }
        }

        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        foreach (string root in new[]
                 {
                     @"C:\ffmpeg\bin",
                     @"C:\ffmpeg",
                     Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "ffmpeg", "bin"),
                     Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "ffmpeg", "bin"),
                     Path.Combine(localAppData, "Microsoft", "WinGet", "Links"),
                     @"C:\ProgramData\chocolatey\bin",
                     Path.Combine(userProfile, "scoop", "shims"),
                     Path.Combine(localAppData, "Programs", "ffmpeg", "bin")
                 })
        {
            yield return Path.Combine(root, exeName);
        }

        // winget installs FFmpeg as a portable package, so the real exe lives a couple of
        // folders deep under a versioned name such as "ffmpeg-7.1-essentials_build".
        foreach (string found in SearchWinGetPackages(exeName))
        {
            yield return found;
        }
    }

    private static IEnumerable<string> SearchWinGetPackages(string exeName)
    {
        string packagesRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Microsoft",
            "WinGet",
            "Packages");

        string[] packageFolders;

        try
        {
            if (!Directory.Exists(packagesRoot))
            {
                yield break;
            }

            packageFolders = Directory.GetDirectories(packagesRoot, "*FFmpeg*", SearchOption.TopDirectoryOnly);
        }
        catch
        {
            yield break;
        }

        foreach (string packageFolder in packageFolders)
        {
            // Deliberately bounded: winget lays these out as
            // "<package>\<build>\bin\ffmpeg.exe", so checking the package folder and two
            // levels below it covers every case without scanning the whole profile.
            yield return Path.Combine(packageFolder, exeName);

            foreach (string buildFolder in SafeSubfolders(packageFolder))
            {
                yield return Path.Combine(buildFolder, exeName);
                yield return Path.Combine(buildFolder, "bin", exeName);
            }
        }
    }

    private static string[] SafeSubfolders(string folder)
    {
        try
        {
            return Directory.GetDirectories(folder, "*", SearchOption.TopDirectoryOnly);
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    /// <summary>
    /// Confirms a file really is FFmpeg by asking it for its version, so a mistaken pick
    /// fails here with a clear message instead of halfway through an export.
    /// </summary>
    public static bool Verify(string? path)
    {
        if (!IsUsable(path))
        {
            return false;
        }

        try
        {
            using var process = new System.Diagnostics.Process
            {
                StartInfo = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = path!,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };

            process.StartInfo.ArgumentList.Add("-version");

            if (!process.Start())
            {
                return false;
            }

            // Drain stderr in the background; reading the two pipes one after the other
            // is the classic way to deadlock a child process.
            process.ErrorDataReceived += (_, _) => { };
            process.BeginErrorReadLine();

            string output = process.StandardOutput.ReadToEnd();

            if (!process.WaitForExit(5000))
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch
                {
                    // Already gone.
                }

                return false;
            }

            return output.Contains("ffmpeg version", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static bool IsUsable(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        try
        {
            return File.Exists(path);
        }
        catch
        {
            return false;
        }
    }
}
