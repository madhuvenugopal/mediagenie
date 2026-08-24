using System;
using NAudio.Wave;

namespace MkvPlayer.Audio;

/// <summary>
/// An <see cref="ISampleProvider"/> that fades out center-panned content -- typically lead
/// vocals -- from stereo material, giving a "karaoke" effect. Uses mid/side decomposition:
/// mid = (L+R)/2 is whatever's identical (or near-identical) in both channels, side = (L-R)/2
/// is whatever's stereo-only. Scaling mid toward zero removes vocals along with anything else
/// panned dead center (e.g. bass, kick), which is the same trade-off every phase-cancellation
/// karaoke effect makes -- it isn't true vocal isolation, just a cheap approximation of it.
/// Mono sources have no left/right difference to work with, so they pass through unchanged.
/// </summary>
public sealed class VocalReducerSampleProvider : ISampleProvider
{
    private readonly ISampleProvider _source;
    private readonly int _channels;
    private volatile float _reduction;

    public VocalReducerSampleProvider(ISampleProvider source)
    {
        _source = source;
        WaveFormat = source.WaveFormat;
        _channels = WaveFormat.Channels;
    }

    public WaveFormat WaveFormat { get; }

    /// <summary>0.0 = vocals untouched, 1.0 = vocals fully removed.</summary>
    public float Reduction
    {
        get => _reduction;
        set => _reduction = Math.Clamp(value, 0f, 1f);
    }

    public int Read(float[] buffer, int offset, int count)
    {
        var samplesRead = _source.Read(buffer, offset, count);

        var reduction = _reduction;
        if (_channels != 2 || reduction <= 0f) return samplesRead;

        var keep = 1f - reduction;
        var end = offset + samplesRead - 1;
        for (var i = offset; i < end; i += 2)
        {
            var left = buffer[i];
            var right = buffer[i + 1];
            var mid = (left + right) * 0.5f;
            var side = (left - right) * 0.5f;
            buffer[i] = side + mid * keep;
            buffer[i + 1] = mid * keep - side;
        }

        return samplesRead;
    }
}
