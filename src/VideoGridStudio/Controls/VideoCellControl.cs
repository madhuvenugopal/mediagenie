using System.ComponentModel;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using LibVLCSharp.Shared;
using LibVLCSharp.WinForms;
using VideoGridStudio.Models;
using VideoGridStudio.Rendering;

namespace VideoGridStudio.Controls;

/// <summary>
/// One tile of the grid. It stacks two surfaces on top of each other: a painted
/// placeholder (also used to hold the frozen last frame) and a LibVLC video surface.
/// Only one of the two is visible at a time.
/// </summary>
public sealed class VideoCellControl : UserControl
{
    private static readonly string[] VideoExtensions =
    {
        ".mp4", ".mov", ".mkv", ".avi", ".wmv", ".m4v", ".webm", ".mpg", ".mpeg", ".ts", ".flv", ".3gp"
    };

    /// <summary>Shared OpenFileDialog/SaveFileDialog filter for the extensions above.</summary>
    public const string OpenFileDialogFilter =
        "Video files|*.mp4;*.mov;*.mkv;*.avi;*.wmv;*.m4v;*.webm;*.mpg;*.mpeg;*.ts;*.flv;*.3gp|All files|*.*";

    private readonly PlaceholderSurface _surface;
    private readonly VideoView _videoView;

    private Image? _frozenFrame;
    private Image? _thumbnail;

    public VideoCellControl(ClipSlot slot)
    {
        Slot = slot;

        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
        BackColor = Theme.Canvas;
        Margin = new Padding(4);
        AllowDrop = true;

        _videoView = new VideoView
        {
            Dock = DockStyle.Fill,
            BackColor = Color.Black,
            AllowDrop = true,
            Visible = false
        };

        _surface = new PlaceholderSurface(this)
        {
            Dock = DockStyle.Fill,
            AllowDrop = true
        };

        Controls.Add(_videoView);
        Controls.Add(_surface);
        _surface.BringToFront();

        var menu = new ContextMenuStrip();
        menu.Items.Add("Choose video...", null, (_, _) => BrowseRequested?.Invoke(this, EventArgs.Empty));
        menu.Items.Add("Clear this slot", null, (_, _) => ClearRequested?.Invoke(this, EventArgs.Empty));
        ContextMenuStrip = menu;
        _surface.ContextMenuStrip = menu;

        HookDragAndDrop(this);
        HookDragAndDrop(_surface);
        HookDragAndDrop(_videoView);

        _surface.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left)
            {
                PlayRequested?.Invoke(this, EventArgs.Empty);
            }
        };

        _surface.MouseDoubleClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left)
            {
                BrowseRequested?.Invoke(this, EventArgs.Empty);
            }
        };
    }

    /// <summary>Raised when the user drops a video file on this tile.</summary>
    public event EventHandler<string>? ClipDropped;

    /// <summary>Raised when the user left-clicks this tile, asking to play its clip in place.</summary>
    public event EventHandler? PlayRequested;

    /// <summary>Raised when the user asks to pick a file for this tile.</summary>
    public event EventHandler? BrowseRequested;

    /// <summary>Raised when the user asks to empty this tile.</summary>
    public event EventHandler? ClearRequested;

    public ClipSlot Slot { get; }

    /// <summary>The LibVLC surface this tile renders video into.</summary>
    public VideoView VideoSurface => _videoView;

    /// <summary>Attaches a player and switches the tile over to live video.</summary>
    public void ShowVideo(MediaPlayer player)
    {
        _videoView.MediaPlayer = player;
        _videoView.Visible = true;
        _videoView.BringToFront();
        _surface.Visible = false;
    }

    /// <summary>Detaches the player, leaving the painted surface in charge again.</summary>
    public void DetachPlayer()
    {
        _videoView.MediaPlayer = null;
        _videoView.Visible = false;
        _surface.Visible = true;
        _surface.BringToFront();
    }

    /// <summary>Parks the tile's final video frame on the painted surface.</summary>
    public void FreezeOn(Image? lastFrame)
    {
        Image? previous = _frozenFrame;
        _frozenFrame = lastFrame;
        previous?.Dispose();

        // The frozen last frame takes visual priority; the poster is no longer needed once
        // there is a real frame from actual playback to show instead.
        Image? previousThumbnail = _thumbnail;
        _thumbnail = null;
        previousThumbnail?.Dispose();

        DetachPlayer();
        _surface.Invalidate();
    }

    /// <summary>
    /// Shows a representative frame while the tile waits its turn (see SequencePlayerForm),
    /// without touching playback state -- unlike FreezeOn, this survives Play/Stop.
    /// </summary>
    public void SetThumbnail(Image? thumbnail)
    {
        Image? previous = _thumbnail;
        _thumbnail = thumbnail;
        previous?.Dispose();

        if (_frozenFrame is null)
        {
            _surface.Invalidate();
        }
    }

    /// <summary>Drops the frozen last frame from a previous run, e.g. when a fresh run starts. The poster thumbnail, if any, is left alone.</summary>
    public void ClearPlaybackFrame()
    {
        Image? previous = _frozenFrame;
        _frozenFrame = null;
        previous?.Dispose();
        DetachPlayer();
        _surface.Invalidate();
    }

    /// <summary>Drops any frozen frame and poster thumbnail, returning to the placeholder artwork.</summary>
    public void ResetSurface()
    {
        Image? previous = _frozenFrame;
        _frozenFrame = null;
        previous?.Dispose();

        Image? previousThumbnail = _thumbnail;
        _thumbnail = null;
        previousThumbnail?.Dispose();

        DetachPlayer();
        _surface.Invalidate();
    }

    /// <summary>Repaints the placeholder, e.g. after the slot's state changed.</summary>
    public void RefreshSurface() => _surface.Invalidate();

    public static bool LooksLikeVideo(string path)
    {
        string ext = Path.GetExtension(path);
        return VideoExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _videoView.MediaPlayer = null;
            _frozenFrame?.Dispose();
            _frozenFrame = null;
            _thumbnail?.Dispose();
            _thumbnail = null;
        }

        base.Dispose(disposing);
    }

    private void HookDragAndDrop(Control control)
    {
        control.AllowDrop = true;
        control.DragEnter += OnDragEnter;
        control.DragDrop += OnDragDrop;
        control.DragLeave += (_, _) => _surface.Highlighted = false;
    }

    private void OnDragEnter(object? sender, DragEventArgs e)
    {
        if (TryGetDroppedVideo(e, out _))
        {
            e.Effect = DragDropEffects.Copy;
            _surface.Highlighted = true;
        }
        else
        {
            e.Effect = DragDropEffects.None;
        }
    }

    private void OnDragDrop(object? sender, DragEventArgs e)
    {
        _surface.Highlighted = false;

        if (TryGetDroppedVideo(e, out string? path) && path is not null)
        {
            ClipDropped?.Invoke(this, path);
        }
    }

    private static bool TryGetDroppedVideo(DragEventArgs e, out string? path)
    {
        path = null;

        if (e.Data?.GetData(DataFormats.FileDrop) is not string[] files || files.Length == 0)
        {
            return false;
        }

        path = files.FirstOrDefault(LooksLikeVideo);
        return path is not null;
    }

    /// <summary>Painted surface: shows the placeholder art, or a frozen frame once one exists.</summary>
    private sealed class PlaceholderSurface : Control
    {
        private readonly VideoCellControl _owner;
        private bool _highlighted;

        public PlaceholderSurface(VideoCellControl owner)
        {
            _owner = owner;
            SetStyle(
                ControlStyles.AllPaintingInWmPaint |
                ControlStyles.OptimizedDoubleBuffer |
                ControlStyles.ResizeRedraw |
                ControlStyles.UserPaint,
                true);
            BackColor = Theme.Canvas;
            Cursor = Cursors.Hand;
        }

        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public bool Highlighted
        {
            get => _highlighted;
            set
            {
                if (_highlighted == value)
                {
                    return;
                }

                _highlighted = value;
                Invalidate();
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(Theme.Canvas);

            if (Width < 4 || Height < 4)
            {
                return;
            }

            if (_owner._frozenFrame is not null)
            {
                DrawLetterboxedFrame(g, _owner._frozenFrame, Theme.TileBorderDone);
            }
            else if (_owner._thumbnail is not null)
            {
                DrawLetterboxedFrame(g, _owner._thumbnail, Theme.TileBorder);
            }
            else
            {
                using Bitmap tile = PlaceholderRenderer.RenderTile(_owner.Slot, Width, Height);
                g.DrawImageUnscaled(tile, 0, 0);
            }

            if (_highlighted)
            {
                using var pen = new Pen(Theme.Accent, 3f) { DashStyle = DashStyle.Dash };
                g.DrawRectangle(pen, 2, 2, Width - 5, Height - 5);
            }
        }

        /// <summary>Letterboxes a still frame (frozen last frame or poster thumbnail) so it keeps the clip's aspect ratio.</summary>
        private void DrawLetterboxedFrame(Graphics g, Image frame, Color borderColor)
        {
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;

            double scale = Math.Min((double)Width / frame.Width, (double)Height / frame.Height);
            int w = Math.Max(1, (int)Math.Round(frame.Width * scale));
            int h = Math.Max(1, (int)Math.Round(frame.Height * scale));
            var target = new Rectangle((Width - w) / 2, (Height - h) / 2, w, h);

            g.DrawImage(frame, target);

            using var pen = new Pen(borderColor, 1.5f);
            g.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
        }
    }
}
