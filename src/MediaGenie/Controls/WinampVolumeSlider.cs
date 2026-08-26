using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace MediaGenie.Controls;

/// <summary>
/// A small owner-drawn horizontal volume slider styled after Winamp's transport-bar volume
/// bar: a sunken dark groove, a green fill up to the thumb, and a raised metal thumb. Used
/// instead of the stock WinForms TrackBar, which is a native common control that cannot be
/// recolored to match the rest of the dark, beveled Winamp-style toolbar.
/// </summary>
public sealed class WinampVolumeSlider : Control
{
    private const int ThumbWidth = 10;
    private const int Minimum = 0;
    private const int Maximum = 100;

    private int _value = 100;
    private bool _dragging;

    public WinampVolumeSlider()
    {
        SetStyle(
            ControlStyles.AllPaintingInWmPaint |
            ControlStyles.UserPaint |
            ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.ResizeRedraw,
            true);

        Size = new Size(90, 20);
        MinimumSize = new Size(40, 16);
        Cursor = Cursors.Hand;
    }

    public event EventHandler? ValueChanged;

    public int Value
    {
        get => _value;
        set
        {
            int clamped = Math.Clamp(value, Minimum, Maximum);

            if (clamped == _value)
            {
                return;
            }

            _value = clamped;
            Invalidate();
            ValueChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);

        if (e.Button == MouseButtons.Left)
        {
            _dragging = true;
            SetValueFromX(e.X);
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);

        if (_dragging)
        {
            SetValueFromX(e.X);
        }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        _dragging = false;
    }

    private void SetValueFromX(int x)
    {
        int usable = Math.Max(1, Width - ThumbWidth);
        float fraction = Math.Clamp((x - ThumbWidth / 2f) / usable, 0f, 1f);
        Value = (int)Math.Round(fraction * (Maximum - Minimum)) + Minimum;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.SmoothingMode = SmoothingMode.None;

        Rectangle bounds = ClientRectangle;
        const int grooveHeight = 6;
        var groove = new Rectangle(0, (bounds.Height - grooveHeight) / 2, Math.Max(0, bounds.Width - 1), grooveHeight);

        using (var back = new SolidBrush(Color.FromArgb(10, 10, 12)))
        {
            g.FillRectangle(back, groove);
        }

        using (var dark = new Pen(Color.FromArgb(4, 4, 5)))
        using (var light = new Pen(Color.FromArgb(70, 70, 76)))
        {
            g.DrawLine(dark, groove.Left, groove.Top, groove.Right, groove.Top);
            g.DrawLine(dark, groove.Left, groove.Top, groove.Left, groove.Bottom);
            g.DrawLine(light, groove.Right, groove.Top, groove.Right, groove.Bottom);
            g.DrawLine(light, groove.Left, groove.Bottom, groove.Right, groove.Bottom);
        }

        float fraction = (float)(_value - Minimum) / (Maximum - Minimum);
        int thumbCenter = ThumbWidth / 2 + (int)Math.Round(fraction * Math.Max(0, bounds.Width - ThumbWidth));

        if (thumbCenter > groove.Left + 1)
        {
            var fill = new Rectangle(groove.Left + 1, groove.Top + 1, Math.Max(0, thumbCenter - groove.Left - 1), Math.Max(0, groove.Height - 2));

            using var fillBrush = new LinearGradientBrush(
                groove, Color.FromArgb(40, 200, 60), Color.FromArgb(150, 255, 140), LinearGradientMode.Horizontal);
            g.FillRectangle(fillBrush, fill);
        }

        var thumb = new Rectangle(thumbCenter - ThumbWidth / 2, 1, ThumbWidth, Math.Max(1, bounds.Height - 2));

        using (var face = new LinearGradientBrush(
            new Rectangle(thumb.X, thumb.Y, Math.Max(1, thumb.Width), Math.Max(1, thumb.Height)),
            Color.FromArgb(120, 124, 116),
            Color.FromArgb(58, 60, 56),
            LinearGradientMode.Vertical))
        {
            g.FillRectangle(face, thumb);
        }

        using (var light = new Pen(Color.FromArgb(180, 184, 176)))
        using (var dark = new Pen(Color.FromArgb(20, 20, 22)))
        {
            g.DrawLine(light, thumb.Left, thumb.Top, thumb.Right - 1, thumb.Top);
            g.DrawLine(light, thumb.Left, thumb.Top, thumb.Left, thumb.Bottom - 1);
            g.DrawLine(dark, thumb.Right - 1, thumb.Top, thumb.Right - 1, thumb.Bottom - 1);
            g.DrawLine(dark, thumb.Left, thumb.Bottom - 1, thumb.Right - 1, thumb.Bottom - 1);
        }
    }
}
