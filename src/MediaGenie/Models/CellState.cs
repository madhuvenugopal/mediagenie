namespace VideoGridStudio.Models;

/// <summary>Lifecycle of a single tile in the grid.</summary>
public enum CellState
{
    /// <summary>No clip assigned yet.</summary>
    Empty,

    /// <summary>A clip is assigned and ready to start.</summary>
    Queued,

    /// <summary>This clip is running.</summary>
    Playing,

    /// <summary>Clip has played to the end; the tile holds its last frame.</summary>
    Finished
}
