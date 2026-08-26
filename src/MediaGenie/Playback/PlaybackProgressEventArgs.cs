namespace MediaGenie.Playback;

public sealed class PlaybackProgressEventArgs : EventArgs
{
    public PlaybackProgressEventArgs(double elapsedSeconds, double totalSeconds, int playingCount, int finishedCount)
    {
        ElapsedSeconds = elapsedSeconds;
        TotalSeconds = totalSeconds;
        PlayingCount = playingCount;
        FinishedCount = finishedCount;
    }

    public double ElapsedSeconds { get; }

    public double TotalSeconds { get; }

    /// <summary>How many tiles are still running.</summary>
    public int PlayingCount { get; }

    /// <summary>How many tiles have reached the end and are holding their last frame.</summary>
    public int FinishedCount { get; }

    public double Fraction => TotalSeconds > 0 ? Math.Clamp(ElapsedSeconds / TotalSeconds, 0, 1) : 0;
}
