using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace MkvPlayer.Audio;

/// <summary>
/// Saves and loads playlists as plain .m3u files -- a simple, widely-compatible format for
/// "create a playlist from the files I've loaded, and play all of them in order".
/// </summary>
public static class PlaylistManager
{
    public static void Save(IEnumerable<MediaItem> items, string filePath)
    {
        using var writer = new StreamWriter(filePath, append: false);
        writer.WriteLine("#EXTM3U");
        foreach (var item in items)
        {
            writer.WriteLine($"#EXTINF:-1,{item.DisplayName}");
            writer.WriteLine(item.FullPath);
        }
    }

    /// <summary>Returns the ordered list of file paths referenced by the playlist, skipping files that no longer exist.</summary>
    public static List<string> Load(string filePath)
    {
        var paths = new List<string>();
        if (!File.Exists(filePath)) return paths;

        foreach (var rawLine in File.ReadAllLines(filePath))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            if (File.Exists(line))
            {
                paths.Add(line);
            }
        }

        return paths.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }
}
