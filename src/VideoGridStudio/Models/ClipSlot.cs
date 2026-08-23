using System.Globalization;

namespace VideoGridStudio.Models;

/// <summary>One position in the grid, plus the clip (if any) assigned to it.</summary>
public sealed class ClipSlot
{
    public ClipSlot(int index)
    {
        Index = index;
    }

    /// <summary>Zero based position in the grid, in reading order (left to right, top to bottom).</summary>
    public int Index { get; }

    public string? FilePath { get; private set; }

    /// <summary>Duration in seconds, discovered with ffprobe. Zero until probed.</summary>
    public double DurationSeconds { get; set; }

    /// <summary>True when ffprobe reported at least one audio stream.</summary>
    public bool HasAudio { get; set; }

    public CellState State { get; set; } = CellState.Empty;

    public bool HasClip => !string.IsNullOrWhiteSpace(FilePath);

    public string DisplayName =>
        HasClip ? Path.GetFileName(FilePath!) : "Empty slot";

    public string DurationText =>
        DurationSeconds > 0
            ? TimeSpan.FromSeconds(DurationSeconds)
                      .ToString(DurationSeconds >= 3600 ? @"h\:mm\:ss" : @"m\:ss", CultureInfo.InvariantCulture)
            : "--:--";

    public void Assign(string path)
    {
        FilePath = path;
        DurationSeconds = 0;
        HasAudio = false;
        State = CellState.Queued;
    }

    public void Clear()
    {
        FilePath = null;
        DurationSeconds = 0;
        HasAudio = false;
        State = CellState.Empty;
    }

    /// <summary>Resets playback state without dropping the clip.</summary>
    public void Rewind()
    {
        if (HasClip)
        {
            State = CellState.Queued;
        }
    }
}
