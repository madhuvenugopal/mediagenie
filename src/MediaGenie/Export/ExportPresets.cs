namespace MediaGenie.Export;

/// <summary>Output choices shared by both the grid and sequential export dialogs.</summary>
public static class ExportPresets
{
    public static readonly (string Label, int Width, int Height)[] Resolutions =
    {
        ("1280 x 720 (HD)", 1280, 720),
        ("1920 x 1080 (Full HD)", 1920, 1080),
        ("2560 x 1440 (2K)", 2560, 1440),
        ("3840 x 2160 (4K)", 3840, 2160)
    };

    public static readonly (string Label, int Crf, string Preset)[] Qualities =
    {
        ("High quality (larger file)", 18, "slow"),
        ("Balanced", 20, "medium"),
        ("Smaller file (faster)", 24, "veryfast")
    };

    public static readonly int[] FrameRates = { 24, 25, 30, 50, 60 };
}
