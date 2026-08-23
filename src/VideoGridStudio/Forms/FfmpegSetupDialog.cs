using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;
using VideoGridStudio.Export;

namespace VideoGridStudio.Forms;

/// <summary>
/// Shown when exporting is attempted without FFmpeg present. Offers to fetch a build
/// automatically, or to point at a copy the user already has.
/// </summary>
public sealed class FfmpegSetupDialog : Form
{
    private readonly Label _explanation = new();
    private readonly Label _statusLabel = new();
    private readonly ProgressBar _progressBar = new();
    private readonly Button _downloadButton = new();
    private readonly Button _locateButton = new();
    private readonly Button _cancelButton = new();
    private readonly LinkLabel _manualLink = new();

    private readonly string? _currentPath;

    private CancellationTokenSource? _cancellation;
    private bool _forceClose;

    public FfmpegSetupDialog(string? currentPath = null)
    {
        _currentPath = currentPath;

        Text = currentPath is null ? "FFmpeg is needed to export" : "FFmpeg location";

        // Sizable rather than fixed: at 150% scaling a fixed height clips the explanation.
        FormBorderStyle = FormBorderStyle.Sizable;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = false;
        MinimizeBox = false;
        ClientSize = new Size(580, 340);
        MinimumSize = new Size(520, 320);

        BuildLayout();
    }

    /// <summary>Full path to ffmpeg.exe once the dialog closes with <see cref="DialogResult.OK"/>.</summary>
    public string? FfmpegPath { get; private set; }

    private void BuildLayout()
    {
        string opening = _currentPath is null
            ? "Playing the grid needs nothing extra, but saving it as a single video is done by " +
              "FFmpeg, which was not found on this machine."
            : "FFmpeg is currently being used from:" + Environment.NewLine + _currentPath;

        _explanation.Text =
            opening + Environment.NewLine + Environment.NewLine +
            "The app can fetch an official Windows build for you. It is about 80 MB, goes into " +
            "your own user folder (no administrator rights needed), and is used only by this app:" +
            Environment.NewLine +
            FfmpegInstaller.InstallFolder;
        _explanation.Dock = DockStyle.Top;
        _explanation.Height = 150;
        _explanation.Padding = new Padding(4, 4, 4, 8);

        _downloadButton.Text = "Download FFmpeg";
        _downloadButton.Width = 150;
        _downloadButton.Height = 30;
        _downloadButton.Click += (_, _) => DownloadAsync();

        _locateButton.Text = "I already have it...";
        _locateButton.Width = 150;
        _locateButton.Height = 30;
        _locateButton.Click += (_, _) => Locate();

        _cancelButton.Text = "Not now";
        _cancelButton.Width = 100;
        _cancelButton.Height = 30;
        _cancelButton.Click += (_, _) => CancelOrClose();

        _progressBar.Dock = DockStyle.Top;
        _progressBar.Height = 20;
        _progressBar.Maximum = 1000;
        _progressBar.Visible = false;

        _statusLabel.Dock = DockStyle.Top;
        _statusLabel.Height = 24;
        _statusLabel.Text = string.Empty;

        _manualLink.Text = "Or install it yourself:  winget install Gyan.FFmpeg";
        _manualLink.Dock = DockStyle.Top;
        _manualLink.Height = 24;
        _manualLink.LinkClicked += (_, _) => OpenFfmpegSite();

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            FlowDirection = FlowDirection.RightToLeft,
            Height = 44,
            Padding = new Padding(0, 6, 0, 0)
        };

        buttons.Controls.Add(_cancelButton);
        buttons.Controls.Add(_locateButton);
        buttons.Controls.Add(_downloadButton);

        var root = new Panel { Dock = DockStyle.Fill, Padding = new Padding(16) };
        root.Controls.Add(_manualLink);
        root.Controls.Add(_statusLabel);
        root.Controls.Add(_progressBar);
        root.Controls.Add(_explanation);

        Controls.Add(root);
        Controls.Add(buttons);

        AcceptButton = _downloadButton;
        CancelButton = _cancelButton;

        // Assigning CancelButton stamps DialogResult.Cancel onto the button, which WinForms
        // applies to the form before our own Click handler runs - closing the dialog behind
        // our back in the middle of a download.
        _cancelButton.DialogResult = DialogResult.None;
    }

    private async void DownloadAsync()
    {
        SetBusy(true);
        _cancellation = new CancellationTokenSource();

        var progress = new Progress<FfmpegInstallProgress>(p =>
        {
            if (IsDisposed || Disposing)
            {
                return;
            }

            if (p.Fraction is { } fraction)
            {
                _progressBar.Style = ProgressBarStyle.Blocks;
                _progressBar.Value = (int)Math.Round(fraction * _progressBar.Maximum);
                _statusLabel.Text = $"{p.Stage}  {Megabytes(p.BytesReceived)} of {Megabytes(p.TotalBytes ?? 0)}";
            }
            else
            {
                _progressBar.Style = ProgressBarStyle.Marquee;
                _statusLabel.Text = p.Stage;
            }
        });

        try
        {
            FfmpegPath = await FfmpegInstaller.InstallAsync(progress, _cancellation.Token);
            _statusLabel.Text = "FFmpeg is ready.";

            // Retire the token source before closing. OnFormClosing refuses to close while
            // one is live, and it would otherwise veto this very close and throw away the
            // DialogResult we just set.
            _cancellation.Dispose();
            _cancellation = null;

            DialogResult = DialogResult.OK;
            Close();
        }
        catch (OperationCanceledException)
        {
            _statusLabel.Text = "Download cancelled.";
        }
        catch (Exception ex)
        {
            _statusLabel.Text = "Download failed.";
            MessageBox.Show(
                this,
                "FFmpeg could not be downloaded." + Environment.NewLine + Environment.NewLine +
                ex.Message + Environment.NewLine + Environment.NewLine +
                "You can install it yourself with \"winget install Gyan.FFmpeg\" and then use " +
                "\"I already have it...\".",
                "Download failed",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
        }
        finally
        {
            _cancellation?.Dispose();
            _cancellation = null;
            _forceClose = false;
            SetBusy(false);
        }
    }

    private void Locate()
    {
        using var dialog = new OpenFileDialog
        {
            Title = "Locate ffmpeg.exe",
            Filter = "ffmpeg.exe|ffmpeg.exe|Executables|*.exe|All files|*.*"
        };

        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        if (!FfmpegLocator.Verify(dialog.FileName))
        {
            MessageBox.Show(
                this,
                "That file did not answer the way FFmpeg does." + Environment.NewLine +
                Environment.NewLine +
                "Pick ffmpeg.exe itself, or let the app download a copy.",
                "Not FFmpeg",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            return;
        }

        FfmpegPath = dialog.FileName;
        DialogResult = DialogResult.OK;
        Close();
    }

    private void CancelOrClose()
    {
        if (_cancellation is not null && !_forceClose)
        {
            _forceClose = true;
            _cancellation.Cancel();
            _statusLabel.Text = "Cancelling...  (click again to close anyway)";
            return;
        }

        DialogResult = DialogResult.Cancel;
        Close();
    }

    private void SetBusy(bool busy)
    {
        _progressBar.Visible = busy;
        _progressBar.Style = ProgressBarStyle.Marquee;
        _downloadButton.Enabled = !busy;
        _locateButton.Enabled = !busy;
        _cancelButton.Text = busy ? "Cancel" : "Not now";
        UseWaitCursor = busy;

        if (!busy)
        {
            _progressBar.Value = 0;
        }
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        base.OnFormClosing(e);

        if (e.Cancel || _cancellation is null || _forceClose)
        {
            return;
        }

        // First close attempt during a download asks the transfer to stop, rather than
        // leaving it running behind a window that is no longer there. A second attempt
        // closes regardless, so the dialog can never become a trap.
        e.Cancel = true;
        _forceClose = true;
        _cancellation.Cancel();
        _statusLabel.Text = "Cancelling...  (close again to give up waiting)";
    }

    private static void OpenFfmpegSite()
    {
        try
        {
            Process.Start(new ProcessStartInfo("https://ffmpeg.org/download.html") { UseShellExecute = true });
        }
        catch
        {
            // Not being able to open a browser is not worth an error dialog.
        }
    }

    private static string Megabytes(long bytes) => $"{bytes / 1024.0 / 1024.0:0.#} MB";
}
