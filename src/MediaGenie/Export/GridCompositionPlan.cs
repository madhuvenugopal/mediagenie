namespace MediaGenie.Export;

/// <summary>Everything needed to run one export: the FFmpeg arguments and the filter graph.</summary>
public sealed class GridCompositionPlan
{
    public GridCompositionPlan(
        IReadOnlyList<string> arguments,
        string filterScript,
        double totalSeconds,
        IReadOnlyList<int> orderedCellIndexes)
    {
        Arguments = arguments;
        FilterScript = filterScript;
        TotalSeconds = totalSeconds;
        OrderedCellIndexes = orderedCellIndexes;
    }

    /// <summary>Command line arguments for ffmpeg, in order.</summary>
    public IReadOnlyList<string> Arguments { get; }

    /// <summary>The filter graph: also embedded inline in <see cref="Arguments"/>, and separately written to disk for troubleshooting.</summary>
    public string FilterScript { get; }

    /// <summary>Length of the finished video: the clip durations added together.</summary>
    public double TotalSeconds { get; }

    /// <summary>Grid positions that carry a clip, in playback order.</summary>
    public IReadOnlyList<int> OrderedCellIndexes { get; }
}
