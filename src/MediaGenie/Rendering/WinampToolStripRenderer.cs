using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace VideoGridStudio.Rendering;

/// <summary>
/// Toolbar look shared by ClipPlayerForm and SequencePlayerForm: a brushed-metal dark strip
/// with chunky beveled buttons -- raised when idle, sunken when pressed, a lime glow on
/// hover -- and small bold LCD-green captions, styled after the classic Winamp transport bar.
/// </summary>
public sealed class WinampToolStripRenderer : ToolStripProfessionalRenderer
{
    private static readonly Color FaceColor = Color.FromArgb(40, 40, 46);
    private static readonly Color FaceHover = Color.FromArgb(54, 60, 52);
    private static readonly Color FacePressed = Color.FromArgb(22, 28, 22);
    private static readonly Color BevelLight = Color.FromArgb(96, 100, 92);
    private static readonly Color BevelDark = Color.FromArgb(6, 6, 8);
    private static readonly Color GlowGreen = Color.FromArgb(140, 255, 128);
    private static readonly Color LcdGreen = Color.FromArgb(150, 255, 140);
    private static readonly Color LcdGreenDim = Color.FromArgb(90, 100, 92);
    private static readonly Font ButtonFont = new("Tahoma", 8.25f, FontStyle.Bold);

    public WinampToolStripRenderer()
        : base(new WinampColorTable())
    {
        RoundedEdges = false;
    }

    protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
    {
        using var brush = new LinearGradientBrush(
            e.AffectedBounds,
            Color.FromArgb(32, 32, 36),
            Color.FromArgb(14, 14, 17),
            LinearGradientMode.Vertical);

        e.Graphics.FillRectangle(brush, e.AffectedBounds);
    }

    protected override void OnRenderButtonBackground(ToolStripItemRenderEventArgs e)
    {
        if (e.Item is not ToolStripButton button)
        {
            base.OnRenderButtonBackground(e);
            return;
        }

        Graphics g = e.Graphics;
        var bounds = new Rectangle(Point.Empty, button.Size);
        bounds.Width -= 1;
        bounds.Height -= 1;

        if (bounds.Width <= 0 || bounds.Height <= 0)
        {
            return;
        }

        bool pressed = button.Pressed || (button.CheckOnClick && button.Checked);
        bool hot = button.Selected;

        Color face = !button.Enabled ? FaceColor : pressed ? FacePressed : hot ? FaceHover : FaceColor;

        using (var faceBrush = new SolidBrush(face))
        {
            g.FillRectangle(faceBrush, bounds);
        }

        Color top = pressed ? BevelDark : BevelLight;
        Color bottom = pressed ? BevelLight : BevelDark;

        using (var topPen = new Pen(top))
        using (var bottomPen = new Pen(bottom))
        {
            g.DrawLine(topPen, bounds.Left, bounds.Top, bounds.Right, bounds.Top);
            g.DrawLine(topPen, bounds.Left, bounds.Top, bounds.Left, bounds.Bottom);
            g.DrawLine(bottomPen, bounds.Right, bounds.Top, bounds.Right, bounds.Bottom);
            g.DrawLine(bottomPen, bounds.Left, bounds.Bottom, bounds.Right, bounds.Bottom);
        }

        if (hot && !pressed && button.Enabled && bounds.Width > 2 && bounds.Height > 2)
        {
            using var glow = new Pen(GlowGreen);
            g.DrawRectangle(glow, bounds.Left + 1, bounds.Top + 1, bounds.Width - 2, bounds.Height - 2);
        }
    }

    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
        if (e.Item is ToolStripButton button)
        {
            e.TextColor = button.Enabled ? LcdGreen : LcdGreenDim;
            e.Item.Font = ButtonFont;
        }

        base.OnRenderItemText(e);
    }

    protected override void OnRenderLabelBackground(ToolStripItemRenderEventArgs e)
    {
        // ToolStripProfessionalRenderer's stock label background painting leaves an explicit
        // BackColor unfilled, so a light-background label (Grid/Audio/Vol, set to WhiteSmoke
        // for contrast against this otherwise-dark toolbar) shows the dark toolstrip gradient
        // bleeding through behind its text instead of its own background. Fill explicitly
        // whenever BackColor isn't the ambient default.
        if (e.Item.BackColor != Control.DefaultBackColor && e.Item.BackColor != Color.Transparent)
        {
            using var brush = new SolidBrush(e.Item.BackColor);
            e.Graphics.FillRectangle(brush, new Rectangle(Point.Empty, e.Item.Size));
            return;
        }

        base.OnRenderLabelBackground(e);
    }

    protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
    {
        Graphics g = e.Graphics;
        var bounds = new Rectangle(Point.Empty, e.Item.Size);
        int mid = bounds.Width / 2;

        using var dark = new Pen(BevelDark);
        using var light = new Pen(BevelLight);

        g.DrawLine(dark, mid, 2, mid, bounds.Height - 2);
        g.DrawLine(light, mid + 1, 2, mid + 1, bounds.Height - 2);
    }

    private sealed class WinampColorTable : ProfessionalColorTable
    {
        public override Color ToolStripGradientBegin => Color.FromArgb(32, 32, 36);
        public override Color ToolStripGradientMiddle => Color.FromArgb(22, 22, 26);
        public override Color ToolStripGradientEnd => Color.FromArgb(14, 14, 17);
        public override Color ImageMarginGradientBegin => ToolStripGradientBegin;
        public override Color ImageMarginGradientMiddle => ToolStripGradientMiddle;
        public override Color ImageMarginGradientEnd => ToolStripGradientEnd;
    }
}
