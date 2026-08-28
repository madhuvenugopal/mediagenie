using System;
using NAudio.Dsp;
using NAudio.Wave;

namespace MkvPlayer.Audio;

/// <summary>
/// Approximate "drums / bass / guitar" removal via broad frequency-band cuts, using the same
/// PeakingEQ biquad primitive as <see cref="Equalizer.NAudioEqualizer"/> (mutated in place with
/// <c>SetPeakingEq</c> so slider drags never allocate or glitch playback). This is NOT true stem
/// separation -- that needs a trained source-separation model, which this NAudio-based engine
/// doesn't have -- so it's the same trade-off <see cref="VocalReducerSampleProvider"/> makes with
/// phase cancellation: cutting "drums" or "guitar" also dulls whatever other content shares that
/// frequency range, and since bass/drums both lean on the low end and guitar/vocals both live in
/// the mid-range, the sliders inevitably bleed into each other somewhat.
/// </summary>
public sealed class InstrumentBandReducerSampleProvider : ISampleProvider
{
    // Bass guitar / kick fundamentals.
    private const float BassCenterHz = 100f;
    private const float BassQ = 0.7f;

    // Kick punch (low) + snare/hi-hat transient (high) -- drum energy isn't one contiguous
    // band the way bass/guitar roughly are, so it gets two cuts instead of one.
    private const float DrumsLowCenterHz = 100f;
    private const float DrumsLowQ = 1.0f;
    private const float DrumsHighCenterHz = 6000f;
    private const float DrumsHighQ = 0.7f;

    // Core of the electric/acoustic guitar range.
    private const float GuitarCenterHz = 1000f;
    private const float GuitarQ = 0.5f;

    private const float MaxCutDb = -24f;

    private readonly ISampleProvider _source;
    private readonly int _channels;
    private readonly BiQuadFilter[] _bass;
    private readonly BiQuadFilter[] _drumsLow;
    private readonly BiQuadFilter[] _drumsHigh;
    private readonly BiQuadFilter[] _guitar;

    private volatile float _bassReduction;
    private volatile float _drumsReduction;
    private volatile float _guitarReduction;

    public InstrumentBandReducerSampleProvider(ISampleProvider source)
    {
        _source = source;
        WaveFormat = source.WaveFormat;
        _channels = Math.Max(1, WaveFormat.Channels);

        _bass = CreateFilters(BassCenterHz, BassQ);
        _drumsLow = CreateFilters(DrumsLowCenterHz, DrumsLowQ);
        _drumsHigh = CreateFilters(DrumsHighCenterHz, DrumsHighQ);
        _guitar = CreateFilters(GuitarCenterHz, GuitarQ);
    }

    public WaveFormat WaveFormat { get; }

    /// <summary>0.0 (untouched) - 1.0 (fully cut).</summary>
    public float BassReduction
    {
        get => _bassReduction;
        set
        {
            _bassReduction = Math.Clamp(value, 0f, 1f);
            Retune(_bass, BassCenterHz, BassQ, _bassReduction);
        }
    }

    /// <summary>0.0 (untouched) - 1.0 (fully cut).</summary>
    public float DrumsReduction
    {
        get => _drumsReduction;
        set
        {
            _drumsReduction = Math.Clamp(value, 0f, 1f);
            Retune(_drumsLow, DrumsLowCenterHz, DrumsLowQ, _drumsReduction);
            Retune(_drumsHigh, DrumsHighCenterHz, DrumsHighQ, _drumsReduction);
        }
    }

    /// <summary>0.0 (untouched) - 1.0 (fully cut).</summary>
    public float GuitarReduction
    {
        get => _guitarReduction;
        set
        {
            _guitarReduction = Math.Clamp(value, 0f, 1f);
            Retune(_guitar, GuitarCenterHz, GuitarQ, _guitarReduction);
        }
    }

    private BiQuadFilter[] CreateFilters(float centerHz, float q)
    {
        var filters = new BiQuadFilter[_channels];
        for (var c = 0; c < _channels; c++)
        {
            filters[c] = BiQuadFilter.PeakingEQ(WaveFormat.SampleRate, centerHz, q, 0f);
        }
        return filters;
    }

    private void Retune(BiQuadFilter[] filters, float centerHz, float q, float reduction)
    {
        var dbGain = MaxCutDb * reduction;
        foreach (var filter in filters)
        {
            filter.SetPeakingEq(WaveFormat.SampleRate, centerHz, q, dbGain);
        }
    }

    public int Read(float[] buffer, int offset, int count)
    {
        var samplesRead = _source.Read(buffer, offset, count);
        if (_bassReduction <= 0f && _drumsReduction <= 0f && _guitarReduction <= 0f) return samplesRead;

        for (var n = 0; n < samplesRead; n++)
        {
            var channel = n % _channels;
            var sample = buffer[offset + n];
            sample = _bass[channel].Transform(sample);
            sample = _drumsLow[channel].Transform(sample);
            sample = _drumsHigh[channel].Transform(sample);
            sample = _guitar[channel].Transform(sample);
            buffer[offset + n] = sample;
        }

        return samplesRead;
    }
}
