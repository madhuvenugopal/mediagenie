namespace MediaGenie.Models;

/// <summary>What to do with sound when every tile is playing at once.</summary>
public enum AudioMode
{
    /// <summary>Every clip's audio is mixed together, like an unmuted meeting.</summary>
    MixAll,

    /// <summary>Only one chosen tile is audible; the rest are muted.</summary>
    SingleTile,

    /// <summary>The finished video has a silent track.</summary>
    Silent
}
