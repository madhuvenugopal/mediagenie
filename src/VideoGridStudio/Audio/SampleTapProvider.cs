using System;
using NAudio.Wave;

namespace MkvPlayer.Audio;

/// <summary>
/// A pass-through <see cref="ISampleProvider"/> that hands every buffer it reads to a
/// callback before returning it, without altering the samples. This is how the oscilloscope
/// on the Audio tab gets real-time PCM data: it's inserted right before the output device, so
/// it sees exactly what's about to reach the speakers (post-equalizer).
/// </summary>
public sealed class SampleTapProvider : ISampleProvider
{
    private readonly ISampleProvider _source;

    public SampleTapProvider(ISampleProvider source)
    {
        _source = source;
    }

    /// <summary>Raised on the audio thread with each buffer of samples as they're played. Keep handlers fast.</summary>
    public event Action<float[], int, int>? SamplesRead;

    public WaveFormat WaveFormat => _source.WaveFormat;

    public int Read(float[] buffer, int offset, int count)
    {
        var samplesRead = _source.Read(buffer, offset, count);
        if (samplesRead > 0)
        {
            SamplesRead?.Invoke(buffer, offset, samplesRead);
        }
        return samplesRead;
    }
}
