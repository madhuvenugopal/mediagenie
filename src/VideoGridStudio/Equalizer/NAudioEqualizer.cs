using System;
using NAudio.Dsp;
using NAudio.Wave;

namespace MkvPlayer.Equalizer;

/// <summary>
/// An <see cref="ISampleProvider"/> that sits between the audio file reader and the output
/// device on the Audio tab, applying preamp + a 5-band peaking-filter chain (one
/// <see cref="BiQuadFilter"/> per band, per channel, so stereo material gets independent
/// left/right filtering) driven by a shared <see cref="EqualizerSettings"/> instance.
/// Call <see cref="ApplySettings"/> whenever the Preferences window changes a slider -- the
/// existing filters are recomputed in place so playback doesn't glitch or reset.
/// </summary>
public sealed class NAudioEqualizer : ISampleProvider
{
    private readonly ISampleProvider _source;
    private readonly int _channels;
    private readonly BiQuadFilter[,] _filters; // [channel, band]
    private float _preampLinear = 1f;
    private volatile bool _enabled = true;

    public NAudioEqualizer(ISampleProvider source, EqualizerSettings initial)
    {
        _source = source;
        WaveFormat = source.WaveFormat;
        _channels = Math.Max(1, WaveFormat.Channels);

        var bandCount = EqualizerSettings.BandCenterFrequencies.Length;
        _filters = new BiQuadFilter[_channels, bandCount];
        for (var c = 0; c < _channels; c++)
        {
            for (var b = 0; b < bandCount; b++)
            {
                _filters[c, b] = BiQuadFilter.PeakingEQ(
                    WaveFormat.SampleRate,
                    EqualizerSettings.BandCenterFrequencies[b],
                    q: 1.0f,
                    dbGain: 0f);
            }
        }

        ApplySettings(initial);
    }

    public WaveFormat WaveFormat { get; }

    /// <summary>Recomputes every filter's coefficients from the given settings, in place.</summary>
    public void ApplySettings(EqualizerSettings settings)
    {
        _enabled = settings.Enabled;
        _preampLinear = (float)Math.Pow(10.0, settings.PreampDb / 20.0);

        var bandCount = EqualizerSettings.BandCenterFrequencies.Length;
        for (var c = 0; c < _channels; c++)
        {
            for (var b = 0; b < bandCount; b++)
            {
                var gainDb = (float)settings.BandGainsDb[b];
                _filters[c, b].SetPeakingEq(WaveFormat.SampleRate, EqualizerSettings.BandCenterFrequencies[b], 1.0f, gainDb);
            }
        }
    }

    public int Read(float[] buffer, int offset, int count)
    {
        var samplesRead = _source.Read(buffer, offset, count);
        if (!_enabled) return samplesRead;

        var bandCount = EqualizerSettings.BandCenterFrequencies.Length;
        for (var n = 0; n < samplesRead; n++)
        {
            var channel = n % _channels;
            var sample = buffer[offset + n] * _preampLinear;
            for (var b = 0; b < bandCount; b++)
            {
                sample = _filters[channel, b].Transform(sample);
            }
            buffer[offset + n] = sample;
        }

        return samplesRead;
    }
}
