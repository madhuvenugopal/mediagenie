using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.Windows.Forms;
using VideoGridStudio.Export;
using VideoGridStudio.Models;
using VideoGridStudio.Rendering;

namespace VideoGridStudio.Forms;

/// <summary>Picks the output settings, then runs the FFmpeg job and shows its progress.</summary>
public sealed class ExportDialog : Form
{
    private readonly bool _sequential;
    private readonly GridSettings _settings;
    private readonly IReadOnlyList<ClipSlot> _slots;
    private readonly string _ffmpegPath;
    private readonly AppSettings _appSettings;

    private readonly TextBox _outputBox = new();
    private readonly ComboBox _resolutionBox = new();
    private readonly ComboBox _fpsBox = new();
    private readonly ComboBox _qualityBox = new();
    private readonly ComboBox _audioBox = new();
    private readonly List<(AudioMode Mode, int TileIndex)> _audioChoices = new();
    private readonly TextBox _musicBox = new();
    private readonly Button _musicBrowseButton = new();
    private readonly Button _musicClearButton = new();
    private string? _musicPath;
    private readonly NumericUpDown _gutterBox = new();
    private readonly Label _summaryLabel = new();
    private readonly ProgressBar _progressBar = new();
    private readonly Label _statusLabel = new();
    private readonly Button _startButton = new();
    private readonly Button _closeButton = new();

    private CancellationTokenSource? _cancellation;
    private string? _finishedFile;

    private const string NoMusicText = "(none - original clip audio used)";
    private static readonly string[] MusicExtensions = { ".mp3", ".wav", ".wma", ".flac", ".aac", ".ogg", ".m4a" };

    public ExportDialog(
        GridSettings settings,
        IReadOnlyList<ClipSlot> slots,
        string ffmpegPath,
        AppSettings appSettings,
        bool sequential = false)
    {
        _sequential = sequential;
        _settings = settings.Clone();
        _slots = slots;
        _ffmpegPath = ffmpegPath;
        _appSettings = appSettings;

        Text = sequential ? "Export playlist to a single video" : "Export grid to a single video";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = false;
        MinimizeBox = false;
        ClientSize = new Size(620, 560);

        BuildLayout();
        LoadDefaults();
        UpdateSummary();
    }

    private void BuildLayout()
    {
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 3,
            Padding = new Padding(16),
            AutoSize = false
        };

        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 90));

        _outputBox.Dock = DockStyle.Fill;

        var browseButton = new Button { Text = "Browse...", Dock = DockStyle.Fill };
        browseButton.Click += (_, _) => BrowseForOutput();

        foreach ((string label, _, _) in ExportPresets.Resolutions)
        {
            _resolutionBox.Items.Add(label);
        }

        foreach (int fps in ExportPresets.FrameRates)
        {
            _fpsBox.Items.Add(fps.ToString(CultureInfo.InvariantCulture));
        }

        foreach ((string label, _, _) in ExportPresets.Qualities)
        {
            _qualityBox.Items.Add(label);
        }

        foreach (ComboBox box in new[] { _resolutionBox, _fpsBox, _qualityBox, _audioBox })
        {
            box.DropDownStyle = ComboBoxStyle.DropDownList;
            box.Dock = DockStyle.Fill;
            box.SelectedIndexChanged += (_, _) => UpdateSummary();
        }

        _musicBox.Dock = DockStyle.Fill;
        _musicBox.ReadOnly = true;
        _musicBox.Text = NoMusicText;

        _musicBrowseButton.Text = "Browse...";
        _musicBrowseButton.Width = 90;
        _musicBrowseButton.Height = 26;
        _musicBrowseButton.Click += (_, _) => BrowseForMusic();

        _musicClearButton.Text = "Clear";
        _musicClearButton.Width = 90;
        _musicClearButton.Height = 26;
        _musicClearButton.Margin = new Padding(0, 4, 0, 0);
        _musicClearButton.Click += (_, _) => SetMusic(null);

        var musicButtons = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            AutoSize = true,
            WrapContents = false
        };
        musicButtons.Controls.Add(_musicBrowseButton);
        musicButtons.Controls.Add(_musicClearButton);

        _gutterBox.Minimum = 0;
        _gutterBox.Maximum = 40;
        _gutterBox.Value = 8;
        _gutterBox.Dock = DockStyle.Left;
        _gutterBox.Width = 70;
        _gutterBox.ValueChanged += (_, _) => UpdateSummary();

        _summaryLabel.Dock = DockStyle.Fill;
        _summaryLabel.ForeColor = Color.FromArgb(90, 90, 96);
        _summaryLabel.AutoSize = false;
        _summaryLabel.Height = 72;

        _progressBar.Dock = DockStyle.Fill;
        _progressBar.Maximum = 1000;
        _progressBar.Height = 22;

        _statusLabel.Dock = DockStyle.Fill;
        _statusLabel.Text = "Ready.";

        _startButton.Text = "Start export";
        _startButton.Dock = DockStyle.Fill;
        _startButton.Click += (_, _) => StartOrCancelAsync();

        _closeButton.Text = "Close";
        _closeButton.Dock = DockStyle.Fill;
        _closeButton.Click += (_, _) => Close();

        int row = 0;
        AddRow(layout, row++, "Save to", _outputBox, browseButton);
        AddRow(layout, row++, "Frame size", _resolutionBox, null);
        AddRow(layout, row++, "Frame rate", _fpsBox, null);
        AddRow(layout, row++, "Quality", _qualityBox, null);
        AddRow(layout, row++, "Audio", _audioBox, null);
        AddRow(layout, row++, "Background music", _musicBox, musicButtons);
        AddRow(layout, row++, "Tile gap", _gutterBox, null);
        AddRow(layout, row++, string.Empty, _summaryLabel, null);
        AddRow(layout, row++, "Progress", _progressBar, null);
        AddRow(layout, row++, string.Empty, _statusLabel, null);

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            AutoSize = true
        };

        _startButton.Width = 120;
        _closeButton.Width = 90;
        _startButton.Dock = DockStyle.None;
        _closeButton.Dock = DockStyle.None;
        buttons.Controls.Add(_closeButton);
        buttons.Controls.Add(_startButton);

        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(buttons, 1, row);
        layout.SetColumnSpan(buttons, 2);

        Controls.Add(layout);
    }

    private static void AddRow(TableLayoutPanel layout, int row, string label, Control control, Control? trailing)
    {
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        if (!string.IsNullOrEmpty(label))
        {
            layout.Controls.Add(
                new Label { Text = label, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, Margin = new Padding(0, 6, 6, 6) },
                0,
                row);
        }

        control.Margin = new Padding(0, 5, 6, 5);
        layout.Controls.Add(control, 1, row);

        if (trailing is not null)
        {
            trailing.Margin = new Padding(0, 5, 0, 5);
            layout.Controls.Add(trailing, 2, row);
        }
        else
        {
            layout.SetColumnSpan(control, 2);
        }
    }

    private void LoadDefaults()
    {
        _resolutionBox.SelectedIndex = 1;
        _fpsBox.SelectedIndex = 2;
        _qualityBox.SelectedIndex = 1;
        _gutterBox.Value = _settings.Gutter;

        BuildAudioChoices();
        SetMusic(string.IsNullOrWhiteSpace(_settings.BackgroundMusicPath) ? null : _settings.BackgroundMusicPath);

        string folder = _appSettings.LastOutputFolder ??
                        Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);

        _outputBox.Text = Path.Combine(folder, _sequential ? "video-sequence.mp4" : "video-grid.mp4");
    }

    /// <summary>Offers "everything mixed", one entry per tile that actually has sound, and silence.</summary>
    private void BuildAudioChoices()
    {
        _audioChoices.Clear();
        _audioBox.Items.Clear();

        _audioChoices.Add((AudioMode.MixAll, 0));
        _audioBox.Items.Add("Mix every clip together");

        foreach (ClipSlot slot in _slots.Where(s => s.HasClip && s.HasAudio).OrderBy(s => s.Index))
        {
            _audioChoices.Add((AudioMode.SingleTile, slot.Index));
            _audioBox.Items.Add($"Only tile {slot.Index + 1} ({slot.DisplayName})");
        }

        _audioChoices.Add((AudioMode.Silent, 0));
        _audioBox.Items.Add("No sound");

        int preselected = _audioChoices.FindIndex(c =>
            c.Mode == _settings.AudioMode &&
            (c.Mode != AudioMode.SingleTile || c.TileIndex == _settings.AudioTileIndex));

        _audioBox.SelectedIndex = preselected >= 0 ? preselected : 0;
    }

    private void BrowseForOutput()
    {
        using var dialog = new SaveFileDialog
        {
            Title = "Save the combined video",
            Filter = "MP4 video|*.mp4",
            FileName = Path.GetFileName(_outputBox.Text),
            InitialDirectory = SafeDirectory(_outputBox.Text)
        };

        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            _outputBox.Text = dialog.FileName;
        }
    }

    private void BrowseForMusic()
    {
        string filter = "Audio files|*" + string.Join(";*", MusicExtensions) + "|All files|*.*";

        using var dialog = new OpenFileDialog
        {
            Title = "Choose background music",
            Filter = filter,
            InitialDirectory = SafeDirectory(_musicPath ?? string.Empty)
        };

        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            SetMusic(dialog.FileName);
        }
    }

    /// <summary>
    /// Also flips the Audio dropdown's enabled state: once background music is chosen it
    /// completely replaces the clips' own audio (see GridSettings.BackgroundMusicPath), so
    /// whatever mix/single-tile/silent choice sits in that dropdown no longer applies.
    /// </summary>
    private void SetMusic(string? path)
    {
        _musicPath = string.IsNullOrWhiteSpace(path) ? null : path;
        _musicBox.Text = _musicPath is null ? NoMusicText : Path.GetFileName(_musicPath);
        _audioBox.Enabled = _musicPath is null;
        UpdateSummary();
    }

    private void ApplySettings()
    {
        (_, int width, int height) = ExportPresets.Resolutions[Math.Max(0, _resolutionBox.SelectedIndex)];
        (_, int crf, string preset) = ExportPresets.Qualities[Math.Max(0, _qualityBox.SelectedIndex)];

        _settings.OutputWidth = width;
        _settings.OutputHeight = height;
        _settings.FrameRate = int.Parse((string)_fpsBox.SelectedItem!, CultureInfo.InvariantCulture);
        _settings.Crf = crf;
        _settings.Preset = preset;
        _settings.Gutter = (int)_gutterBox.Value;

        if (_audioBox.SelectedIndex >= 0 && _audioBox.SelectedIndex < _audioChoices.Count)
        {
            (AudioMode mode, int tile) = _audioChoices[_audioBox.SelectedIndex];
            _settings.AudioMode = mode;
            _settings.AudioTileIndex = tile;
        }

        _settings.BackgroundMusicPath = _musicPath;
    }

    private void UpdateSummary()
    {
        if (_resolutionBox.SelectedIndex < 0 || _fpsBox.SelectedIndex < 0 || _qualityBox.SelectedIndex < 0)
        {
            return;
        }

        ApplySettings();

        List<double> durations = _slots.Where(s => s.HasClip).Select(s => s.DurationSeconds).ToList();

        if (_sequential)
        {
            double total = durations.Sum();

            _summaryLabel.Text =
                $"{durations.Count} clip(s) play one after another, in grid order  ·  finished video is {FormatClock(total)} long." +
                Environment.NewLine +
                $"Grid {_settings.Columns} x {_settings.Rows}, each tile {_settings.CellWidth} x {_settings.CellHeight} px." +
                Environment.NewLine +
                "Each tile shows its placeholder until its turn, then holds its last frame once it's done.";
        }
        else
        {
            double longest = durations.Count > 0 ? durations.Max() : 0;

            _summaryLabel.Text =
                $"{durations.Count} clip(s) all starting together  ·  finished video is {FormatClock(longest)} long " +
                "(as long as the longest clip)." +
                Environment.NewLine +
                $"Grid {_settings.Columns} x {_settings.Rows}, each tile {_settings.CellWidth} x {_settings.CellHeight} px." +
                Environment.NewLine +
                "Clips that end early keep showing their last frame until the longest one finishes.";
        }

        if (_musicPath is not null)
        {
            _summaryLabel.Text += Environment.NewLine +
                $"Background music \"{Path.GetFileName(_musicPath)}\" replaces every clip's own audio (looped or trimmed to fit).";
        }
    }

    private async void StartOrCancelAsync()
    {
        if (_cancellation is not null)
        {
            _cancellation.Cancel();
            _statusLabel.Text = "Cancelling...";
            return;
        }

        string output = _outputBox.Text.Trim();

        if (string.IsNullOrEmpty(output))
        {
            MessageBox.Show(this, "Choose where to save the video first.", "No output file", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        try
        {
            string? folder = Path.GetDirectoryName(output);

            if (!string.IsNullOrEmpty(folder))
            {
                Directory.CreateDirectory(folder);
                _appSettings.LastOutputFolder = folder;
                _appSettings.Save();
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Cannot write to that folder.{Environment.NewLine}{ex.Message}", "Bad location", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        ApplySettings();

        string workFolder = Path.Combine(Path.GetTempPath(), "VideoGridStudio", "export");
        Directory.CreateDirectory(workFolder);
        string backgroundPath = Path.Combine(workFolder, "grid-background.png");
        string filterPath = Path.Combine(workFolder, "filter.txt");

        SetBusy(true);
        _cancellation = new CancellationTokenSource();

        try
        {
            // Placeholders are baked once into a full-frame PNG that sits under every clip.
            var placeholderSlots = _slots.Select(s =>
            {
                var copy = new ClipSlot(s.Index);

                if (s.HasClip)
                {
                    copy.Assign(s.FilePath!);
                    copy.DurationSeconds = s.DurationSeconds;
                    copy.State = CellState.Queued;
                }

                return copy;
            }).ToList();

            // In sequential mode, every tile spends time waiting its turn before it starts
            // playing -- during that stretch the exported canvas shows through underneath,
            // so it needs each clip's own first frame baked in (matching the live grid's
            // poster thumbnails) instead of the plain "Ready" placeholder. Grid mode has no
            // such waiting period (every tile is live from t=0), so it skips this.
            var thumbnails = new Dictionary<int, Image>();

            if (_sequential)
            {
                _statusLabel.Text = "Grabbing thumbnails...";
                thumbnails = await BuildThumbnailsAsync(workFolder, _cancellation.Token);
            }

            try
            {
                await Task.Run(() => PlaceholderRenderer.SaveCanvasPng(_settings, placeholderSlots, backgroundPath, thumbnails), _cancellation.Token);
            }
            finally
            {
                foreach (Image thumbnail in thumbnails.Values)
                {
                    thumbnail.Dispose();
                }
            }

            GridCompositionPlan plan = _sequential
                ? SequentialGridFilterGraphBuilder.Build(_settings, _slots, backgroundPath, output)
                : GridFilterGraphBuilder.Build(_settings, _slots, backgroundPath, output);

            var progress = new Progress<ExportProgress>(p =>
            {
                _progressBar.Value = (int)Math.Round(p.Fraction * _progressBar.Maximum);
                _statusLabel.Text = $"{p.Message}  {p.Percent}%  ({FormatClock(p.EncodedSeconds)} of {FormatClock(p.TotalSeconds)})";
            });

            await FfmpegRunner.RunAsync(_ffmpegPath, plan.Arguments, plan.FilterScript, plan.TotalSeconds, filterPath, progress, _cancellation.Token);

            _finishedFile = output;
            _progressBar.Value = _progressBar.Maximum;
            _statusLabel.Text = $"Saved to {output}";

            if (MessageBox.Show(
                    this,
                    $"Export finished.{Environment.NewLine}{Environment.NewLine}{output}{Environment.NewLine}{Environment.NewLine}Open the folder?",
                    "Done",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Information) == DialogResult.Yes)
            {
                RevealInExplorer(output);
            }
        }
        catch (OperationCanceledException)
        {
            _statusLabel.Text = "Export cancelled.";
            _progressBar.Value = 0;
            TryDelete(output);
        }
        catch (Exception ex)
        {
            _statusLabel.Text = "Export failed.";
            _progressBar.Value = 0;
            MessageBox.Show(this, ex.Message, "Export failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            _cancellation?.Dispose();
            _cancellation = null;
            SetBusy(false);
        }
    }

    /// <summary>
    /// Grabs each filled slot's first frame via FFmpeg, in parallel. A slot that fails to
    /// produce one is simply left out -- that tile falls back to the plain placeholder in
    /// the exported canvas, same as the live grid does when a thumbnail never loads.
    /// </summary>
    private async Task<Dictionary<int, Image>> BuildThumbnailsAsync(string workFolder, CancellationToken token)
    {
        string thumbnailDirectory = Path.Combine(workFolder, "thumbnails");
        Directory.CreateDirectory(thumbnailDirectory);

        IEnumerable<Task<(int Index, Image? Thumbnail)>> extractions = _slots
            .Where(s => s.HasClip)
            .Select(async slot =>
            {
                string thumbnailPath = Path.Combine(thumbnailDirectory, $"slot_{slot.Index:D2}.png");

                try
                {
                    bool extracted = await ThumbnailExtractor.ExtractFirstFrameAsync(_ffmpegPath, slot.FilePath!, thumbnailPath, token);

                    if (!extracted)
                    {
                        return (slot.Index, (Image?)null);
                    }

                    byte[] bytes = await File.ReadAllBytesAsync(thumbnailPath, token);
                    using var stream = new MemoryStream(bytes);
                    using Image decoded = Image.FromStream(stream);
                    return (slot.Index, (Image?)new Bitmap(decoded));
                }
                catch
                {
                    return (slot.Index, (Image?)null);
                }
            });

        var thumbnails = new Dictionary<int, Image>();

        foreach ((int index, Image? thumbnail) in await Task.WhenAll(extractions))
        {
            if (thumbnail is not null)
            {
                thumbnails[index] = thumbnail;
            }
        }

        return thumbnails;
    }

    private void SetBusy(bool busy)
    {
        _startButton.Text = busy ? "Cancel" : "Start export";
        _closeButton.Enabled = !busy;
        _outputBox.Enabled = !busy;
        _resolutionBox.Enabled = !busy;
        _fpsBox.Enabled = !busy;
        _qualityBox.Enabled = !busy;
        _gutterBox.Enabled = !busy;
        _musicBrowseButton.Enabled = !busy;
        _musicClearButton.Enabled = !busy && _musicPath is not null;
        if (busy) _audioBox.Enabled = false;
        else if (_musicPath is null) _audioBox.Enabled = true;
        UseWaitCursor = busy;

        if (!busy)
        {
            _statusLabel.Text = _finishedFile is null ? "Ready." : $"Saved to {_finishedFile}";
        }
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (_cancellation is not null)
        {
            e.Cancel = true;
            _cancellation.Cancel();
            _statusLabel.Text = "Cancelling...";
            return;
        }

        base.OnFormClosing(e);
    }

    private static void RevealInExplorer(string file)
    {
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{file}\"") { UseShellExecute = true });
        }
        catch
        {
            // Not being able to open Explorer is not worth an error dialog.
        }
    }

    private static void TryDelete(string path)
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
            // A half written file left behind is not fatal.
        }
    }

    private static string SafeDirectory(string path)
    {
        try
        {
            string? folder = Path.GetDirectoryName(path);
            return folder is not null && Directory.Exists(folder) ? folder : string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string FormatClock(double seconds)
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
