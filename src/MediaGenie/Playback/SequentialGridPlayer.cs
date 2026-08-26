using System.Drawing;
using System.Windows.Forms;
using LibVLCSharp.Shared;
using MediaGenie.Controls;
using MediaGenie.Models;

namespace MediaGenie.Playback;

/// <summary>
/// Plays the same grid of tiles as GridPlayer, but one clip at a time in grid order --
/// the next tile only starts once the current one finishes, instead of every tile
/// starting together. A finished tile freezes on its last frame (same per-tile snapshot
/// mechanism as GridPlayer) while tiles not reached yet stay on their placeholder, so a
/// run in progress reads like the grid filling in one tile at a time.
/// </summary>
public sealed class SequentialGridPlayer : IDisposable
{
    /// <summary>How close to the end (ms) we start grabbing last-frame snapshots.</summary>
    private const long SnapshotWindowMs = 900;

    private readonly LibVLC _libVlc;
    private readonly Control _uiContext;
    private readonly string _snapshotDirectory;
    private readonly System.Windows.Forms.Timer _ticker;

    private IReadOnlyList<VideoCellControl> _cells = Array.Empty<VideoCellControl>();
    private List<VideoCellControl> _queue = new();
    private int _queueIndex = -1;
    private TilePlayback? _current;
    private bool _muted;
    private int _volume = 100;
    private bool _disposed;

    public SequentialGridPlayer(LibVLC libVlc, Control uiContext)
    {
        _libVlc = libVlc;
        _uiContext = uiContext;

        _snapshotDirectory = Path.Combine(Path.GetTempPath(), "MediaGenie", "frames-sequential");
        Directory.CreateDirectory(_snapshotDirectory);

        _ticker = new System.Windows.Forms.Timer { Interval = 200 };
        _ticker.Tick += OnTick;
    }

    /// <summary>Raised as a tile starts.</summary>
    public event EventHandler<int>? ClipStarted;

    /// <summary>Raised when a tile reaches the end of its clip and freezes.</summary>
    public event EventHandler<int>? ClipFinished;

    public event EventHandler<PlaybackProgressEventArgs>? ProgressChanged;

    /// <summary>Raised when the last tile in grid order finishes.</summary>
    public event EventHandler? SequenceCompleted;

    public event EventHandler<string>? Failed;

    public bool IsRunning { get; private set; }

    public bool IsPaused { get; private set; }

    /// <summary>Master volume (0-100) applied to whichever tile is currently playing.</summary>
    public int Volume => _volume;

    /// <summary>Sets the master volume and applies it to the tile currently playing, if any.</summary>
    public void SetVolume(int volume)
    {
        _volume = Math.Clamp(volume, 0, 100);

        if (_current is null)
        {
            return;
        }

        try
        {
            _current.Player.Volume = _volume;
        }
        catch
        {
            // The tile may be mid-teardown; the next tick will pick up the volume.
        }
    }

    /// <summary>Starts the first tile that holds a clip; each one queues the next as it ends.</summary>
    public void Start(IReadOnlyList<VideoCellControl> cells, bool muted)
    {
        Stop();

        _cells = cells;
        _muted = muted;

        foreach (VideoCellControl cell in _cells)
        {
            cell.Slot.Rewind();

            // Only the frozen last frame from a previous run is cleared -- a poster
            // thumbnail loaded while the tile was waiting its turn stays valid regardless of
            // Play/Stop, since it's just a still of the assigned clip.
            cell.ClearPlaybackFrame();
        }

        _queue = _cells.Where(c => c.Slot.HasClip).ToList();

        if (_queue.Count == 0)
        {
            Failed?.Invoke(this, "Add at least one video before playing.");
            return;
        }

        IsRunning = true;
        IsPaused = false;
        _queueIndex = -1;

        AdvanceQueue();
        _ticker.Start();
    }

    public void TogglePause()
    {
        if (!IsRunning || _current is null)
        {
            return;
        }

        IsPaused = !IsPaused;

        try
        {
            _current.Player.SetPause(IsPaused);
        }
        catch
        {
            // The clip may have just finished on its own.
        }
    }

    /// <summary>Stops the current tile and returns every tile to its placeholder.</summary>
    public void Stop()
    {
        _ticker.Stop();
        IsRunning = false;
        IsPaused = false;

        if (_current is not null)
        {
            Release(_current);
            _current = null;
        }

        foreach (VideoCellControl cell in _cells)
        {
            cell.Slot.Rewind();

            // Only the frozen last frame from a previous run is cleared -- a poster
            // thumbnail loaded while the tile was waiting its turn stays valid regardless of
            // Play/Stop, since it's just a still of the assigned clip.
            cell.ClearPlaybackFrame();
        }

        _queue.Clear();
        _queueIndex = -1;

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

        if (_current is not null)
        {
            Release(_current);
            _current = null;
        }
    }

    private void AdvanceQueue()
    {
        _queueIndex++;

        if (_queueIndex >= _queue.Count)
        {
            _ticker.Stop();
            IsRunning = false;
            double total = TotalSeconds();
            ProgressChanged?.Invoke(this, new PlaybackProgressEventArgs(total, total, 0, FinishedCount()));
            SequenceCompleted?.Invoke(this, EventArgs.Empty);
            return;
        }

        VideoCellControl cell = _queue[_queueIndex];
        TilePlayback? playback = TryStartTile(cell);

        if (playback is not null)
        {
            _current = playback;
            ClipStarted?.Invoke(this, cell.Slot.Index);
        }
        else
        {
            // TryStartTile already reported the failure; move on to the next tile.
            AdvanceQueue();
        }
    }

    private TilePlayback? TryStartTile(VideoCellControl cell)
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

            // Setting Mute right after Play() is unreliable, because the audio output does
            // not exist yet at that point. A media option is applied before playback starts.
            using var media = _muted
                ? new Media(_libVlc, cell.Slot.FilePath!, FromType.FromPath, ":no-audio")
                : new Media(_libVlc, cell.Slot.FilePath!, FromType.FromPath);

            player.Play(media);

            try
            {
                player.Volume = _volume;
            }
            catch
            {
                // Same timing quirk as Mute above; OnTick re-applies it every tick until it sticks.
            }

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

    /// <summary>Freezes the tile that just finished, then starts the next one in the queue.</summary>
    private void FinishTile(TilePlayback playback, bool captureFrame)
    {
        if (_current != playback)
        {
            return;
        }

        Release(playback);
        _current = null;

        VideoCellControl cell = playback.Cell;
        cell.Slot.State = CellState.Finished;
        cell.FreezeOn(captureFrame ? LoadImageCopy(playback.SnapshotPath) : null);
        ClipFinished?.Invoke(this, cell.Slot.Index);

        if (IsRunning)
        {
            AdvanceQueue();
        }
    }

    private void OnTick(object? sender, EventArgs e)
    {
        if (_current is null)
        {
            return;
        }

        MediaPlayer player = _current.Player;
        long length;
        long time;

        try
        {
            length = player.Length;
            time = player.Time;
        }
        catch
        {
            return;
        }

        if (player.Volume != _volume)
        {
            try
            {
                player.Volume = _volume;
            }
            catch
            {
                // Will retry on the next tick.
            }
        }

        // Keep overwriting the snapshot as the end approaches; the last one written before
        // EndReached is the frame this tile will hold.
        if (length > 0 && time > 0 && length - time <= SnapshotWindowMs)
        {
            try
            {
                player.TakeSnapshot(0, _current.SnapshotPath, 0, 0);
            }
            catch
            {
                // Snapshots can fail while the decoder shuts down; the tile then simply
                // falls back to its placeholder.
            }
        }

        double elapsed = ElapsedBeforeCurrent() + Math.Max(0, time / 1000.0);
        ProgressChanged?.Invoke(this, new PlaybackProgressEventArgs(elapsed, TotalSeconds(), 1, FinishedCount()));
    }

    /// <summary>Durations of every clip that has already played, so progress covers the whole run.</summary>
    private double ElapsedBeforeCurrent() =>
        _queueIndex > 0 ? _queue.Take(_queueIndex).Sum(c => c.Slot.DurationSeconds) : 0;

    /// <summary>The run lasts as long as every clip's duration added together.</summary>
    private double TotalSeconds() => _queue.Sum(c => c.Slot.DurationSeconds);

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

    /// <summary>The one tile currently playing, plus the bits needed to unhook and freeze it later.</summary>
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
