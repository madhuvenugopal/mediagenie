using System;
using System.Threading;
using MkvPlayer.Equalizer;
using NAudio.Wave;

namespace MkvPlayer.Audio;

/// <summary>
/// NAudio-based playback engine used by the Audio tab (and for playing back a just-recorded
/// clip on the Voice Record tab). The signal chain is:
///
///   AudioFileReader -> NAudioEqualizer (shared 7-band EQ) -> InstrumentBandReducerSampleProvider
///     (Track Separation: drums/bass/guitar) -> VocalReducerSampleProvider (karaoke + Track
///     Separation voice) -> SampleTapProvider (oscilloscope) -> WaveOutEvent
///
/// Each stage only does one job, which is what makes tapping the samples for the
/// oscilloscope possible without disturbing playback or the equalizer.
/// </summary>
public sealed class AudioEngine : IDisposable
{
    private readonly WaveOutEvent _output = new();
    private AudioFileReader? _reader;
    private NAudioEqualizer? _equalizer;
    private InstrumentBandReducerSampleProvider? _instrumentReducer;
    private VocalReducerSampleProvider? _vocalReducer;
    private SampleTapProvider? _tap;
    private float _vocalLevel = 1f;
    private float _bassReduction;
    private float _drumsReduction;
    private float _guitarReduction;

    // How many of our own Stop() calls are still owed a PlaybackStopped notification we
    // haven't seen yet. See RequestManualStop()/OnPlaybackStopped for why this has to be a
    // counter rather than a single "was this manual" flag.
    private int _pendingManualStops;

    public AudioEngine()
    {
        _output.PlaybackStopped += OnPlaybackStopped;
    }

    /// <summary>Raised on the audio thread with each buffer about to reach the speakers (post-EQ), for the oscilloscope.</summary>
    public event Action<float[], int, int>? SamplesAvailable;

    /// <summary>Raised when a track finishes playing on its own (not from a manual Stop()) -- used to auto-advance a playlist.</summary>
    public event EventHandler? TrackEnded;

    public bool IsPlaying => _output.PlaybackState == PlaybackState.Playing;

    public TimeSpan CurrentTime
    {
        get => _reader?.CurrentTime ?? TimeSpan.Zero;
        set
        {
            if (_reader is null) return;
            var clamped = value;
            if (clamped < TimeSpan.Zero) clamped = TimeSpan.Zero;
            if (clamped > _reader.TotalTime) clamped = _reader.TotalTime;
            _reader.CurrentTime = clamped;
        }
    }

    public TimeSpan TotalTime => _reader?.TotalTime ?? TimeSpan.Zero;

    /// <summary>0.0 - 1.0</summary>
    public float Volume
    {
        get => _output.Volume;
        set => _output.Volume = Math.Clamp(value, 0f, 1f);
    }

    public string? CurrentFilePath { get; private set; }

    /// <summary>
    /// 0.0 (vocals fully faded out, karaoke) - 1.0 (vocals untouched, normal playback).
    /// Persists across tracks, same as <see cref="Volume"/>, since it's set on the engine
    /// rather than on the per-track sample chain that Play() rebuilds.
    /// </summary>
    public float VocalLevel
    {
        get => _vocalLevel;
        set
        {
            _vocalLevel = Math.Clamp(value, 0f, 1f);
            if (_vocalReducer != null) _vocalReducer.Reduction = 1f - _vocalLevel;
        }
    }

    /// <summary>0.0 (untouched) - 1.0 (fully cut). Persists across tracks, same as <see cref="VocalLevel"/>.</summary>
    public float BassReduction
    {
        get => _bassReduction;
        set
        {
            _bassReduction = Math.Clamp(value, 0f, 1f);
            if (_instrumentReducer != null) _instrumentReducer.BassReduction = _bassReduction;
        }
    }

    /// <summary>0.0 (untouched) - 1.0 (fully cut). Persists across tracks, same as <see cref="VocalLevel"/>.</summary>
    public float DrumsReduction
    {
        get => _drumsReduction;
        set
        {
            _drumsReduction = Math.Clamp(value, 0f, 1f);
            if (_instrumentReducer != null) _instrumentReducer.DrumsReduction = _drumsReduction;
        }
    }

    /// <summary>0.0 (untouched) - 1.0 (fully cut). Persists across tracks, same as <see cref="VocalLevel"/>.</summary>
    public float GuitarReduction
    {
        get => _guitarReduction;
        set
        {
            _guitarReduction = Math.Clamp(value, 0f, 1f);
            if (_instrumentReducer != null) _instrumentReducer.GuitarReduction = _guitarReduction;
        }
    }

    /// <summary>The current Track Separation slider values, e.g. to seed the window with whatever was last applied.</summary>
    public TrackSeparationSettings CurrentTrackSeparation => new()
    {
        VoiceReduction = 1f - _vocalLevel,
        DrumsReduction = _drumsReduction,
        BassReduction = _bassReduction,
        GuitarReduction = _guitarReduction,
    };

    /// <summary>Applies all four Track Separation sliders to the currently loaded track, if any, without interrupting playback.</summary>
    public void ApplyTrackSeparation(TrackSeparationSettings settings)
    {
        VocalLevel = 1f - settings.VoiceReduction;
        BassReduction = settings.BassReduction;
        DrumsReduction = settings.DrumsReduction;
        GuitarReduction = settings.GuitarReduction;
    }

    /// <summary>Loads a file and starts playing it immediately.</summary>
    public void Play(string filePath, EqualizerSettings equalizerSettings)
    {
        Teardown();

        CurrentFilePath = filePath;
        _reader = new AudioFileReader(filePath);
        _equalizer = new NAudioEqualizer(_reader, equalizerSettings);
        _instrumentReducer = new InstrumentBandReducerSampleProvider(_equalizer)
        {
            BassReduction = _bassReduction,
            DrumsReduction = _drumsReduction,
            GuitarReduction = _guitarReduction,
        };
        _vocalReducer = new VocalReducerSampleProvider(_instrumentReducer) { Reduction = 1f - _vocalLevel };
        _tap = new SampleTapProvider(_vocalReducer);
        _tap.SamplesRead += (buffer, offset, count) => SamplesAvailable?.Invoke(buffer, offset, count);

        _output.Init(_tap);
        _output.Play();
    }

    /// <summary>Applies updated equalizer settings to the currently loaded track, if any, without interrupting playback.</summary>
    public void ApplyEqualizer(EqualizerSettings settings) => _equalizer?.ApplySettings(settings);

    public void Pause()
    {
        if (_output.PlaybackState == PlaybackState.Playing)
        {
            _output.Pause();
        }
    }

    public void Resume()
    {
        if (_reader != null && _output.PlaybackState != PlaybackState.Playing)
        {
            _output.Play();
        }
    }

    public void Stop() => RequestManualStop();

    /// <summary>
    /// Stops the output (if it's actually running) and records that we're expecting exactly
    /// one PlaybackStopped notification we already know the reason for, so OnPlaybackStopped
    /// can swallow it instead of mistaking it for a track ending naturally.
    ///
    /// This used to be a single "_manualStop = true/false" bool, which is NOT enough:
    /// WaveOutEvent.Stop() only signals its playback thread to wind down -- it returns
    /// immediately, and the actual PlaybackStopped event fires later, asynchronously, from
    /// that background thread's own finally block. By the time a stale notification for a
    /// track we just switched AWAY from finally arrives, a subsequent Play() for the NEW
    /// track can easily have already flipped a plain bool flag back to "not manual" -- so the
    /// old track's late stop gets misread as the NEW track ending naturally, which is exactly
    /// what caused Next/Prev to intermittently skip an extra track or misbehave. A counter
    /// doesn't have that problem: every Stop() we issue that will actually produce a
    /// notification increments it, and OnPlaybackStopped consumes (decrements) one pending
    /// entry per notification, however late it arrives and regardless of what's played since.
    /// </summary>
    private void RequestManualStop()
    {
        // WaveOutEvent.Stop() is itself a no-op (and raises nothing) if it's already stopped,
        // so only count on a notification when there's actually a playback thread running to
        // send one.
        if (_output.PlaybackState != PlaybackState.Stopped)
        {
            Interlocked.Increment(ref _pendingManualStops);
        }
        _output.Stop();
    }

    /// <summary>
    /// OnPlaybackStopped fires on WaveOutEvent's own background thread, so consuming a pending
    /// stop has to be a real interlocked "decrement only if positive" -- a plain check-then-
    /// decrement could race against RequestManualStop() incrementing from the UI thread at the
    /// same moment.
    /// </summary>
    private bool TryConsumePendingManualStop()
    {
        while (true)
        {
            var current = Volatile.Read(ref _pendingManualStops);
            if (current <= 0) return false;
            if (Interlocked.CompareExchange(ref _pendingManualStops, current - 1, current) == current)
            {
                return true;
            }
        }
    }

    private void OnPlaybackStopped(object? sender, StoppedEventArgs e)
    {
        if (TryConsumePendingManualStop()) return;

        TrackEnded?.Invoke(this, EventArgs.Empty);
    }

    private void Teardown()
    {
        RequestManualStop();
        _reader?.Dispose();
        _reader = null;
        _equalizer = null;
        _instrumentReducer = null;
        _vocalReducer = null;
        _tap = null;
    }

    public void Dispose()
    {
        _output.PlaybackStopped -= OnPlaybackStopped;
        Teardown();
        _output.Dispose();
    }
}
