using System.Drawing;
using System.Windows.Forms;
using LibVLCSharp.Shared;
using VideoGridStudio.Controls;
using VideoGridStudio.Models;

namespace VideoGridStudio.Playback;

/// <summary>
/// Plays every tile at the same time, the way a video call shows all the participants
/// at once. Each tile gets its own player. When a clip reaches its end, that tile keeps
/// showing its last frame while the longer clips carry on, and the run is over once the
/// last clip finishes.
/// </summary>
public sealed class GridPlayer : IDisposable
{
    /// <summary>How close to the end (ms) we start grabbing last-frame snapshots.</summary>
    private const long SnapshotWindowMs = 900;

    private readonly LibVLC _libVlc;
    private readonly Control _uiContext;
    private readonly string _snapshotDirectory;
    private readonly System.Windows.Forms.Timer _ticker;
    private readonly List<TilePlayback> _active = new();

    private IReadOnlyList<VideoCellControl> _cells = Array.Empty<VideoCellControl>();
    private bool _disposed;

    public GridPlayer(LibVLC libVlc, Control uiContext)
    {
        _libVlc = libVlc;
        _uiContext = uiContext;

        _snapshotDirectory = Path.Combine(Path.GetTempPath(), "VideoGridStudio", "frames");
        Directory.CreateDirectory(_snapshotDirectory);

        _ticker = new System.Windows.Forms.Timer { Interval = 200 };
        _ticker.Tick += OnTick;
    }

    /// <summary>Raised once per tile, as that tile starts.</summary>
    public event EventHandler<int>? ClipStarted;

    /// <summary>Raised when a tile reaches the end of its clip and freezes.</summary>
    public event EventHandler<int>? ClipFinished;

    public event EventHandler<PlaybackProgressEventArgs>? ProgressChanged;

    /// <summary>Raised when the last still-running tile finishes.</summary>
    public event EventHandler? SequenceCompleted;

    public event EventHandler<string>? Failed;

    public bool IsRunning { get; private set; }

    public bool IsPaused { get; private set; }

    /// <summary>Starts every tile that holds a clip.</summary>
    public void Start(IReadOnlyList<VideoCellControl> cells, AudioMode audioMode, int audioTileIndex)
    {
        Stop();

        _cells = cells;

        foreach (VideoCellControl cell in _cells)
        {
            cell.Slot.Rewind();
            cell.ResetSurface();
        }

        List<VideoCellControl> withClips = _cells.Where(c => c.Slot.HasClip).ToList();

        if (withClips.Count == 0)
        {
            Failed?.Invoke(this, "Add at least one video before playing.");
            return;
        }

        IsRunning = true;
        IsPaused = false;

        foreach (VideoCellControl cell in withClips)
        {
            TilePlayback? playback = TryStartTile(cell, audioMode, audioTileIndex);

            if (playback is not null)
            {
                _active.Add(playback);
                ClipStarted?.Invoke(this, cell.Slot.Index);
            }
        }

        if (_active.Count == 0)
        {
            IsRunning = false;
            return;
        }

        _ticker.Start();
    }

    /// <summary>Pauses or resumes every tile together.</summary>
    public void TogglePause()
    {
        if (!IsRunning)
        {
            return;
        }

        IsPaused = !IsPaused;

        foreach (TilePlayback playback in _active)
        {
            try
            {
                playback.Player.SetPause(IsPaused);
            }
            catch
            {
                // A tile that has just finished may already be gone.
            }
        }
    }

    /// <summary>Stops everything and returns every tile to its placeholder.</summary>
    public void Stop()
    {
        _ticker.Stop();
        IsRunning = false;
        IsPaused = false;

        foreach (TilePlayback playback in _active.ToList())
        {
            Release(playback);
        }

        _active.Clear();

        foreach (VideoCellControl cell in _cells)
        {
            cell.Slot.Rewind();
            cell.ResetSurface();
        }

        // The grid may be rebuilt (and these controls disposed) before the next Start.
        _cells = Array.Empty<VideoCellControl>();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _ticker.Stop();
        _ticker.Dispose();

        foreach (TilePlayback playback in _active.ToList())
        {
            Release(playback);
        }

        _active.Clear();
    }

    private TilePlayback? TryStartTile(VideoCellControl cell, AudioMode audioMode, int audioTileIndex)
    {
        string snapshotPath = Path.Combine(_snapshotDirectory, $"cell_{cell.Slot.Index:D2}.png");
        TryDelete(snapshotPath);

        try
        {
            var player = new MediaPlayer(_libVlc)
            {
                EnableHardwareDecoding = true,
                EnableMouseInput = false,
                EnableKeyInput = false
            };

            var playback = new TilePlayback(cell, player, snapshotPath);
            playback.EndHandler = (_, _) => PostToUi(() => FinishTile(playback, captureFrame: true));
            playback.ErrorHandler = (_, _) => PostToUi(() =>
            {
                Failed?.Invoke(this, $"Playback error on {cell.Slot.DisplayName}.");
                FinishTile(playback, captureFrame: false);
            });

            player.EndReached += playback.EndHandler;
            player.EncounteredError += playback.ErrorHandler;

            cell.Slot.State = CellState.Playing;
            cell.ShowVideo(player);

            bool mute = audioMode switch
            {
                AudioMode.MixAll => false,
                AudioMode.SingleTile => cell.Slot.Index != audioTileIndex,
                _ => true
            };

            // Setting Mute right after Play() is unreliable, because the audio output does
            // not exist yet at that point. A media option is applied before playback starts.
            using var media = mute
                ? new Media(_libVlc, cell.Slot.FilePath!, FromType.FromPath, ":no-audio")
                : new Media(_libVlc, cell.Slot.FilePath!, FromType.FromPath);

            player.Play(media);

            return playback;
        }
        catch (Exception ex)
        {
            Failed?.Invoke(this, $"Could not play {cell.Slot.DisplayName}: {ex.Message}");
            cell.Slot.State = CellState.Finished;
            cell.FreezeOn(null);
            return null;
        }
    }

    /// <summary>Freezes one tile on its last frame; ends the run if it was the last one going.</summary>
    private void FinishTile(TilePlayback playback, bool captureFrame)
    {
        if (!_active.Remove(playback))
        {
            return;
        }

        Release(playback);

        VideoCellControl cell = playback.Cell;
        cell.Slot.State = CellState.Finished;
        cell.FreezeOn(captureFrame ? LoadImageCopy(playback.SnapshotPath) : null);
        ClipFinished?.Invoke(this, cell.Slot.Index);

        if (_active.Count == 0 && IsRunning)
        {
            _ticker.Stop();
            IsRunning = false;
            double total = TotalSeconds();
            ProgressChanged?.Invoke(this, new PlaybackProgressEventArgs(total, total, 0, FinishedCount()));
            SequenceCompleted?.Invoke(this, EventArgs.Empty);
        }
    }

    private void OnTick(object? sender, EventArgs e)
    {
        double elapsed = 0;

        foreach (TilePlayback playback in _active.ToList())
        {
            MediaPlayer player = playback.Player;

            long length;
            long time;

            try
            {
                length = player.Length;
                time = player.Time;
            }
            catch
            {
                continue;
            }

            if (time > 0)
            {
                elapsed = Math.Max(elapsed, time / 1000.0);
            }

            // Keep overwriting the snapshot as the end approaches; the last one written
            // before EndReached is the frame this tile will hold.
            if (length > 0 && time > 0 && length - time <= SnapshotWindowMs)
            {
                try
                {
                    player.TakeSnapshot(0, playback.SnapshotPath, 0, 0);
                }
                catch
                {
                    // Snapshots can fail while the decoder shuts down; the tile then
                    // simply falls back to its placeholder.
                }
            }
        }

        foreach (VideoCellControl cell in _cells.Where(c => c.Slot.State == CellState.Finished))
        {
            elapsed = Math.Max(elapsed, cell.Slot.DurationSeconds);
        }

        ProgressChanged?.Invoke(this, new PlaybackProgressEventArgs(
            elapsed,
            TotalSeconds(),
            _active.Count,
            FinishedCount()));
    }

    /// <summary>The run lasts as long as the longest clip, because everything starts together.</summary>
    private double TotalSeconds()
    {
        IEnumerable<double> durations = _cells.Where(c => c.Slot.HasClip).Select(c => c.Slot.DurationSeconds);
        return durations.Any() ? durations.Max() : 0;
    }

    private int FinishedCount() => _cells.Count(c => c.Slot.State == CellState.Finished);

    private void Release(TilePlayback playback)
    {
        MediaPlayer player = playback.Player;

        if (playback.EndHandler is not null)
        {
            player.EndReached -= playback.EndHandler;
        }

        if (playback.ErrorHandler is not null)
        {
            player.EncounteredError -= playback.ErrorHandler;
        }

        playback.Cell.DetachPlayer();

        // Tearing a player down can block for a moment, so keep it off the UI thread.
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

    private void PostToUi(Action action)
    {
        if (_disposed || _uiContext.IsDisposed || !_uiContext.IsHandleCreated)
        {
            return;
        }

        try
        {
            _uiContext.BeginInvoke(action);
        }
        catch (ObjectDisposedException)
        {
            // The window closed while VLC was still talking to us.
        }
        catch (InvalidOperationException)
        {
            // The handle went away between the check and the call.
        }
    }

    /// <summary>Reads the PNG into memory so the file on disk is not left locked.</summary>
    private static Image? LoadImageCopy(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return null;
        }

        for (int attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                if (File.Exists(path))
                {
                    byte[] bytes = File.ReadAllBytes(path);
                    using var stream = new MemoryStream(bytes);
                    using Image decoded = Image.FromStream(stream);

                    // GDI+ keeps reading from the stream behind an Image, so hand back a
                    // standalone copy instead of one tied to a stream we are about to close.
                    return new Bitmap(decoded);
                }
            }
            catch (IOException)
            {
                // VLC may still be writing the file; give it a moment.
            }
            catch (Exception)
            {
                return null;
            }

            Thread.Sleep(60);
        }

        return null;
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
            // Leftover snapshots are harmless.
        }
    }

    /// <summary>One tile's player plus the bits needed to unhook and freeze it later.</summary>
    private sealed class TilePlayback
    {
        public TilePlayback(VideoCellControl cell, MediaPlayer player, string snapshotPath)
        {
            Cell = cell;
            Player = player;
            SnapshotPath = snapshotPath;
        }

        public VideoCellControl Cell { get; }

        public MediaPlayer Player { get; }

        public string SnapshotPath { get; }

        public EventHandler<EventArgs>? EndHandler { get; set; }

        public EventHandler<EventArgs>? ErrorHandler { get; set; }
    }
}
