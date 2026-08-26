using System;
using System.Windows;
using System.Windows.Media;

namespace MkvPlayer.Controls;

/// <summary>
/// A classic LED-style spectrum analyzer: a row of vertical bar-graph columns, one per
/// log-spaced frequency band, each built from discrete "LED" segments that light up
/// green/yellow/red DOWN from the top as that band gets louder, plus a slowly-decaying white
/// peak dot per column. <see cref="PushSamples"/> is called from the audio thread (via
/// <see cref="MkvPlayer.Audio.SampleTapProvider"/>) with the exact buffer about to reach the
/// speakers; the control repaints on the UI thread via <see cref="CompositionTarget.Rendering"/>.
///
/// Each band's energy comes from a single-frequency Goertzel filter run directly against the
/// latest PCM buffer -- effectively one bin of a DFT, computed only where needed. That's
/// O(bands * samples) per frame, cheap enough to run every repaint without a full FFT, since
/// the LED look only needs a modest, fixed number of bands rather than fine-grained resolution.
/// </summary>
public sealed class OscilloscopeSpectrumControl : FrameworkElement
{
    private const int BandCount = 20;
    private const int SegmentsPerBand = 14;
    private const float LevelSmoothing = 0.5f; // higher = snappier, lower = smoother
    private const float PeakDecayPerFrame = 0.02f;
    private const float MinBandHz = 55f;
    private const float MaxBandHz = 14000f;

    // The exact sample rate doesn't matter for how this looks -- it only shifts which PCM
    // samples land in which band by a few percent, invisible at 20-band resolution -- so a
    // fixed assumption avoids plumbing the real WaveFormat through PushSamples.
    private const int AssumedSampleRate = 44100;

    private static readonly float[] BandFrequencies = BuildBandFrequencies();

    private static readonly SolidColorBrush GreenLit = Frozen(Color.FromRgb(0x39, 0xFF, 0x14));
    private static readonly SolidColorBrush YellowLit = Frozen(Color.FromRgb(0xFF, 0xE0, 0x00));
    private static readonly SolidColorBrush RedLit = Frozen(Color.FromRgb(0xFF, 0x2A, 0x00));
    private static readonly SolidColorBrush GreenDim = Frozen(Color.FromRgb(0x0A, 0x2E, 0x06));
    private static readonly SolidColorBrush YellowDim = Frozen(Color.FromRgb(0x2E, 0x27, 0x00));
    private static readonly SolidColorBrush RedDim = Frozen(Color.FromRgb(0x2E, 0x08, 0x00));
    private static readonly SolidColorBrush PeakBrush = Frozen(Colors.White);

    private readonly object _lock = new();
    private readonly float[] _levels = new float[BandCount];
    private readonly float[] _peaks = new float[BandCount];

    private float[] _latestSamples = Array.Empty<float>();
    private bool _renderingHooked;

    public OscilloscopeSpectrumControl()
    {
        Loaded += (_, _) =>
        {
            if (_renderingHooked) return;
            CompositionTarget.Rendering += OnRendering;
            _renderingHooked = true;
        };
        Unloaded += (_, _) =>
        {
            if (!_renderingHooked) return;
            CompositionTarget.Rendering -= OnRendering;
            _renderingHooked = false;
        };
    }

    /// <summary>
    /// Copies the given slice of samples for the next repaint. Safe to call from any thread
    /// (the audio thread, in practice) -- it just stores a copy under a lock.
    /// </summary>
    public void PushSamples(float[] buffer, int offset, int count)
    {
        if (count <= 0) return;

        var copy = new float[count];
        Array.Copy(buffer, offset, copy, 0, count);
        lock (_lock)
        {
            _latestSamples = copy;
        }
    }

    /// <summary>Lets the bars and peaks fall back to zero over the next few frames, e.g. when playback stops.</summary>
    public void Clear()
    {
        lock (_lock)
        {
            _latestSamples = Array.Empty<float>();
        }
    }

    private void OnRendering(object? sender, EventArgs e) => InvalidateVisual();

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);

        var width = ActualWidth;
        var height = ActualHeight;
        if (width <= 0 || height <= 0) return;

        dc.DrawRectangle(Brushes.Black, null, new Rect(0, 0, width, height));

        float[] samples;
        lock (_lock)
        {
            samples = _latestSamples;
        }

        UpdateBandLevels(samples);
        DrawLedColumns(dc, width, height);
    }

    /// <summary>Runs one Goertzel filter per band against the latest buffer and smooths/peak-holds the result.</summary>
    private void UpdateBandLevels(float[] samples)
    {
        for (var b = 0; b < BandCount; b++)
        {
            var normalized = samples.Length >= 8
                ? Math.Clamp(Goertzel(samples, BandFrequencies[b], AssumedSampleRate) * 6f, 0f, 1f)
                : 0f;

            _levels[b] = _levels[b] * (1f - LevelSmoothing) + normalized * LevelSmoothing;
            _peaks[b] = _levels[b] >= _peaks[b] ? _levels[b] : Math.Max(0f, _peaks[b] - PeakDecayPerFrame);
        }
    }

    /// <summary>Single-frequency-bin magnitude, i.e. one DFT bin computed without a full FFT.</summary>
    private static float Goertzel(float[] samples, float targetFrequency, int sampleRate)
    {
        var n = samples.Length;
        var k = (int)(0.5 + n * targetFrequency / sampleRate);
        var omega = 2.0 * Math.PI / n * k;
        var cosine = Math.Cos(omega);
        var coeff = 2.0 * cosine;

        double q0, q1 = 0, q2 = 0;
        for (var i = 0; i < n; i++)
        {
            q0 = coeff * q1 - q2 + samples[i];
            q2 = q1;
            q1 = q0;
        }

        var real = q1 - q2 * cosine;
        var imag = q2 * Math.Sin(omega);
        return (float)(Math.Sqrt(real * real + imag * imag) / (n / 2.0));
    }

    private void DrawLedColumns(DrawingContext dc, double width, double height)
    {
        const double bandGap = 2.0;
        const double segmentGap = 2.0;

        var bandWidth = (width - bandGap * (BandCount + 1)) / BandCount;
        var segmentHeight = (height - segmentGap * (SegmentsPerBand + 1)) / SegmentsPerBand;
        if (bandWidth <= 0 || segmentHeight <= 0) return;

        for (var b = 0; b < BandCount; b++)
        {
            var x = bandGap + b * (bandWidth + bandGap);
            var litSegments = (int)Math.Round(_levels[b] * SegmentsPerBand);

            // s is indexed bottom-up (0 = bottom row), but louder bands fill DOWN from the top,
            // so a segment is lit once it's within litSegments of the top edge.
            var litThreshold = SegmentsPerBand - litSegments;

            for (var s = 0; s < SegmentsPerBand; s++)
            {
                var y = height - segmentGap - (s + 1) * (segmentHeight + segmentGap) + segmentGap;
                var brush = LedBrush(s, SegmentsPerBand, lit: s >= litThreshold);
                dc.DrawRectangle(brush, null, new Rect(x, y, bandWidth, segmentHeight));
            }

            var peakSegments = (int)Math.Round(_peaks[b] * SegmentsPerBand);
            if (peakSegments > 0)
            {
                // The peak marker sits at the furthest point the fill has reached down from the
                // top -- the bottom-most lit segment index when filling top-down by peakSegments.
                var peakIndex = Math.Clamp(SegmentsPerBand - peakSegments, 0, SegmentsPerBand - 1);
                var y = height - segmentGap - (peakIndex + 1) * (segmentHeight + segmentGap) + segmentGap;
                dc.DrawRectangle(PeakBrush, null, new Rect(x, y, bandWidth, Math.Max(1.0, segmentHeight * 0.4)));
            }
        }
    }

    /// <summary>Classic VU coloring: green for most of the column, yellow near the top, red at the very peak.</summary>
    private static SolidColorBrush LedBrush(int segmentIndex, int total, bool lit)
    {
        var t = segmentIndex / (float)(total - 1);

        if (t < 0.6f) return lit ? GreenLit : GreenDim;
        if (t < 0.85f) return lit ? YellowLit : YellowDim;
        return lit ? RedLit : RedDim;
    }

    private static float[] BuildBandFrequencies()
    {
        var frequencies = new float[BandCount];

        for (var i = 0; i < BandCount; i++)
        {
            var t = i / (float)(BandCount - 1);
            frequencies[i] = MinBandHz * MathF.Pow(MaxBandHz / MinBandHz, t);
        }

        return frequencies;
    }

    private static SolidColorBrush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
