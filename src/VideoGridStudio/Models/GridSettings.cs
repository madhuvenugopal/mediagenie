namespace VideoGridStudio.Models;

/// <summary>Everything that controls how the grid looks and how it is exported.</summary>
public sealed class GridSettings
{
    public int Rows { get; set; } = 4;

    public int Columns { get; set; } = 4;

    /// <summary>Total width of the exported video in pixels.</summary>
    public int OutputWidth { get; set; } = 1920;

    /// <summary>Total height of the exported video in pixels.</summary>
    public int OutputHeight { get; set; } = 1080;

    public int FrameRate { get; set; } = 30;

    /// <summary>Gap between tiles in the exported video, in output pixels.</summary>
    public int Gutter { get; set; } = 8;

    /// <summary>x264 quality, lower is better. 18-23 is the useful range.</summary>
    public int Crf { get; set; } = 20;

    public string Preset { get; set; } = "medium";

    public AudioMode AudioMode { get; set; } = AudioMode.MixAll;

    /// <summary>
    /// Fraction of the grid/canvas the actively-playing tile occupies while centered and
    /// spotlighted (0.75 = 75%, so the other tiles stay visible behind it). Shared by
    /// SequencePlayerForm (live zoom) and SequentialGridFilterGraphBuilder (export zoom) so
    /// the two stay in sync.
    /// </summary>
    public float SpotlightScale { get; set; } = 0.75f;

    /// <summary>Grid position whose audio is used when <see cref="AudioMode.SingleTile"/> is chosen.</summary>
    public int AudioTileIndex { get; set; }

    /// <summary>
    /// Optional music file laid under the whole export instead of the clips' own audio.
    /// When set, this completely replaces whatever <see cref="AudioMode"/> would otherwise
    /// select -- every clip's original track is muted, not mixed in alongside it. Looped if
    /// shorter than the export, trimmed if longer.
    /// </summary>
    public string? BackgroundMusicPath { get; set; }

    public int CellCount => Rows * Columns;

    /// <summary>Width of one tile, after the outer margin and gutters are removed.</summary>
    public int CellWidth
    {
        get
        {
            int available = OutputWidth - (Gutter * (Columns + 1));
            return MakeEven(Math.Max(2, available / Columns));
        }
    }

    public int CellHeight
    {
        get
        {
            int available = OutputHeight - (Gutter * (Rows + 1));
            return MakeEven(Math.Max(2, available / Rows));
        }
    }

    /// <summary>Left edge of the tile in the given column, in output pixels.</summary>
    public int CellX(int column) => Gutter + (column * (CellWidth + Gutter));

    /// <summary>Top edge of the tile in the given row, in output pixels.</summary>
    public int CellY(int row) => Gutter + (row * (CellHeight + Gutter));

    public GridSettings Clone() => (GridSettings)MemberwiseClone();

    /// <summary>H.264 needs even dimensions with yuv420p.</summary>
    private static int MakeEven(int value) => value % 2 == 0 ? value : value - 1;
}
