using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using MediaGenie.Models;

namespace MediaGenie.Rendering;

/// <summary>
/// Draws the "nobody is on camera yet" tiles. The very same drawing code produces the
/// on-screen placeholder and the background image handed to FFmpeg, so the exported
/// video looks exactly like the preview.
/// </summary>
public static class PlaceholderRenderer
{
    /// <summary>Renders one tile at the requested pixel size.</summary>
    public static Bitmap RenderTile(ClipSlot slot, int width, int height)
    {
        width = Math.Max(8, width);
        height = Math.Max(8, height);

        var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bitmap);
        g.Clear(Theme.Canvas);
        DrawTile(g, slot, new Rectangle(0, 0, width, height), null);
        return bitmap;
    }

    /// <summary>
    /// Renders the whole grid as one image: canvas background plus every placeholder tile
    /// in its final position. FFmpeg uses this as the bottom layer of the composition.
    /// A tile with a thumbnail (see SequentialGridPlayer/SequencePlayerForm's poster frames)
    /// shows that instead of the abstract avatar art, keyed by grid index.
    /// </summary>
    public static Bitmap RenderCanvas(GridSettings settings, IReadOnlyList<ClipSlot> slots, IReadOnlyDictionary<int, Image>? thumbnails = null)
    {
        var bitmap = new Bitmap(settings.OutputWidth, settings.OutputHeight, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bitmap);
        g.Clear(Theme.Canvas);

        for (int row = 0; row < settings.Rows; row++)
        {
            for (int col = 0; col < settings.Columns; col++)
            {
                int index = (row * settings.Columns) + col;
                ClipSlot slot = index < slots.Count ? slots[index] : new ClipSlot(index);
                var rect = new Rectangle(
                    settings.CellX(col),
                    settings.CellY(row),
                    settings.CellWidth,
                    settings.CellHeight);
                Image? thumbnail = thumbnails is not null && thumbnails.TryGetValue(index, out Image? found) ? found : null;
                DrawTile(g, slot, rect, thumbnail);
            }
        }

        return bitmap;
    }

    /// <summary>Saves the grid background as a PNG for FFmpeg to consume.</summary>
    public static string SaveCanvasPng(GridSettings settings, IReadOnlyList<ClipSlot> slots, string path, IReadOnlyDictionary<int, Image>? thumbnails = null)
    {
        using Bitmap bitmap = RenderCanvas(settings, slots, thumbnails);
        bitmap.Save(path, ImageFormat.Png);
        return path;
    }

    private static void DrawTile(Graphics g, ClipSlot slot, Rectangle rect, Image? thumbnail)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;

        int radius = Math.Max(6, Math.Min(rect.Width, rect.Height) / 16);
        using GraphicsPath path = RoundedRect(rect, radius);

        using (var fill = new SolidBrush(Theme.Tile))
        {
            g.FillPath(fill, path);
        }

        Color border = slot.State switch
        {
            CellState.Playing => Theme.TileBorderActive,
            CellState.Finished => Theme.TileBorderDone,
            _ => Theme.TileBorder
        };

        using (var pen = new Pen(border, slot.State == CellState.Playing ? 3f : 1.5f))
        {
            g.DrawPath(pen, path);
        }

        if (thumbnail is not null)
        {
            Region previousClip = g.Clip;
            g.SetClip(path, CombineMode.Intersect);
            DrawLetterboxed(g, thumbnail, rect);
            g.Clip = previousClip;
            return;
        }

        if (!slot.HasClip)
        {
            DrawEmptyCameraTile(g, slot, rect);
            return;
        }

        // Circular "avatar" with the slot number, like a participant tile with the camera off.
        int avatarSize = Math.Max(24, Math.Min(rect.Width, rect.Height) / 3);
        var avatarRect = new Rectangle(
            rect.X + ((rect.Width - avatarSize) / 2),
            rect.Y + ((rect.Height - avatarSize) / 2) - (rect.Height / 12),
            avatarSize,
            avatarSize);

        using (var avatarBrush = new SolidBrush(Theme.Avatar))
        {
            g.FillEllipse(avatarBrush, avatarRect);
        }

        float numberSize = Math.Max(9f, avatarSize * 0.42f);
        using (var numberFont = new Font("Segoe UI", numberSize, FontStyle.Bold, GraphicsUnit.Pixel))
        using (var numberBrush = new SolidBrush(Theme.AvatarText))
        using (var centre = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
        {
            g.DrawString((slot.Index + 1).ToString(), numberFont, numberBrush, avatarRect, centre);
        }

        // Caption block underneath the avatar: file name then state.
        float captionSize = Math.Max(8f, Math.Min(rect.Width, rect.Height) * 0.062f);
        float subSize = Math.Max(7f, captionSize * 0.82f);

        var captionRect = new RectangleF(
            rect.X + (rect.Width * 0.06f),
            avatarRect.Bottom + (rect.Height * 0.05f),
            rect.Width * 0.88f,
            captionSize * 1.5f);

        using (var captionFont = new Font("Segoe UI", captionSize, FontStyle.Regular, GraphicsUnit.Pixel))
        using (var captionBrush = new SolidBrush(slot.HasClip ? Theme.PrimaryText : Theme.MutedText))
        using (var format = new StringFormat
        {
            Alignment = StringAlignment.Center,
            LineAlignment = StringAlignment.Near,
            Trimming = StringTrimming.EllipsisCharacter,
            FormatFlags = StringFormatFlags.NoWrap
        })
        {
            g.DrawString(slot.DisplayName, captionFont, captionBrush, captionRect, format);
        }

        string subtitle = slot.State switch
        {
            CellState.Playing => "Now playing",
            CellState.Finished => "Finished",
            CellState.Queued => $"Ready  ·  {slot.DurationText}",
            _ => "Drop a video here"
        };

        var subRect = new RectangleF(
            captionRect.X,
            captionRect.Bottom + (rect.Height * 0.012f),
            captionRect.Width,
            subSize * 1.6f);

        using (var subFont = new Font("Segoe UI", subSize, FontStyle.Regular, GraphicsUnit.Pixel))
        using (var subBrush = new SolidBrush(slot.State == CellState.Playing ? Theme.Accent : Theme.MutedText))
        using (var format = new StringFormat
        {
            Alignment = StringAlignment.Center,
            LineAlignment = StringAlignment.Near,
            Trimming = StringTrimming.EllipsisCharacter,
            FormatFlags = StringFormatFlags.NoWrap
        })
        {
            g.DrawString(subtitle, subFont, subBrush, subRect, format);
        }
    }

    /// <summary>
    /// Drawn for a slot that has no clip assigned yet: a video-camera-off glyph (a camcorder
    /// body + viewfinder flap with a diagonal "no signal" slash through it), like a
    /// video-call tile whose camera is off, instead of a filename caption reading "Empty slot".
    /// </summary>
    private static void DrawEmptyCameraTile(Graphics g, ClipSlot slot, Rectangle rect)
    {
        int iconSize = Math.Max(28, Math.Min(rect.Width, rect.Height) / 3);
        var iconRect = new RectangleF(
            rect.X + ((rect.Width - iconSize) / 2f),
            rect.Y + ((rect.Height - iconSize) / 2f) - (rect.Height / 14f),
            iconSize,
            iconSize);

        float bodyW = iconRect.Width * 0.60f;
        float bodyH = iconRect.Height * 0.56f;
        var bodyRect = new RectangleF(
            iconRect.X,
            iconRect.Y + ((iconRect.Height - bodyH) / 2f),
            bodyW,
            bodyH);

        using (var bodyPath = RoundedRectF(bodyRect, Math.Min(bodyW, bodyH) * 0.22f))
        using (var bodyBrush = new SolidBrush(Theme.Avatar))
        {
            g.FillPath(bodyBrush, bodyPath);
        }

        // Camcorder viewfinder flap, jutting right from the body.
        var flap = new[]
        {
            new PointF(bodyRect.Right - (bodyRect.Width * 0.06f), bodyRect.Y + (bodyRect.Height * 0.20f)),
            new PointF(iconRect.Right, bodyRect.Y + (bodyRect.Height * 0.06f)),
            new PointF(iconRect.Right, bodyRect.Bottom - (bodyRect.Height * 0.06f)),
            new PointF(bodyRect.Right - (bodyRect.Width * 0.06f), bodyRect.Bottom - (bodyRect.Height * 0.20f)),
        };
        using (var flapBrush = new SolidBrush(Theme.Avatar))
        {
            g.FillPolygon(flapBrush, flap);
        }

        // Diagonal "off" slash through the whole glyph.
        using (var slashPen = new Pen(Theme.MutedText, Math.Max(2.5f, iconRect.Width * 0.11f))
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round
        })
        {
            g.DrawLine(
                slashPen,
                iconRect.X - (iconRect.Width * 0.04f), iconRect.Y - (iconRect.Height * 0.04f),
                iconRect.Right + (iconRect.Width * 0.04f), iconRect.Bottom + (iconRect.Height * 0.04f));
        }

        float subSize = Math.Max(8f, Math.Min(rect.Width, rect.Height) * 0.054f);
        var subRect = new RectangleF(
            rect.X + (rect.Width * 0.06f),
            iconRect.Bottom + (rect.Height * 0.06f),
            rect.Width * 0.88f,
            subSize * 1.6f);

        using (var subFont = new Font("Segoe UI", subSize, FontStyle.Regular, GraphicsUnit.Pixel))
        using (var subBrush = new SolidBrush(Theme.MutedText))
        using (var format = new StringFormat
        {
            Alignment = StringAlignment.Center,
            LineAlignment = StringAlignment.Near,
            Trimming = StringTrimming.EllipsisCharacter,
            FormatFlags = StringFormatFlags.NoWrap
        })
        {
            g.DrawString("Drop a video here", subFont, subBrush, subRect, format);
        }

        // Small, unobtrusive slot index in the corner -- enough to tell tiles apart while empty.
        float badgeSize = Math.Max(7f, subSize * 0.9f);
        using (var badgeFont = new Font("Segoe UI", badgeSize, FontStyle.Regular, GraphicsUnit.Pixel))
        using (var badgeBrush = new SolidBrush(Theme.MutedText))
        {
            g.DrawString((slot.Index + 1).ToString(), badgeFont, badgeBrush, rect.X + (rect.Width * 0.05f), rect.Y + (rect.Height * 0.035f));
        }
    }

    /// <summary>Fits an image inside a rect, aspect ratio preserved, centred.</summary>
    private static void DrawLetterboxed(Graphics g, Image image, Rectangle rect)
    {
        double scale = Math.Min((double)rect.Width / image.Width, (double)rect.Height / image.Height);
        int w = Math.Max(1, (int)Math.Round(image.Width * scale));
        int h = Math.Max(1, (int)Math.Round(image.Height * scale));
        var target = new Rectangle(rect.X + ((rect.Width - w) / 2), rect.Y + ((rect.Height - h) / 2), w, h);
        g.DrawImage(image, target);
    }

    private static GraphicsPath RoundedRect(Rectangle rect, int radius)
    {
        var path = new GraphicsPath();
        int d = radius * 2;

        if (d <= 0 || d > rect.Width || d > rect.Height)
        {
            path.AddRectangle(rect);
            return path;
        }

        path.AddArc(rect.X, rect.Y, d, d, 180, 90);
        path.AddArc(rect.Right - d, rect.Y, d, d, 270, 90);
        path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
        path.AddArc(rect.X, rect.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    private static GraphicsPath RoundedRectF(RectangleF rect, float radius)
    {
        var path = new GraphicsPath();
        float d = radius * 2;

        if (d <= 0 || d > rect.Width || d > rect.Height)
        {
            path.AddRectangle(rect);
            return path;
        }

        path.AddArc(rect.X, rect.Y, d, d, 180, 90);
        path.AddArc(rect.Right - d, rect.Y, d, d, 270, 90);
        path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
        path.AddArc(rect.X, rect.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}
