using System;
using NAudio.Dsp;
using NAudio.Wave;

namespace MkvPlayer.Audio;

/// <summary>
/// An <see cref="ISampleProvider"/> that fades out center-panned content -- typically lead
/// vocals -- from stereo material, giving a "karaoke" effect. Uses mid/side decomposition:
/// mid = (L+R)/2 is whatever's identical (or near-identical) in both channels, side = (L-R)/2
/// is whatever's stereo-only.
///
/// Naively scaling ALL of mid toward zero (the classic phase-cancellation trick) also guts
/// anything else panned dead center -- kick, bass, snare -- leaving the mix thin and hollow.
/// Instead, only the slice of mid inside <see cref="VocalBandLowHz"/>-<see cref="VocalBandHighHz"/>
/// (where lead-vocal fundamentals and formants concentrate) is band-passed out and reduced;
/// everything else in mid -- bass below it, cymbals/air above it -- passes through untouched.
/// Still just a cheap approximation of true vocal isolation, not the real thing, but noticeably
/// less destructive to the rest of the mix than cancelling the whole center channel.
/// Mono sources have no left/right difference to work with, so they pass through unchanged.
/// </summary>
public sealed class VocalReducerSampleProvider : ISampleProvider
{
    private const float VocalBandLowHz = 200f;
    private const float VocalBandHighHz = 4000f;
    private const float FilterQ = 0.7f;

    private readonly ISampleProvider _source;
    private readonly int _channels;
    private readonly BiQuadFilter _midHighPass;
    private readonly BiQuadFilter _midLowPass;
    private volatile float _reduction;

    public VocalReducerSampleProvider(ISampleProvider source)
    {
        _source = source;
        WaveFormat = source.WaveFormat;
        _channels = WaveFormat.Channels;
        _midHighPass = BiQuadFilter.HighPassFilter(WaveFormat.SampleRate, VocalBandLowHz, FilterQ);
        _midLowPass = BiQuadFilter.LowPassFilter(WaveFormat.SampleRate, VocalBandHighHz, FilterQ);
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

            // Cascaded high-pass then low-pass approximates a band-pass, isolating just the
            // vocal-range slice of mid; everything else in mid rides through at full volume.
            var vocalBand = _midLowPass.Transform(_midHighPass.Transform(mid));
            var midRest = mid - vocalBand;
            var midOut = midRest + vocalBand * keep;

            buffer[i] = side + midOut;
            buffer[i + 1] = midOut - side;
        }

        return samplesRead;
    }
}
