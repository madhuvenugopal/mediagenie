using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using LibVLCSharp.Shared;
using Microsoft.Win32;
using MkvPlayer.Audio;
using MkvPlayer.Equalizer;
using MkvPlayer.Settings;
using NAudio.Gui;

namespace MkvPlayer;

public partial class MainWindow : Window
{
    private static readonly string[] VideoExtensions = { ".mkv", ".mp4" };
    private static readonly string[] AudioExtensions = { ".mp3", ".wav", ".wma", ".flac", ".aac", ".ogg" };

    private const string PlayGlyph = "â–¶";  // â–¶
    private const string PauseGlyph = "â€–"; // â€–

    private enum OscilloscopeMode { Fire, Spectrum, Off }

    // ----- Video (LibVLC) -----
    private LibVLC? _libVLC;
    private MediaPlayer? _mediaPlayer;
    private MediaItem? _currentItem;

    // ----- Audio (NAudio) -----
    private readonly AudioEngine _audioEngine = new();
    private List<string> _audioQueue = new();
    private int _audioQueueIndex = -1;

    // ----- Voice recording (NAudio) -----
    private readonly Recorder _recorder = new();
    private readonly DispatcherTimer _recordingTimer;

    // ----- Shared -----
    private EqualizerSettings _equalizerSettings = EqualizerSettings.Flat();
    private readonly DispatcherTimer _positionTimer;
    private bool _isSeeking;
    private bool _suppressVolumeEvent;
    private double _volumeBeforeMute = 100;

    // Default is Fire -- matches the controls' own default Visibility in XAML (Oscilloscope
    // visible, OscilloscopeSpectrum/OscilloscopeOffPanel collapsed).
    private OscilloscopeMode _oscilloscopeMode = OscilloscopeMode.Fire;

    private bool _isFullscreen;
    private WindowState _previousWindowState;
    private WindowStyle _previousWindowStyle;
    private ResizeMode _previousResizeMode;
    private double _previousLeft;
    private double _previousTop;
    private double _previousWidth;
    private double _previousHeight;
    private double _previousFileListWidth = 260;

    public ObservableCollection<MediaItem> MediaFiles { get; } = new();
    public ObservableCollection<MediaItem> AudioFiles { get; } = new();

    public MainWindow()
    {
        InitializeComponent();
        DataContext = this;

        _positionTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        _positionTimer.Tick += PositionTimer_Tick;

        _recordingTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _recordingTimer.Tick += (_, _) => RecordingTimeLabel.Text = FormatTime((long)_recorder.Elapsed.TotalMilliseconds);

        // Both visualizations get fed regardless of which is currently shown, so switching
        // between them (see OscilloscopeView_MouseLeftButtonDown) is instant -- the hidden one
        // already has live data waiting rather than needing a moment to catch up.
        _audioEngine.SamplesAvailable += (buffer, offset, count) =>
        {
            Oscilloscope.PushSamples(buffer, offset, count);
            OscilloscopeSpectrum.PushSamples(buffer, offset, count);
        };
        _audioEngine.TrackEnded += AudioEngine_TrackEnded;

        _recorder.RecordingStopped += Recorder_RecordingStopped;
        _recorder.RecordingFailed += Recorder_RecordingFailed;

        Loaded += MainWindow_Loaded;
        Closed += MainWindow_Closed;
    }

    private void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        Core.Initialize();

        _libVLC = new LibVLC();
        _mediaPlayer = new MediaPlayer(_libVLC);
        VideoViewControl.MediaPlayer = _mediaPlayer;

        _mediaPlayer.EndReached += (_, _) => Dispatcher.BeginInvoke(() =>
        {
            PlayPauseButton.Content = PlayGlyph;
            SeekSlider.Value = 0;
        });

        LoadSettings();
        ApplyEqualizerLive(_equalizerSettings);

        _positionTimer.Start();
        UpdateTransportBarForActiveTab();
    }

    private void MainWindow_Closed(object? sender, EventArgs e)
    {
        SaveSettings();

        _positionTimer.Stop();
        _recordingTimer.Stop();

        _mediaPlayer?.Stop();
        _mediaPlayer?.Dispose();
        _libVLC?.Dispose();

        _audioEngine.Dispose();
        _recorder.Dispose();
    }

    // ----- Settings (registry) -----

    private void LoadSettings()
    {
        // A saved BandGainsDb may be shorter than today's band count (e.g. a 5-band value from
        // before the equalizer grew to 7 bands) -- copy into a correctly-sized array instead of
        // using the loaded one directly, so every band the app now expects to index safely
        // exists, with any newly-added bands defaulting to 0 dB.
        var savedBands = SettingsService.GetUserDoubleArray(
            SettingsService.Keys.EqualizerBands,
            new double[EqualizerSettings.BandCenterFrequencies.Length]);
        var bandGains = new double[EqualizerSettings.BandCenterFrequencies.Length];
        Array.Copy(savedBands, bandGains, Math.Min(savedBands.Length, bandGains.Length));

        _equalizerSettings = new EqualizerSettings
        {
            Enabled = SettingsService.GetUserBool(SettingsService.Keys.EqualizerEnabled, true),
            PreampDb = SettingsService.GetUserDouble(SettingsService.Keys.EqualizerPreamp, 0),
            BandGainsDb = bandGains,
        };

        var volume = SettingsService.GetUserDouble(SettingsService.Keys.Volume, 100);
        VolumeSlider.Value = volume;
        _audioEngine.Volume = (float)(volume / 100.0);
        if (_mediaPlayer != null) _mediaPlayer.Volume = (int)volume;

        var vocalLevel = SettingsService.GetUserDouble(SettingsService.Keys.VocalLevel, 100);
        KaraokeSlider.Value = vocalLevel;
        _audioEngine.VocalLevel = (float)(vocalLevel / 100.0);

        var width = SettingsService.GetUserDouble(SettingsService.Keys.WindowWidth, Width);
        var height = SettingsService.GetUserDouble(SettingsService.Keys.WindowHeight, Height);
        if (width >= MinWidth && height >= MinHeight)
        {
            Width = width;
            Height = height;
        }
    }

    private void SaveSettings()
    {
        SettingsService.SetUserBool(SettingsService.Keys.EqualizerEnabled, _equalizerSettings.Enabled);
        SettingsService.SetUserDouble(SettingsService.Keys.EqualizerPreamp, _equalizerSettings.PreampDb);
        SettingsService.SetUserDoubleArray(SettingsService.Keys.EqualizerBands, _equalizerSettings.BandGainsDb);
        SettingsService.SetUserDouble(SettingsService.Keys.Volume, VolumeSlider.Value);
        SettingsService.SetUserDouble(SettingsService.Keys.VocalLevel, KaraokeSlider.Value);

        if (WindowState == WindowState.Normal)
        {
            SettingsService.SetUserDouble(SettingsService.Keys.WindowWidth, Width);
            SettingsService.SetUserDouble(SettingsService.Keys.WindowHeight, Height);
        }
    }

    // ----- Tab switching -----

    private void MainTabControl_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // TabControl selects its first tab as soon as its Items are populated, which happens
        // while InitializeComponent() is still running -- i.e. before the rest of the window
        // (and possibly MainTabControl's own field) is fully wired up. MainWindow_Loaded
        // already calls UpdateTransportBarForActiveTab() once everything is ready, so it's
        // safe to just ignore anything that fires before that.
        if (!IsLoaded) return;

        // SelectionChanged bubbles up from any Selector (e.g. the file ListBoxes), so ignore
        // anything that didn't originate from the TabControl itself.
        if (e.Source != MainTabControl) return;

        UpdateTransportBarForActiveTab();

        if (MainTabControl.SelectedItem is TabItem { Content: UIElement content })
        {
            content.Opacity = 0;
            content.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(200)));
        }
    }

    // Null-conditional as a backstop: falls back to the Video tab (index 0) if this is ever
    // evaluated before MainTabControl is assigned (see the IsLoaded guard above).
    private int ActiveTabIndex => MainTabControl?.SelectedIndex ?? 0;

    private void UpdateTransportBarForActiveTab()
    {
        var isVoiceRecordTab = ActiveTabIndex == 2;
        TransportBar.Visibility = isVoiceRecordTab ? Visibility.Collapsed : Visibility.Visible;
        FullscreenButton.Visibility = ActiveTabIndex == 0 ? Visibility.Visible : Visibility.Collapsed;

        // The karaoke effect only applies to the NAudio-driven Audio tab's sample chain --
        // there's nothing for it to act on while the Video tab's LibVLC engine is active.
        var karaokeVisibility = ActiveTabIndex == 1 ? Visibility.Visible : Visibility.Collapsed;
        KaraokeLabel.Visibility = karaokeVisibility;
        KaraokeSlider.Visibility = karaokeVisibility;

        _suppressVolumeEvent = true;
        VolumeSlider.Value = ActiveTabIndex == 1 ? _audioEngine.Volume * 100 : _mediaPlayer?.Volume ?? 100;
        VolumeLabel.Text = $"Vol {(int)VolumeSlider.Value}%";
        UpdateMuteButtonIcon(VolumeSlider.Value <= 0);
        _suppressVolumeEvent = false;

        RefreshTransportDisplay();
    }

    private void RefreshTransportDisplay()
    {
        if (ActiveTabIndex == 1)
        {
            PlayPauseButton.Content = _audioEngine.IsPlaying ? PauseGlyph : PlayGlyph;
        }
        else if (ActiveTabIndex == 0)
        {
            PlayPauseButton.Content = (_mediaPlayer?.IsPlaying ?? false) ? PauseGlyph : PlayGlyph;
        }
    }

    // ----- Video: file list -----

    private void AddFilesButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Select video files",
            Filter = "Video Files (*.mkv;*.mp4)|*.mkv;*.mp4|MKV Files (*.mkv)|*.mkv|MP4 Files (*.mp4)|*.mp4|All Files (*.*)|*.*",
            Multiselect = true
        };

        if (dialog.ShowDialog() == true)
        {
            AddVideoFiles(dialog.FileNames);
        }
    }

    private void AddVideoFiles(IEnumerable<string> paths)
    {
        MediaItem? firstAdded = null;

        foreach (var path in paths)
        {
            var extension = Path.GetExtension(path).ToLowerInvariant();
            if (!VideoExtensions.Contains(extension)) continue;
            if (MediaFiles.Any(m => string.Equals(m.FullPath, path, StringComparison.OrdinalIgnoreCase))) continue;

            var item = new MediaItem(path);
            MediaFiles.Add(item);
            firstAdded ??= item;
        }

        if (_currentItem == null && firstAdded != null)
        {
            FileListBox.SelectedItem = firstAdded;
        }
    }

    private void RemoveFileButton_Click(object sender, RoutedEventArgs e)
    {
        var selected = FileListBox.SelectedItems.Cast<MediaItem>().ToList();
        foreach (var item in selected)
        {
            if (ReferenceEquals(item, _currentItem))
            {
                _mediaPlayer?.Stop();
                _currentItem = null;
                VideoPlaceholderOverlay.Visibility = Visibility.Visible;
                NowPlayingLabel.Text = "No file loaded";
                RefreshTransportDisplay();
            }
            MediaFiles.Remove(item);
        }
    }

    private void ClearAllFilesButton_Click(object sender, RoutedEventArgs e)
    {
        if (_currentItem != null)
        {
            _mediaPlayer?.Stop();
            _currentItem = null;
            VideoPlaceholderOverlay.Visibility = Visibility.Visible;
            NowPlayingLabel.Text = "No file loaded";
            RefreshTransportDisplay();
        }
        MediaFiles.Clear();
    }

    private void FileListBox_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop) && e.Data.GetData(DataFormats.FileDrop) is string[] files)
        {
            AddVideoFiles(files);
        }
    }

    private void FileListBox_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (FileListBox.SelectedItem is MediaItem item)
        {
            PlayVideo(item);
        }
    }

    // ----- Video: playback -----

    private void PlayVideo(MediaItem item)
    {
        if (_libVLC == null || _mediaPlayer == null) return;

        // The two engines share one speaker output -- starting one always stops the other so
        // they never play over each other.
        StopAudioPlayback();

        _currentItem = item;
        using var media = new Media(_libVLC, new Uri(item.FullPath));
        _mediaPlayer.Play(media);

        VideoPlaceholderOverlay.Visibility = Visibility.Collapsed;
        NowPlayingLabel.Text = item.DisplayName;
        RefreshTransportDisplay();
    }

    private void VideoViewControl_MouseDoubleClick(object sender, MouseButtonEventArgs e) => ToggleFullscreen();

    // ----- Audio: file list -----

    private void AddAudioFilesButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Select audio files",
            Filter = "Audio Files (*.mp3;*.wav;*.wma;*.flac;*.aac;*.ogg)|*.mp3;*.wav;*.wma;*.flac;*.aac;*.ogg|All Files (*.*)|*.*",
            Multiselect = true
        };

        if (dialog.ShowDialog() == true)
        {
            AddAudioFiles(dialog.FileNames);
        }
    }

    private void AddAudioFolderButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Select a folder of audio files" };
        if (dialog.ShowDialog() != true) return;

        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(dialog.FolderName, "*.*", SearchOption.AllDirectories)
                .Where(f => AudioExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()));
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Couldn't read that folder: {ex.Message}", "Add Folder", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        AddAudioFiles(files);
    }

    private void AddAudioFiles(IEnumerable<string> paths)
    {
        MediaItem? firstAdded = null;

        foreach (var path in paths)
        {
            var extension = Path.GetExtension(path).ToLowerInvariant();
            if (!AudioExtensions.Contains(extension)) continue;
            if (AudioFiles.Any(m => string.Equals(m.FullPath, path, StringComparison.OrdinalIgnoreCase))) continue;

            var item = new MediaItem(path);
            AudioFiles.Add(item);
            firstAdded ??= item;
        }

        if (_audioEngine.CurrentFilePath == null && firstAdded != null)
        {
            AudioFileListBox.SelectedItem = firstAdded;
        }
    }

    private void RemoveAudioFileButton_Click(object sender, RoutedEventArgs e)
    {
        var selected = AudioFileListBox.SelectedItems.Cast<MediaItem>().ToList();
        foreach (var item in selected)
        {
            if (string.Equals(item.FullPath, _audioEngine.CurrentFilePath, StringComparison.OrdinalIgnoreCase))
            {
                _audioEngine.Stop();
                ClearOscilloscopes();
                NowPlayingLabel.Text = "No file loaded";
                RefreshTransportDisplay();
            }
            AudioFiles.Remove(item);
        }
    }

    private void ClearAllAudioFilesButton_Click(object sender, RoutedEventArgs e)
    {
        if (_audioEngine.CurrentFilePath != null)
        {
            _audioEngine.Stop();
            ClearOscilloscopes();
            NowPlayingLabel.Text = "No file loaded";
            RefreshTransportDisplay();
        }
        _audioQueue = new List<string>();
        _audioQueueIndex = -1;
        AudioFiles.Clear();
    }

    private void AudioFileListBox_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop) && e.Data.GetData(DataFormats.FileDrop) is string[] files)
        {
            AddAudioFiles(files);
        }
    }

    private void AudioFileListBox_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (AudioFileListBox.SelectedItem is MediaItem item)
        {
            // Queue the whole visible list (starting at the double-clicked track), not just
            // that one file -- otherwise Next/Prev have nothing to step to, since they only
            // know how to walk _audioQueue. This mirrors how the Video tab's Next/Prev already
            // treat its file list as an implicit playlist regardless of how playback started.
            var index = AudioFiles.IndexOf(item);
            PlayAudioQueue(AudioFiles.Select(i => i.FullPath).ToList(), Math.Max(index, 0));
        }
    }

    private void PlaySelectedAudioButton_Click(object sender, RoutedEventArgs e)
    {
        var selected = AudioFileListBox.SelectedItems.Cast<MediaItem>().ToList();
        var items = selected.Count > 0 ? selected : AudioFiles.ToList();
        if (items.Count == 0) return;

        PlayAudioQueue(items.Select(i => i.FullPath).ToList(), 0);
    }

    // ----- Audio: playlists -----

    private void CreatePlaylistButton_Click(object sender, RoutedEventArgs e)
    {
        var selected = AudioFileListBox.SelectedItems.Cast<MediaItem>().ToList();
        var items = selected.Count > 0 ? selected : AudioFiles.ToList();
        if (items.Count == 0)
        {
            MessageBox.Show("Add some audio files first.", "Create Playlist", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var dialog = new SaveFileDialog
        {
            Title = "Save playlist",
            Filter = "Playlist (*.m3u)|*.m3u",
            FileName = "Playlist.m3u",
        };
        if (dialog.ShowDialog() != true) return;

        PlaylistManager.Save(items, dialog.FileName);

        // The playlist you just built becomes the active queue and starts playing immediately.
        PlayAudioQueue(items.Select(i => i.FullPath).ToList(), 0);
    }

    private void LoadPlaylistButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "Open playlist", Filter = "Playlist (*.m3u)|*.m3u" };
        if (dialog.ShowDialog() != true) return;

        var paths = PlaylistManager.Load(dialog.FileName);
        if (paths.Count == 0)
        {
            MessageBox.Show("That playlist has no valid files.", "Load Playlist", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        AddAudioFiles(paths);
        PlayAudioQueue(paths, 0);
    }

    private void PlayAudioQueue(List<string> paths, int startIndex)
    {
        _audioQueue = paths;
        _audioQueueIndex = startIndex;
        PlayCurrentAudioQueueItem();
    }

    private void PlayCurrentAudioQueueItem()
    {
        if (_audioQueueIndex < 0 || _audioQueueIndex >= _audioQueue.Count) return;

        // The two engines share one speaker output -- starting one always stops the other so
        // they never play over each other.
        StopVideoPlayback();

        var path = _audioQueue[_audioQueueIndex];
        try
        {
            _audioEngine.Play(path, _equalizerSettings);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Couldn't play '{Path.GetFileName(path)}': {ex.Message}", "Playback error", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var item = AudioFiles.FirstOrDefault(m => string.Equals(m.FullPath, path, StringComparison.OrdinalIgnoreCase));
        NowPlayingLabel.Text = item?.DisplayName ?? Path.GetFileName(path);
        AudioFileListBox.SelectedItem = item;
        RefreshTransportDisplay();
    }

    private void AudioEngine_TrackEnded(object? sender, EventArgs e)
    {
        Dispatcher.BeginInvoke(() =>
        {
            _audioQueueIndex++;
            if (_audioQueueIndex < _audioQueue.Count)
            {
                PlayCurrentAudioQueueItem();
            }
            else
            {
                SeekSlider.Value = 0;
                ClearOscilloscopes();
                RefreshTransportDisplay();
            }
        });
    }

    // ----- Shared transport bar -----

    private void PlayPauseButton_Click(object sender, RoutedEventArgs e)
    {
        if (ActiveTabIndex == 1)
        {
            AudioPlayPause();
        }
        else
        {
            VideoPlayPause();
        }
    }

    private void VideoPlayPause()
    {
        if (_mediaPlayer == null) return;

        if (_currentItem == null)
        {
            var toPlay = FileListBox.SelectedItem as MediaItem ?? MediaFiles.FirstOrDefault();
            if (toPlay != null)
            {
                FileListBox.SelectedItem = toPlay;
                PlayVideo(toPlay);
            }
            return;
        }

        if (_mediaPlayer.IsPlaying)
        {
            _mediaPlayer.Pause();
        }
        else
        {
            // Resuming from pause -- PlayVideo() isn't in this path, so stop audio here too
            // (see the comment there) in case audio was started while this video sat paused.
            StopAudioPlayback();
            _mediaPlayer.Play();
        }
        RefreshTransportDisplay();
    }

    private void AudioPlayPause()
    {
        if (_audioEngine.CurrentFilePath == null)
        {
            var selected = AudioFileListBox.SelectedItems.Cast<MediaItem>().ToList();
            var items = selected.Count > 0 ? selected : AudioFiles.ToList();
            if (items.Count > 0)
            {
                PlayAudioQueue(items.Select(i => i.FullPath).ToList(), 0);
            }
            return;
        }

        if (_audioEngine.IsPlaying)
        {
            _audioEngine.Pause();
        }
        else
        {
            // Resuming from pause -- PlayCurrentAudioQueueItem() isn't in this path, so stop
            // video here too (see the comment there) in case video was started while this
            // track sat paused.
            StopVideoPlayback();
            _audioEngine.Resume();
        }
        RefreshTransportDisplay();
    }

    private void PrevButton_Click(object sender, RoutedEventArgs e)
    {
        if (ActiveTabIndex == 1)
        {
            AudioPrev();
        }
        else
        {
            VideoPrev();
        }
    }

    private void NextButton_Click(object sender, RoutedEventArgs e)
    {
        if (ActiveTabIndex == 1)
        {
            AudioNext();
        }
        else
        {
            VideoNext();
        }
    }

    private void AudioPrev()
    {
        // Mirrors AudioEngine_TrackEnded's forward stepping, just manually triggered and in
        // the other direction. No wraparound -- stepping past either end of the queue is a
        // silent no-op, same as Back10/Fwd10 clamping at the track's own bounds.
        if (_audioQueueIndex - 1 < 0) return;

        _audioQueueIndex--;
        PlayCurrentAudioQueueItem();
        ShowOsd("Previous");
    }

    private void AudioNext()
    {
        if (_audioQueue.Count == 0)
        {
            // Nothing queued yet -- behave like the Play button does when nothing's loaded:
            // start the selection (or the whole list) as a fresh queue.
            var selected = AudioFileListBox.SelectedItems.Cast<MediaItem>().ToList();
            var items = selected.Count > 0 ? selected : AudioFiles.ToList();
            if (items.Count == 0) return;

            PlayAudioQueue(items.Select(i => i.FullPath).ToList(), 0);
            ShowOsd("Next");
            return;
        }

        if (_audioQueueIndex + 1 >= _audioQueue.Count) return;

        _audioQueueIndex++;
        PlayCurrentAudioQueueItem();
        ShowOsd("Next");
    }

    private void VideoPrev()
    {
        var item = GetAdjacentVideoItem(-1);
        if (item == null) return;

        FileListBox.SelectedItem = item;
        PlayVideo(item);
        ShowOsd("Previous");
    }

    private void VideoNext()
    {
        var item = GetAdjacentVideoItem(1);
        if (item == null) return;

        FileListBox.SelectedItem = item;
        PlayVideo(item);
        ShowOsd("Next");
    }

    /// <summary>
    /// The video tab has no explicit queue like the audio tab -- MediaFiles (the playlist
    /// ListBox's own order) doubles as the implicit playlist, walked relative to whatever's
    /// currently playing. Returns null at either end (no wraparound) or when there's nothing
    /// to navigate to yet.
    /// </summary>
    private MediaItem? GetAdjacentVideoItem(int offset)
    {
        if (MediaFiles.Count == 0) return null;

        if (_currentItem == null)
        {
            // Nothing loaded yet: land on the first item either direction, matching
            // VideoPlayPause's "nothing loaded -> just start playing something" fallback.
            return MediaFiles.FirstOrDefault();
        }

        var index = MediaFiles.IndexOf(_currentItem);
        if (index < 0) return null;

        var newIndex = index + offset;
        return newIndex >= 0 && newIndex < MediaFiles.Count ? MediaFiles[newIndex] : null;
    }

    // ----- Cross-engine exclusivity: only one of Video/Audio ever makes sound at a time -----

    /// <summary>Stops the audio engine if it's actively playing. No-op if it's idle or merely paused.</summary>
    private void StopAudioPlayback()
    {
        if (!_audioEngine.IsPlaying) return;
        _audioEngine.Stop();
        ClearOscilloscopes();
    }

    /// <summary>Stops the video engine if it's actively playing. No-op if it's idle or merely paused.</summary>
    private void StopVideoPlayback()
    {
        if (_mediaPlayer == null || !_mediaPlayer.IsPlaying) return;
        _mediaPlayer.Stop();
    }

    private void ClearOscilloscopes()
    {
        Oscilloscope.Clear();
        OscilloscopeSpectrum.Clear();
    }

    // ----- Oscilloscope view switching (fire <-> LED spectrum analyzer) -----

    private void OscilloscopeView_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _oscilloscopeMode = _oscilloscopeMode switch
        {
            OscilloscopeMode.Fire => OscilloscopeMode.Spectrum,
            OscilloscopeMode.Spectrum => OscilloscopeMode.Off,
            _ => OscilloscopeMode.Fire,
        };
        ApplyOscilloscopeMode();
    }

    // Both visualization controls are always fed live samples regardless of mode (see the
    // SamplesAvailable subscription in the constructor), so switching -- including back on from
    // Off -- is just a Visibility swap; whichever one becomes visible already has current data
    // to draw, no catch-up needed.
    private void ApplyOscilloscopeMode()
    {
        Oscilloscope.Visibility = _oscilloscopeMode == OscilloscopeMode.Fire ? Visibility.Visible : Visibility.Collapsed;
        OscilloscopeSpectrum.Visibility = _oscilloscopeMode == OscilloscopeMode.Spectrum ? Visibility.Visible : Visibility.Collapsed;
        OscilloscopeOffPanel.Visibility = _oscilloscopeMode == OscilloscopeMode.Off ? Visibility.Visible : Visibility.Collapsed;
    }

    private void StopButton_Click(object sender, RoutedEventArgs e)
    {
        if (ActiveTabIndex == 1)
        {
            _audioEngine.Stop();
            ClearOscilloscopes();
        }
        else
        {
            _mediaPlayer?.Stop();
            VideoPlaceholderOverlay.Visibility = Visibility.Visible;
        }

        SeekSlider.Value = 0;
        RefreshTransportDisplay();
    }

    private void Back10Button_Click(object sender, RoutedEventArgs e)
    {
        if (ActiveTabIndex == 1)
        {
            _audioEngine.CurrentTime -= TimeSpan.FromSeconds(10);
        }
        else if (_mediaPlayer != null)
        {
            _mediaPlayer.Time = Math.Max(0, _mediaPlayer.Time - 10000);
        }
        ShowOsd("â—€â—€ 10s");
    }

    private void Fwd10Button_Click(object sender, RoutedEventArgs e)
    {
        if (ActiveTabIndex == 1)
        {
            _audioEngine.CurrentTime += TimeSpan.FromSeconds(10);
        }
        else if (_mediaPlayer != null && _mediaPlayer.Length > 0)
        {
            _mediaPlayer.Time = Math.Min(_mediaPlayer.Length, _mediaPlayer.Time + 10000);
        }
        ShowOsd("10s â–¶â–¶");
    }

    private void VolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        // VolumeSlider's XAML sets Value="100", which differs from a Slider's implicit
        // default of 0, so it fires ValueChanged the moment it's constructed during
        // InitializeComponent() -- before the rest of the window is wired up. Nothing useful
        // can happen yet at that point anyway (LibVLC/_mediaPlayer isn't initialized until
        // Loaded), so just ignore it, same as the TabControl's early SelectionChanged.
        if (!IsLoaded) return;
        if (_suppressVolumeEvent) return;

        if (ActiveTabIndex == 1)
        {
            _audioEngine.Volume = (float)(e.NewValue / 100.0);
        }
        else if (_mediaPlayer != null)
        {
            _mediaPlayer.Volume = (int)e.NewValue;
        }

        // Shown as a static label next to the slider rather than the transient OSD -- unlike
        // seek/skip feedback, volume has a persistent control right there to read it from, so
        // it doesn't need to float a fading message over the video like the others do.
        VolumeLabel.Text = $"Vol {(int)e.NewValue}%";
        UpdateMuteButtonIcon(e.NewValue <= 0);
    }

    /// <summary>
    /// Toggles between silence and the last non-zero volume, applying to whichever engine
    /// (video or audio) is behind the currently-active tab -- same tab-scoped convention
    /// VolumeSlider/KaraokeSlider already follow. Driven entirely through VolumeSlider.Value so
    /// VolumeSlider_ValueChanged does the actual engine plumbing; this just decides the target
    /// value and remembers what to restore on unmute.
    /// </summary>
    private void MuteButton_Click(object sender, RoutedEventArgs e)
    {
        if (VolumeSlider.Value > 0)
        {
            _volumeBeforeMute = VolumeSlider.Value;
            VolumeSlider.Value = 0;
        }
        else
        {
            VolumeSlider.Value = _volumeBeforeMute > 0 ? _volumeBeforeMute : 100;
        }
    }

    private void UpdateMuteButtonIcon(bool muted)
    {
        MuteButtonIcon.Text = muted ? "\uE74F" : "\uE767";
        MuteButton.ToolTip = muted ? "Unmute" : "Mute";
    }

    private void KaraokeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        // Same early-firing quirk as VolumeSlider_ValueChanged: this control's XAML sets
        // Value="100", so InitializeComponent() raises ValueChanged before the window (and
        // _audioEngine) is fully wired up.
        if (!IsLoaded) return;

        _audioEngine.VocalLevel = (float)(e.NewValue / 100.0);
        ShowOsd(e.NewValue >= 100 ? "Vocals normal" : $"Vocals {(int)e.NewValue}%");
    }

    private void SeekSlider_PreviewMouseDown(object sender, MouseButtonEventArgs e) => _isSeeking = true;

    private void SeekSlider_PreviewMouseUp(object sender, MouseButtonEventArgs e)
    {
        _isSeeking = false;
        if (SeekSlider.Maximum <= 0) return;

        var ratio = SeekSlider.Value / SeekSlider.Maximum;

        if (ActiveTabIndex == 1)
        {
            if (_audioEngine.TotalTime > TimeSpan.Zero)
            {
                _audioEngine.CurrentTime = TimeSpan.FromMilliseconds(ratio * _audioEngine.TotalTime.TotalMilliseconds);
            }
        }
        else if (_mediaPlayer != null && _mediaPlayer.Length > 0)
        {
            _mediaPlayer.Position = (float)ratio;
        }
    }

    private void PositionTimer_Tick(object? sender, EventArgs e)
    {
        if (ActiveTabIndex == 1)
        {
            if (!_isSeeking && _audioEngine.TotalTime > TimeSpan.Zero)
            {
                var ratio = _audioEngine.CurrentTime.TotalMilliseconds / _audioEngine.TotalTime.TotalMilliseconds;
                SeekSlider.Value = ratio * SeekSlider.Maximum;
            }
            TimeLabel.Text = $"{FormatTime((long)_audioEngine.CurrentTime.TotalMilliseconds)} / {FormatTime((long)_audioEngine.TotalTime.TotalMilliseconds)}";
        }
        else if (ActiveTabIndex == 0 && _mediaPlayer != null)
        {
            if (!_isSeeking && _mediaPlayer.Length > 0)
            {
                var ratio = (double)_mediaPlayer.Time / _mediaPlayer.Length;
                SeekSlider.Value = ratio * SeekSlider.Maximum;
            }
            TimeLabel.Text = $"{FormatTime(_mediaPlayer.Time)} / {FormatTime(_mediaPlayer.Length)}";
        }
    }

    private static string FormatTime(long milliseconds)
    {
        if (milliseconds < 0) milliseconds = 0;
        var span = TimeSpan.FromMilliseconds(milliseconds);
        return span.Hours > 0 ? span.ToString(@"hh\:mm\:ss") : span.ToString(@"mm\:ss");
    }

    // ----- OSD (VLC-style transient overlay) -----

    private void ShowOsd(string text)
    {
        // Defense in depth: bail out if called before OsdText/OsdBorder are wired up (see the
        // IsLoaded guards on the handlers that call this).
        if (OsdText == null || OsdBorder == null) return;

        OsdText.Text = text;
        OsdBorder.Visibility = Visibility.Visible;

        var animation = new DoubleAnimationUsingKeyFrames();
        animation.KeyFrames.Add(new LinearDoubleKeyFrame(1.0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(120))));
        animation.KeyFrames.Add(new LinearDoubleKeyFrame(1.0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(900))));
        animation.KeyFrames.Add(new LinearDoubleKeyFrame(0.0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(1400))));
        animation.Completed += (_, _) => OsdBorder.Visibility = Visibility.Collapsed;

        OsdBorder.BeginAnimation(OpacityProperty, animation);
    }

    // ----- Preferences / equalizer -----

    private void PreferencesMenuItem_Click(object sender, RoutedEventArgs e)
    {
        var previousSettings = _equalizerSettings.Clone();
        var defaultFolder = SettingsService.GetUserString(SettingsService.Keys.DefaultMediaFolder, string.Empty);

        var window = new PreferencesWindow(_equalizerSettings, defaultFolder, ApplyEqualizerLive) { Owner = this };
        var saved = window.ShowDialog();

        if (saved == true)
        {
            _equalizerSettings = window.ResultSettings;
            ApplyEqualizerLive(_equalizerSettings);
            PersistEqualizerSettings(_equalizerSettings);

            SettingsService.SetUserString(SettingsService.Keys.DefaultMediaFolder, window.DefaultMediaFolder);

            if (window.ApplyMachineWide &&
                !SettingsService.TrySetMachineString(SettingsService.Keys.DefaultMediaFolder, window.DefaultMediaFolder))
            {
                MessageBox.Show(
                    "Couldn't write the machine-wide default (this needs the app running as administrator). Saved for your account only.",
                    "Preferences", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }
        else
        {
            // Cancelled: undo whatever the live preview applied while the sliders were dragged.
            _equalizerSettings = previousSettings;
            ApplyEqualizerLive(_equalizerSettings);
        }
    }

    private void ApplyEqualizerLive(EqualizerSettings settings)
    {
        _audioEngine.ApplyEqualizer(settings);
        if (_mediaPlayer != null)
        {
            LibVlcEqualizerAdapter.Apply(_mediaPlayer, settings);
        }
    }

    private void PersistEqualizerSettings(EqualizerSettings settings)
    {
        SettingsService.SetUserBool(SettingsService.Keys.EqualizerEnabled, settings.Enabled);
        SettingsService.SetUserDouble(SettingsService.Keys.EqualizerPreamp, settings.PreampDb);
        SettingsService.SetUserDoubleArray(SettingsService.Keys.EqualizerBands, settings.BandGainsDb);
    }

    // ----- Voice Record tab -----

    private void RecordButton_Click(object sender, RoutedEventArgs e)
    {
        if (_recorder.IsRecording)
        {
            _recorder.Stop();
            return;
        }

        try
        {
            var folder = Path.Combine(Path.GetTempPath(), "MkvPlayerRecordings");
            Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, $"Recording-{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.wav");

            _recorder.Start(path);
            RecordButton.Content = "Stop";
            RecordingStatusLabel.Text = "Recording...";
            StartRecordingIndicatorAnimation();
            _recordingTimer.Start();
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Couldn't start recording", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void Recorder_RecordingStopped(object? sender, string path)
    {
        Dispatcher.BeginInvoke(() =>
        {
            RecordButton.Content = "Record";
            RecordingStatusLabel.Text = $"Saved: {Path.GetFileName(path)} (added to the Audio tab)";
            _recordingTimer.Stop();
            RecordingTimeLabel.Text = "00:00";
            StopRecordingIndicatorAnimation();

            AddAudioFiles(new[] { path });
        });
    }

    private void Recorder_RecordingFailed(object? sender, Exception ex)
    {
        Dispatcher.BeginInvoke(() =>
        {
            RecordButton.Content = "Record";
            RecordingStatusLabel.Text = "Recording failed.";
            _recordingTimer.Stop();
            StopRecordingIndicatorAnimation();
            MessageBox.Show(ex.Message, "Recording error", MessageBoxButton.OK, MessageBoxImage.Warning);
        });
    }

    private void StartRecordingIndicatorAnimation()
    {
        var animation = new DoubleAnimation(0.15, 1.0, TimeSpan.FromMilliseconds(700))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
        };
        RecordingIndicator.BeginAnimation(OpacityProperty, animation);
    }

    private void StopRecordingIndicatorAnimation()
    {
        RecordingIndicator.BeginAnimation(OpacityProperty, null);
        RecordingIndicator.Opacity = 0;
    }

    // ----- Menu -----

    private void ExitMenuItem_Click(object sender, RoutedEventArgs e) => Close();

    private void AboutMenuItem_Click(object sender, RoutedEventArgs e)
    {
        MessageBox.Show(
            "Multimedia Player\n\nWPF UI, LibVLC for video, NAudio for audio playback and microphone recording.\n" +
            "A shared 7-band equalizer applies to both engines; settings are stored in the registry.\n\n" +
            "Design and Creation by Madhu Venugopal\nEmail: madhuvenugopal@yahoo.com",
            "About Multimedia Player", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    // ----- VideoCreator (grid / sequential playback tools) -----

    private void VideoCreatorMenuItem_Click(object sender, RoutedEventArgs e)
    {
        LibVLC creatorLibVlc;

        try
        {
            Core.Initialize();
            creatorLibVlc = new LibVLC("--no-video-title-show", "--quiet", "--no-snapshot-preview");
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                "VLC's playback engine could not be loaded." + Environment.NewLine + Environment.NewLine + ex.Message,
                "Video Creator", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        var launcher = new VideoGridStudio.Forms.LauncherForm(creatorLibVlc);
        launcher.FormClosed += (_, _) => creatorLibVlc.Dispose();
        launcher.Show();
    }

    // ----- Fullscreen (Video tab only) -----

    private void FullscreenButton_Click(object sender, RoutedEventArgs e) => ToggleFullscreen();

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.F11:
                if (ActiveTabIndex == 0) ToggleFullscreen();
                break;
            case Key.Escape when _isFullscreen:
                ToggleFullscreen();
                break;
            case Key.Space:
                PlayPauseButton_Click(this, new RoutedEventArgs());
                e.Handled = true;
                break;
        }
    }

    private void ToggleFullscreen()
    {
        if (!_isFullscreen)
        {
            _previousWindowState = WindowState;
            _previousWindowStyle = WindowStyle;
            _previousResizeMode = ResizeMode;
            _previousLeft = Left;
            _previousTop = Top;
            _previousWidth = Width;
            _previousHeight = Height;
            _previousFileListWidth = FileListColumn.ActualWidth > 0 ? FileListColumn.ActualWidth : 260;

            // WindowState.Maximized + WindowStyle.None can leave a taskbar-sized gap on some
            // Windows configurations, so we set explicit screen-covering bounds instead, which
            // is the reliable way to get true borderless fullscreen in WPF.
            WindowState = WindowState.Normal;
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            Left = 0;
            Top = 0;
            Width = SystemParameters.PrimaryScreenWidth;
            Height = SystemParameters.PrimaryScreenHeight;

            FileListColumn.Width = new GridLength(0);
            SplitterColumn.Width = new GridLength(0);
            FileListPanel.Visibility = Visibility.Collapsed;

            _isFullscreen = true;
            FullscreenButton.Content = "Exit Fullscreen";
        }
        else
        {
            WindowStyle = _previousWindowStyle;
            ResizeMode = _previousResizeMode;
            Left = _previousLeft;
            Top = _previousTop;
            Width = _previousWidth;
            Height = _previousHeight;
            WindowState = _previousWindowState;

            FileListColumn.Width = new GridLength(_previousFileListWidth);
            SplitterColumn.Width = new GridLength(4);
            FileListPanel.Visibility = Visibility.Visible;

            _isFullscreen = false;
            FullscreenButton.Content = "Fullscreen";
        }
    }
}
