using System.Drawing;

namespace VideoGridStudio.Rendering;

/// <summary>Single place for the colours used by both the on-screen grid and the exported frame.</summary>
public static class Theme
{
    public static readonly Color Canvas = Color.FromArgb(16, 16, 20);
    public static readonly Color Tile = Color.FromArgb(27, 27, 33);
    public static readonly Color TileBorder = Color.FromArgb(45, 45, 55);
    public static readonly Color TileBorderActive = Color.FromArgb(96, 152, 232);
    public static readonly Color TileBorderDone = Color.FromArgb(70, 130, 100);
    public static readonly Color Avatar = Color.FromArgb(52, 54, 66);
    public static readonly Color AvatarText = Color.FromArgb(225, 228, 236);
    public static readonly Color PrimaryText = Color.FromArgb(228, 230, 236);
    public static readonly Color MutedText = Color.FromArgb(140, 143, 154);
    public static readonly Color Accent = Color.FromArgb(96, 152, 232);
    public static readonly Color Panel = Color.FromArgb(24, 24, 29);

    /// <summary>Letterbox colour used when a clip's aspect ratio does not match the tile.</summary>
    public const string LetterboxHex = "0x101014";
}
