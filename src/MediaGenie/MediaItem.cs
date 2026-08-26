using System.IO;

namespace MkvPlayer;

/// <summary>
/// One entry in the file list: a video file the user has added to the playlist.
/// </summary>
public class MediaItem
{
    public MediaItem(string fullPath)
    {
        FullPath = fullPath;
        DisplayName = Path.GetFileName(fullPath);
    }

    public string FullPath { get; }

    public string DisplayName { get; }

    public override string ToString() => DisplayName;
}
