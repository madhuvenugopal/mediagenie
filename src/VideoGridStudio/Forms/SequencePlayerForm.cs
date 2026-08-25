using System.Drawing;
using System.Globalization;
using System.Windows.Forms;
using LibVLCSharp.Shared;
using VideoGridStudio.Controls;
using VideoGridStudio.Export;
using VideoGridStudio.Models;
using VideoGridStudio.Playback;
using VideoGridStudio.Rendering;

namespace VideoGridStudio.Forms;

/// <summary>
/// The same Meet-style grid of tiles as ClipPlayerForm, but clips play one after another in
/// grid order instead of all starting together -- see SequentialGridPlayer.
/// </summary>
public sealed class SequencePlayerForm : Form
{
    private static readonly (int Rows, int Columns)[] GridChoices =
    {
        (2, 2), (3, 3), (4, 4), (5, 5), (2, 3), (3, 4)
    };

    private readonly LibVLC _libVlc;
    private readonly AppSettings _appSettings;
    private readonly GridSettings _settings = new() { Rows = 2, Columns = 3 };
    private readonly List<ClipSlot> _slots = new();
    private readonly List<VideoCellControl> _cells = new();
    private readonly SequentialGridPlayer _player;

    private readonly TableLayoutPanel _grid = new();
    private readonly Panel _gridHost = new();
    private readonly Dictionary<VideoCellControl, (int Column, int Row)> _zoomedCells = new();
    private readonly ToolStrip _toolStrip = new();
    private readonly StatusStrip _statusStrip = new();
    private readonly ToolStripStatusLabel _statusLabel = new();
    private readonly ToolStripProgressBar _progressBar = new();
    private readonly ToolStripComboBox _gridSizeCombo = new();
    private readonly ToolStripComboBox _audioCombo = new();
    private readonly ToolStripButton _playButton = new();
    private readonly ToolStripButton _pauseButton = new();
    private readonly ToolStripButton _stopButton = new();
    private readonly ToolStripButton _exportButton = new();
    private readonly WinampVolumeSlider _volumeSlider = new();
    private readonly ToolStripLabel _volumeValueLabel = new();

    // A single click on an idle tile plays just that clip in place, independent of the grid-wide
    // SequentialGridPlayer -- see OnCellPlayRequested/StopPreview. At most one tile previews at a time.
    private VideoCellControl? _previewCell;
    private MediaPlayer? _previewPlayer;

    private readonly string _thumbnailDirectory = Path.Combine(Path.GetTempPath(), "VideoGridStudio", "thumbnails");

    private string? _ffmpegPath;
    private string? _ffprobePath;

    public SequencePlayerForm(LibVLC libVlc)
    {
        _libVlc = libVlc;
        _appSettings = AppSettings.Load();
        _ffmpegPath = FfmpegLocator.FindFfmpeg(_appSettings.FfmpegPath);
        _ffprobePath = FfmpegLocator.FindFfprobe(_ffmpegPath);
        Directory.CreateDirectory(_thumbnailDirectory);

        Text = "Video Grid Studio - Play in order";
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(960, 640);
        ClientSize = new Size(1280, 800);
        BackColor = Theme.Panel;
        AllowDrop = true;
        KeyPreview = true;

        BuildToolStrip();
        BuildGridHost();
        BuildStatusStrip();

        _player = new SequentialGridPlayer(_libVlc, this);
        _player.ClipStarted += OnClipStarted;
        _player.ClipFinished += OnClipFinished;
        _player.ProgressChanged += OnPlaybackProgress;
        _player.SequenceCompleted += OnSequenceCompleted;
        _player.Failed += OnPlaybackFailed;

        RebuildGrid(_settings.Rows, _settings.Columns);
        UpdateStatus();

        DragEnter += OnFormDragEnter;
        DragDrop += OnFormDragDrop;
        KeyDown += OnKeyDown;
    }

    private void BuildToolStrip()
    {
        _toolStrip.Dock = DockStyle.Top;
        _toolStrip.GripStyle = ToolStripGripStyle.Hidden;
        _toolStrip.Padding = new Padding(8, 6, 8, 6);
        _toolStrip.ImageScalingSize = new Size(20, 20);
        _toolStrip.Renderer = new WinampToolStripRenderer();

        var addButton = new ToolStripButton("Add videos...")
        {
            DisplayStyle = ToolStripItemDisplayStyle.Text,
            ToolTipText = "Fill the next free tiles with the videos you pick"
        };
        addButton.Click += (_, _) => AddVideosAsync();

        var clearButton = new ToolStripButton("Clear all")
        {
            DisplayStyle = ToolStripItemDisplayStyle.Text
        };
        clearButton.Click += (_, _) => ClearAll();

        _gridSizeCombo.DropDownStyle = ComboBoxStyle.DropDownList;
        _gridSizeCombo.Width = 90;

        foreach ((int rows, int columns) in GridChoices)
        {
            _gridSizeCombo.Items.Add($"{columns} x {rows}");
        }

        _gridSizeCombo.SelectedIndex = Array.FindIndex(GridChoices, c => c.Rows == 2 && c.Columns == 3);
        _gridSizeCombo.SelectedIndexChanged += OnGridSizeChanged;

        _playButton.Text = "Play in order";
        _playButton.DisplayStyle = ToolStripItemDisplayStyle.Text;
        _playButton.ToolTipText = "Play each filled tile in turn, in grid order";
        _playButton.Click += (_, _) => StartPlayback();

        _pauseButton.Text = "Pause";
        _pauseButton.DisplayStyle = ToolStripItemDisplayStyle.Text;
        _pauseButton.Enabled = false;
        _pauseButton.Click += (_, _) => TogglePause();

        _stopButton.Text = "Stop";
        _stopButton.DisplayStyle = ToolStripItemDisplayStyle.Text;
        _stopButton.Enabled = false;
        _stopButton.Click += (_, _) => StopPlayback();

        _exportButton.Text = "Export video...";
        _exportButton.DisplayStyle = ToolStripItemDisplayStyle.Text;
        _exportButton.ToolTipText = "Render every clip, one after another, to a single video file";
        _exportButton.Click += (_, _) => ShowExportDialog();

        _audioCombo.DropDownStyle = ComboBoxStyle.DropDownList;
        _audioCombo.Width = 110;
        _audioCombo.Items.AddRange(new object[] { "Mix all", "Muted" });
        _audioCombo.SelectedIndex = 0;
        _audioCombo.SelectedIndexChanged += (_, _) =>
            _settings.AudioMode = _audioCombo.SelectedIndex == 1 ? AudioMode.Silent : AudioMode.MixAll;

        var ffmpegButton = new ToolStripButton("FFmpeg...")
        {
            DisplayStyle = ToolStripItemDisplayStyle.Text,
            Alignment = ToolStripItemAlignment.Right,
            ToolTipText = "Tell the app where ffmpeg.exe is"
        };
        ffmpegButton.Click += (_, _) => ShowFfmpegSetup(alreadyFound: true);

        var gridLabel = new ToolStripLabel("Grid")
        {
            BackColor = Color.WhiteSmoke,
            ForeColor = Color.Black
        };

        var audioLabel = new ToolStripLabel("Audio")
        {
            BackColor = Color.WhiteSmoke,
            ForeColor = Color.Black
        };

        var volumeLabel = new ToolStripLabel("Vol")
        {
            BackColor = Color.WhiteSmoke,
            ForeColor = Color.Black
        };

        _volumeSlider.Value = 100;
        _volumeSlider.ValueChanged += (_, _) => OnVolumeChanged();

        var volumeHost = new ToolStripControlHost(_volumeSlider)
        {
            AutoSize = false,
            Size = new Size(90, 22),
            ToolTipText = "Playback volume"
        };

        _volumeValueLabel.Text = "100%";
        _volumeValueLabel.BackColor = Color.WhiteSmoke;
        _volumeValueLabel.ForeColor = Color.Black;
        _volumeValueLabel.AutoSize = false;
        _volumeValueLabel.Width = 40;
        _volumeValueLabel.TextAlign = ContentAlignment.MiddleCenter;

        _toolStrip.Items.AddRange(new ToolStripItem[]
        {
            addButton,
            clearButton,
            new ToolStripSeparator(),
            gridLabel,
            _gridSizeCombo,
            new ToolStripSeparator(),
            audioLabel,
            _audioCombo,
            new ToolStripSeparator(),
            _playButton,
            _pauseButton,
            _stopButton,
            new ToolStripSeparator(),
            volumeLabel,
            volumeHost,
            _volumeValueLabel,
            new ToolStripSeparator(),
            _exportButton,
            ffmpegButton
        });

        Controls.Add(_toolStrip);
    }

    private void BuildGridHost()
    {
        _grid.Dock = DockStyle.Fill;
        _grid.BackColor = Theme.Canvas;
        _grid.Padding = new Padding(10);
        _grid.CellBorderStyle = TableLayoutPanelCellBorderStyle.None;

        _gridHost.Dock = DockStyle.Fill;
        _gridHost.BackColor = Theme.Canvas;
        _gridHost.Padding = new Padding(0);

        _gridHost.Controls.Add(_grid);
        Controls.Add(_gridHost);
        _gridHost.BringToFront();
    }

    private void BuildStatusStrip()
    {
        // Explicit colors, not just left to inherit -- StatusStrip's BackColor is an ambient
        // property, so leaving it unset meant it picked up the Form's dark Theme.Panel
        // background while the text stayed dark too, making the whole bar unreadable.
        _statusStrip.BackColor = Color.WhiteSmoke;

        _statusLabel.Spring = true;
        _statusLabel.TextAlign = ContentAlignment.MiddleLeft;
        _statusLabel.ForeColor = Color.Black;

        _progressBar.Width = 220;
        _progressBar.Maximum = 1000;
        _progressBar.Visible = false;

        _statusStrip.Items.AddRange(new ToolStripItem[] { _statusLabel, _progressBar });
        _statusStrip.Dock = DockStyle.Bottom;
        _statusStrip.SizingGrip = false;
        Controls.Add(_statusStrip);
    }

    /// <summary>Recreates the tiles, keeping any clips that still fit in the new layout.</summary>
    private void RebuildGrid(int rows, int columns)
    {
        _player.Stop();
        StopPreview();
        ResetZoom();

        List<string?> existing = _slots.Select(s => s.FilePath).ToList();
        List<double> durations = _slots.Select(s => s.DurationSeconds).ToList();
        List<bool> audio = _slots.Select(s => s.HasAudio).ToList();

        _grid.SuspendLayout();

        foreach (VideoCellControl cell in _cells)
        {
            _grid.Controls.Remove(cell);
            cell.Dispose();
        }

        _cells.Clear();
        _slots.Clear();

        _settings.Rows = rows;
        _settings.Columns = columns;

        _grid.Controls.Clear();
        _grid.RowStyles.Clear();
        _grid.ColumnStyles.Clear();
        _grid.RowCount = rows;
        _grid.ColumnCount = columns;

        for (int r = 0; r < rows; r++)
        {
            _grid.RowStyles.Add(new RowStyle(SizeType.Percent, 100f / rows));
        }

        for (int c = 0; c < columns; c++)
        {
            _grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f / columns));
        }

        for (int index = 0; index < rows * columns; index++)
        {
            var slot = new ClipSlot(index);

            if (index < existing.Count && !string.IsNullOrEmpty(existing[index]))
            {
                slot.Assign(existing[index]!);
                slot.DurationSeconds = durations[index];
                slot.HasAudio = audio[index];
            }

            _slots.Add(slot);

            var cell = new VideoCellControl(slot) { Dock = DockStyle.Fill };
            cell.ClipDropped += (sender, path) => AssignClipAsync((VideoCellControl)sender!, path);
            cell.PlayRequested += (sender, _) => OnCellPlayRequested((VideoCellControl)sender!);
            cell.BrowseRequested += (sender, _) => BrowseForCellAsync((VideoCellControl)sender!);
            cell.ClearRequested += (sender, _) => ClearCell((VideoCellControl)sender!);

            _cells.Add(cell);
            _grid.Controls.Add(cell, index % columns, index / columns);
        }

        _grid.ResumeLayout();
        UpdateStatus();
    }

    private void OnGridSizeChanged(object? sender, EventArgs e)
    {
        int selected = _gridSizeCombo.SelectedIndex;

        if (selected < 0 || selected >= GridChoices.Length)
        {
            return;
        }

        (int rows, int columns) = GridChoices[selected];
        RebuildGrid(rows, columns);
    }

    private async void AddVideosAsync()
    {
        using var dialog = new OpenFileDialog
        {
            Title = "Choose videos",
            Multiselect = true,
            Filter = VideoCellControl.OpenFileDialogFilter,
            InitialDirectory = _appSettings.LastInputFolder ?? string.Empty
        };

        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        _appSettings.LastInputFolder = Path.GetDirectoryName(dialog.FileNames.FirstOrDefault());
        _appSettings.Save();

        await AssignToFreeSlotsAsync(dialog.FileNames);
    }

    private async Task AssignToFreeSlotsAsync(IEnumerable<string> paths)
    {
        var queued = new List<ClipSlot>();

        foreach (string path in paths)
        {
            ClipSlot? free = _slots.FirstOrDefault(s => !s.HasClip);

            if (free is null)
            {
                MessageBox.Show(
                    this,
                    "Every tile is taken. Pick a bigger grid or clear a tile first.",
                    "Grid is full",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
                break;
            }

            free.Assign(path);
            queued.Add(free);
            _cells[free.Index].ResetSurface();
        }

        UpdateStatus();

        foreach (ClipSlot slot in queued)
        {
            await ProbeSlotAsync(slot);
        }

        UpdateStatus();
    }

    private async void AssignClipAsync(VideoCellControl cell, string path)
    {
        if (ReferenceEquals(cell, _previewCell))
        {
            StopPreview();
        }

        cell.Slot.Assign(path);
        cell.ResetSurface();
        UpdateStatus();
        await ProbeSlotAsync(cell.Slot);
        UpdateStatus();
    }

    private async void BrowseForCellAsync(VideoCellControl cell)
    {
        using var dialog = new OpenFileDialog
        {
            Title = $"Choose the video for tile {cell.Slot.Index + 1}",
            Filter = VideoCellControl.OpenFileDialogFilter,
            InitialDirectory = _appSettings.LastInputFolder ?? string.Empty
        };

        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        _appSettings.LastInputFolder = Path.GetDirectoryName(dialog.FileName);
        _appSettings.Save();

        if (ReferenceEquals(cell, _previewCell))
        {
            StopPreview();
        }

        cell.Slot.Assign(dialog.FileName);
        cell.ResetSurface();
        UpdateStatus();
        await ProbeSlotAsync(cell.Slot);
        UpdateStatus();
    }

    private void ClearCell(VideoCellControl cell)
    {
        if (ReferenceEquals(cell, _previewCell))
        {
            StopPreview();
        }

        cell.Slot.Clear();
        cell.ResetSurface();
        UpdateStatus();
    }

    private void ClearAll()
    {
        _player.Stop();
        StopPreview();
        ResetZoom();

        foreach (VideoCellControl cell in _cells)
        {
            cell.Slot.Clear();
            cell.ResetSurface();
        }

        UpdateStatus();
    }

    /// <summary>
    /// Reads duration and audio presence. ffprobe is the first choice; when it is not
    /// installed, LibVLC can answer the same question so preview still works.
    /// </summary>
    private async Task ProbeSlotAsync(ClipSlot slot)
    {
        if (!slot.HasClip)
        {
            return;
        }

        _statusLabel.Text = $"Reading {slot.DisplayName}...";

        try
        {
            if (_ffprobePath is not null)
            {
                MediaInfo info = await MediaProbe.ProbeAsync(_ffprobePath, slot.FilePath!);

                if (info.DurationSeconds > 0)
                {
                    slot.DurationSeconds = info.DurationSeconds;
                    slot.HasAudio = info.HasAudio;
                    _cells[slot.Index].RefreshSurface();
                    return;
                }
            }

            await ProbeWithLibVlcAsync(slot);
        }
        catch (Exception ex)
        {
            try
            {
                await ProbeWithLibVlcAsync(slot);
            }
            catch
            {
                MessageBox.Show(
                    this,
                    $"Could not read \"{slot.DisplayName}\".{Environment.NewLine}{Environment.NewLine}{ex.Message}",
                    "Unreadable file",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
                slot.Clear();
            }
        }
        finally
        {
            _cells[slot.Index].RefreshSurface();

            if (slot.HasClip && slot.DurationSeconds > 0)
            {
                _ = LoadThumbnailAsync(slot, slot.FilePath!);
            }
        }
    }

    /// <summary>
    /// Grabs the clip's first frame via FFmpeg and shows it on the tile while it waits its
    /// turn, instead of the plain "Ready" placeholder -- purely cosmetic, so any failure here
    /// is silent and just leaves the placeholder in place.
    /// </summary>
    private async Task LoadThumbnailAsync(ClipSlot slot, string filePath)
    {
        if (_ffmpegPath is null)
        {
            return;
        }

        string thumbnailPath = Path.Combine(_thumbnailDirectory, $"slot_{slot.Index:D2}_{Guid.NewGuid():N}.png");

        try
        {
            bool extracted = await ThumbnailExtractor.ExtractFirstFrameAsync(_ffmpegPath, filePath, thumbnailPath);

            // The slot may have moved on (started playing, been cleared, reassigned) while
            // FFmpeg was still working; a stale thumbnail should never overwrite that.
            if (!extracted || slot.State != CellState.Queued || slot.FilePath != filePath)
            {
                return;
            }

            byte[] bytes = await File.ReadAllBytesAsync(thumbnailPath);
            using var stream = new MemoryStream(bytes);
            using Image decoded = Image.FromStream(stream);
            var copy = new Bitmap(decoded);

            if (slot.State == CellState.Queued && slot.FilePath == filePath)
            {
                _cells[slot.Index].SetThumbnail(copy);
            }
            else
            {
                copy.Dispose();
            }
        }
        catch
        {
            // A thumbnail is a nice-to-have; failing quietly just leaves the plain placeholder.
        }
        finally
        {
            TryDeleteFile(thumbnailPath);
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Leftover thumbnail files are harmless.
        }
    }

    private async Task ProbeWithLibVlcAsync(ClipSlot slot)
    {
        using var media = new Media(_libVlc, slot.FilePath!, FromType.FromPath);
        MediaParsedStatus status = await media.Parse(MediaParseOptions.ParseLocal, timeout: 8000);

        if (status != MediaParsedStatus.Done)
        {
            throw new InvalidOperationException(
                $"VLC could not read \"{slot.DisplayName}\" ({status}).");
        }

        slot.DurationSeconds = media.Duration > 0 ? media.Duration / 1000.0 : 0;
        slot.HasAudio = media.Tracks.Any(t => t.TrackType == TrackType.Audio);
    }

    private void StartPlayback()
    {
        if (!_slots.Any(s => s.HasClip))
        {
            MessageBox.Show(this, "Add a video first.", "Nothing to play", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        StopPreview();
        _player.Start(_cells, _settings.AudioMode == AudioMode.Silent);
        _playButton.Enabled = false;
        _pauseButton.Enabled = true;
        _pauseButton.Text = "Pause";
        _stopButton.Enabled = true;
        _progressBar.Visible = true;
    }

    private void TogglePause()
    {
        _player.TogglePause();
        _pauseButton.Text = _player.IsPaused ? "Resume" : "Pause";
    }

    private void OnVolumeChanged()
    {
        _player.SetVolume(_volumeSlider.Value);
        _volumeValueLabel.Text = $"{_volumeSlider.Value}%";
    }

    /// <summary>
    /// Plays one tile's clip in place, independent of SequentialGridPlayer -- clicking a tile is
    /// a quick single-clip preview, not the same thing as "Play in order". Ignored while
    /// SequentialGridPlayer already owns the active tile's VideoSurface, since swapping its
    /// player out from under it would leave SequentialGridPlayer holding a dangling reference.
    /// Clicking the tile already previewing stops it; clicking a different one switches to that
    /// tile instead.
    /// </summary>
    private void OnCellPlayRequested(VideoCellControl cell)
    {
        if (_player.IsRunning || !cell.Slot.HasClip)
        {
            return;
        }

        if (ReferenceEquals(cell, _previewCell))
        {
            StopPreview();
            return;
        }

        StopPreview();

        try
        {
            var player = new MediaPlayer(_libVlc) { EnableHardwareDecoding = true };
            player.EndReached += (_, _) => BeginInvoke(new Action(StopPreview));
            player.EncounteredError += (_, _) => BeginInvoke(new Action(StopPreview));

            cell.ShowVideo(player);

            using var media = new Media(_libVlc, cell.Slot.FilePath!, FromType.FromPath);
            player.Play(media);

            _previewCell = cell;
            _previewPlayer = player;
        }
        catch
        {
            // The tile just stays on its placeholder if this particular file can't be previewed.
        }
    }

    /// <summary>Stops the single-tile preview started by OnCellPlayRequested, if any.</summary>
    private void StopPreview()
    {
        if (_previewPlayer is null)
        {
            return;
        }

        MediaPlayer player = _previewPlayer;
        VideoCellControl? cell = _previewCell;
        _previewPlayer = null;
        _previewCell = null;

        cell?.DetachPlayer();
        cell?.ResetSurface();

        // Tearing a player down can block for a moment, so keep it off the UI thread -- same
        // reasoning as SequentialGridPlayer's own Release().
        ThreadPool.QueueUserWorkItem(_ =>
        {
            try
            {
                player.Stop();
                player.Dispose();
            }
            catch
            {
                // Nothing useful to do if VLC is already gone.
            }
        });
    }

    private void StopPlayback()
    {
        _player.Stop();
        ResetZoom();
        ResetTransport();
        UpdateStatus();
    }

    private void ResetTransport()
    {
        _playButton.Enabled = true;
        _pauseButton.Enabled = false;
        _pauseButton.Text = "Pause";
        _stopButton.Enabled = false;
        _progressBar.Visible = false;
        _progressBar.Value = 0;
    }

    private void OnClipStarted(object? sender, int index)
    {
        _cells[index].RefreshSurface();
        ZoomIn(_cells[index]);
    }

    private void OnClipFinished(object? sender, int index)
    {
        _cells[index].RefreshSurface();
        ZoomOut(_cells[index]);
    }

    /// <summary>
    /// Lifts the actively-playing tile out of the TableLayoutPanel and re-hosts it directly on
    /// the grid host, centered over the grid at settings.SpotlightScale of its area (the other
    /// tiles stay visible behind it) -- mirrors the same centered spotlight
    /// SequentialGridFilterGraphBuilder bakes into the exported video for the same clip.
    /// </summary>
    private void ZoomIn(VideoCellControl cell)
    {
        if (_zoomedCells.ContainsKey(cell))
        {
            return;
        }

        TableLayoutPanelCellPosition position = _grid.GetPositionFromControl(cell);

        if (position.Column < 0 || position.Row < 0)
        {
            return;
        }

        _grid.Controls.Remove(cell);
        _gridHost.Controls.Add(cell);
        cell.Dock = DockStyle.None;
        cell.Bounds = CenteredFraction(_grid.Bounds, _settings.SpotlightScale);
        cell.BringToFront();

        _zoomedCells[cell] = (position.Column, position.Row);
    }

    /// <summary>The rectangle of size <paramref name="fraction"/> of <paramref name="bounds"/>, centered within it.</summary>
    private static Rectangle CenteredFraction(Rectangle bounds, float fraction)
    {
        int width = Math.Max(1, (int)Math.Round(bounds.Width * fraction));
        int height = Math.Max(1, (int)Math.Round(bounds.Height * fraction));
        int x = bounds.X + (bounds.Width - width) / 2;
        int y = bounds.Y + (bounds.Height - height) / 2;
        return new Rectangle(x, y, width, height);
    }

    /// <summary>Puts a zoomed-in tile back into its grid cell at normal size.</summary>
    private void ZoomOut(VideoCellControl cell)
    {
        if (!_zoomedCells.TryGetValue(cell, out (int Column, int Row) position))
        {
            return;
        }

        _zoomedCells.Remove(cell);
        _gridHost.Controls.Remove(cell);
        cell.Dock = DockStyle.Fill;
        _grid.Controls.Add(cell, position.Column, position.Row);
    }

    /// <summary>Restores every currently-zoomed tile, e.g. before the grid is rebuilt or torn down.</summary>
    private void ResetZoom()
    {
        foreach (VideoCellControl cell in _zoomedCells.Keys.ToList())
        {
            ZoomOut(cell);
        }
    }

    private void OnPlaybackProgress(object? sender, PlaybackProgressEventArgs e)
    {
        _progressBar.Value = (int)Math.Round(e.Fraction * _progressBar.Maximum);
        _statusLabel.Text =
            $"{Clock(e.ElapsedSeconds)} / {Clock(e.TotalSeconds)}   ·   " +
            $"{e.FinishedCount} of {_slots.Count(s => s.HasClip)} clips finished";
    }

    private void OnSequenceCompleted(object? sender, EventArgs e)
    {
        ResetTransport();
        _statusLabel.Text = "All clips finished playing in order.";
    }

    private void OnPlaybackFailed(object? sender, string message)
    {
        _statusLabel.Text = message;
    }

    private void ShowExportDialog()
    {
        if (!_slots.Any(s => s.HasClip))
        {
            MessageBox.Show(this, "Add a video first.", "Nothing to export", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        if (_ffmpegPath is null)
        {
            // One more sweep first: FFmpeg may have been installed since the app started.
            RefreshFfmpegPaths(_appSettings.FfmpegPath);
        }

        if (_ffmpegPath is null && !ShowFfmpegSetup(alreadyFound: false))
        {
            return;
        }

        _player.Stop();
        StopPreview();
        ResetTransport();

        foreach (ClipSlot slot in _slots.Where(s => s.HasClip && s.DurationSeconds <= 0))
        {
            MessageBox.Show(
                this,
                $"\"{slot.DisplayName}\" has no readable duration, so it cannot be placed on the timeline.",
                "Cannot export",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            return;
        }

        using var dialog = new ExportDialog(_settings, _slots, _ffmpegPath!, _appSettings, sequential: true);
        dialog.ShowDialog(this);
        UpdateStatus();
    }

    /// <summary>Offers to download FFmpeg or to point at an existing copy.</summary>
    private bool ShowFfmpegSetup(bool alreadyFound)
    {
        using var dialog = new FfmpegSetupDialog(alreadyFound ? _ffmpegPath : null);

        if (dialog.ShowDialog(this) != DialogResult.OK || dialog.FfmpegPath is null)
        {
            return false;
        }

        RefreshFfmpegPaths(dialog.FfmpegPath);

        if (_ffmpegPath is null)
        {
            MessageBox.Show(
                this,
                "That file could not be used as FFmpeg.",
                "FFmpeg not usable",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            return false;
        }

        return true;
    }

    /// <summary>Re-runs detection and remembers whatever it finds.</summary>
    private void RefreshFfmpegPaths(string? preferred)
    {
        _ffmpegPath = FfmpegLocator.FindFfmpeg(preferred);
        _ffprobePath = FfmpegLocator.FindFfprobe(_ffmpegPath);

        if (_ffmpegPath is not null)
        {
            _appSettings.FfmpegPath = _ffmpegPath;
            _appSettings.Save();
        }

        UpdateStatus();
    }

    private void UpdateStatus()
    {
        int filled = _slots.Count(s => s.HasClip);
        List<double> durations = _slots.Where(s => s.HasClip).Select(s => s.DurationSeconds).ToList();
        double total = durations.Sum();

        string ffmpeg = _ffmpegPath is null
            ? "FFmpeg: not set up (click \"FFmpeg...\")"
            : "FFmpeg: ready";

        _statusLabel.Text = filled == 0
            ? $"Drop videos onto the tiles, or use \"Add videos...\".   ·   {ffmpeg}"
            : $"{filled} of {_slots.Count} tiles filled   ·   total length {Clock(total)} (played one after another)   ·   {ffmpeg}";

        _exportButton.Enabled = filled > 0;
        _playButton.Enabled = filled > 0 && !_player.IsRunning;
    }

    private void OnFormDragEnter(object? sender, DragEventArgs e)
    {
        bool hasVideo = e.Data?.GetData(DataFormats.FileDrop) is string[] files &&
                        files.Any(VideoCellControl.LooksLikeVideo);

        e.Effect = hasVideo ? DragDropEffects.Copy : DragDropEffects.None;
    }

    private async void OnFormDragDrop(object? sender, DragEventArgs e)
    {
        if (e.Data?.GetData(DataFormats.FileDrop) is string[] files)
        {
            await AssignToFreeSlotsAsync(files.Where(VideoCellControl.LooksLikeVideo));
        }
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Space)
        {
            if (_player.IsRunning)
            {
                TogglePause();
            }
            else
            {
                StartPlayback();
            }

            e.Handled = true;
        }
        else if (e.KeyCode == Keys.Escape && _player.IsRunning)
        {
            StopPlayback();
            e.Handled = true;
        }
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        // Let the event run first: a handler may still cancel the close.
        base.OnFormClosing(e);

        if (!e.Cancel)
        {
            _player.Stop();
            _player.Dispose();
            StopPreview();
        }
    }

    private void InitializeComponent()
    {

    }

    private static string Clock(double seconds)
    {
        if (double.IsNaN(seconds) || seconds < 0)
        {
            seconds = 0;
        }

        TimeSpan span = TimeSpan.FromSeconds(seconds);

        return span.TotalHours >= 1
            ? span.ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture)
            : span.ToString(@"m\:ss", CultureInfo.InvariantCulture);
    }
}
