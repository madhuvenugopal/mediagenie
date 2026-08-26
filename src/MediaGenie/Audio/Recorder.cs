using System;
using NAudio.Wave;

namespace MkvPlayer.Audio;

/// <summary>
/// Wraps NAudio's microphone capture for the Voice Record tab: <see cref="WaveInEvent"/>
/// against the system default input device, written straight to a .wav file via
/// <see cref="WaveFileWriter"/>. Start/Stop map directly to the tab's Record/Stop buttons.
/// </summary>
public sealed class Recorder : IDisposable
{
    private WaveInEvent? _waveIn;
    private WaveFileWriter? _writer;
    private string? _outputPath;

    /// <summary>Raised once recording actually stops and the file has been finalized (flushed and closed).</summary>
    public event EventHandler<string>? RecordingStopped;

    /// <summary>Raised if the input device reports an error mid-recording (e.g. the mic was unplugged).</summary>
    public event EventHandler<Exception>? RecordingFailed;

    public bool IsRecording { get; private set; }

    public TimeSpan Elapsed => _writer is null
        ? TimeSpan.Zero
        : TimeSpan.FromSeconds(_writer.Length / (double)_writer.WaveFormat.AverageBytesPerSecond);

    /// <summary>
    /// Starts recording to <paramref name="outputPath"/>. Throws <see cref="InvalidOperationException"/>
    /// if no microphone is available -- callers should show a friendly message (check Windows
    /// privacy settings for microphone access) rather than let this crash the app.
    /// </summary>
    public void Start(string outputPath)
    {
        if (IsRecording) return;

        if (WaveInEvent.DeviceCount == 0)
        {
            throw new InvalidOperationException(
                "No microphone was found. Check that a microphone is connected and that this app has microphone access in Windows Settings > Privacy > Microphone.");
        }

        _outputPath = outputPath;
        _waveIn = new WaveInEvent
        {
            DeviceNumber = 0,
            WaveFormat = new WaveFormat(44100, 16, 1),
        };
        _writer = new WaveFileWriter(outputPath, _waveIn.WaveFormat);

        _waveIn.DataAvailable += OnDataAvailable;
        _waveIn.RecordingStopped += OnRecordingStopped;

        _waveIn.StartRecording();
        IsRecording = true;
    }

    public void Stop()
    {
        if (!IsRecording) return;
        _waveIn?.StopRecording();
        // Finalization happens in OnRecordingStopped once NAudio confirms the device has stopped.
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        _writer?.Write(e.Buffer, 0, e.BytesRecorded);
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        IsRecording = false;

        _writer?.Flush();
        _writer?.Dispose();
        _writer = null;

        if (_waveIn != null)
        {
            _waveIn.DataAvailable -= OnDataAvailable;
            _waveIn.RecordingStopped -= OnRecordingStopped;
            _waveIn.Dispose();
            _waveIn = null;
        }

        if (e.Exception != null)
        {
            RecordingFailed?.Invoke(this, e.Exception);
            return;
        }

        if (_outputPath != null)
        {
            RecordingStopped?.Invoke(this, _outputPath);
        }
    }

    public void Dispose()
    {
        if (IsRecording) Stop();
        _writer?.Dispose();
        _waveIn?.Dispose();
    }
}
