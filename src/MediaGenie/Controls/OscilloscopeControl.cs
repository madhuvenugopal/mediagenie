using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace MkvPlayer.Controls;

/// <summary>
/// A small "scope" window that renders whatever is playing on the Audio tab as a flame instead
/// of a classic scope trace. <see cref="PushSamples"/> is called from the audio thread (via
/// <see cref="MkvPlayer.Audio.SampleTapProvider"/>) with the exact buffer about to reach the
/// speakers; the control itself repaints on the UI thread via
/// <see cref="CompositionTarget.Rendering"/> so the flame stays smooth regardless of how often
/// audio callbacks fire.
///
/// The fire itself is the classic "Doom fire" cellular automaton: a small grid of heat values
/// that cools and drifts upward each frame. What makes it react to audio is where the heat comes
/// from -- the bottom row isn't a constant like the original effect, it's re-seeded every frame
/// from the buffer's energy, sliced into one bucket per column so louder/quieter moments (and
/// even left-to-right position within the buffer, the way the old waveform trace worked) show up
/// as taller or shorter flame in that part of the strip.
/// </summary>
public sealed class OscilloscopeControl : FrameworkElement
{
    // Resolution of the heat grid the fire simulation runs on -- deliberately small and then
    // stretched (with linear filtering) up to the control's actual size. This is what keeps a
    // cellular automaton running every repaint cheap regardless of how large the panel is.
    private const int FireWidth = 90;
    private const int FireHeight = 48;
    private const int MaxHeat = 63; // palette has MaxHeat + 1 entries, index 0 = black/out

    // Idle "embers" heat fed into the bottom row while a track is playing but quiet, so the
    // flame never fully dies mid-song -- only when playback actually stops (see Clear()).
    private const int EmberHeat = 4;

    private static readonly Color[] Palette = BuildPalette();

    private readonly object _lock = new();
    private readonly Random _rng = new();
    private readonly byte[] _heat = new byte[FireWidth * FireHeight];
    private readonly float[] _smoothedEnergy = new float[FireWidth];
    private readonly WriteableBitmap _bitmap = new(FireWidth, FireHeight, 96, 96, PixelFormats.Bgra32, null);
    private readonly byte[] _pixelBuffer = new byte[FireWidth * FireHeight * 4];
    private readonly Int32Rect _fullBitmapRect = new(0, 0, FireWidth, FireHeight);

    private float[] _latestSamples = Array.Empty<float>();
    private bool _renderingHooked;

    public OscilloscopeControl()
    {
        RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.Linear);

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

    /// <summary>Lets the flame die out, e.g. when playback stops.</summary>
    public void Clear()
    {
        lock (_lock)
        {
            _latestSamples = Array.Empty<float>();
        }
        // Deliberately not clearing _heat here -- the fire burns itself out over the next
        // second or so of frames instead of vanishing instantly, which reads as embers dying
        // down rather than someone flipping a switch.
    }

    private void OnRendering(object? sender, EventArgs e) => InvalidateVisual();

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);

        var width = ActualWidth;
        var height = ActualHeight;
        if (width <= 0 || height <= 0) return;

        float[] samples;
        lock (_lock)
        {
            samples = _latestSamples;
        }

        SeedBottomRow(samples);
        PropagateFire();
        RenderHeatToBitmap();

        dc.DrawImage(_bitmap, new Rect(0, 0, width, height));
    }

    /// <summary>
    /// Slices the sample buffer into one bucket per column and turns each bucket's RMS energy
    /// into how much heat that column gets fed this frame -- the actual "reacts to the audio"
    /// part of the effect.
    /// </summary>
    private void SeedBottomRow(float[] samples)
    {
        var bottomRowOffset = (FireHeight - 1) * FireWidth;

        if (samples.Length < 2)
        {
            // Nothing playing -- feed no fresh heat at all. Whatever's already in the grid
            // keeps propagating and cooling on its own, so the flame tapers off over a beat or
            // two instead of cutting out instantly.
            for (var x = 0; x < FireWidth; x++)
            {
                _heat[bottomRowOffset + x] = 0;
            }
            return;
        }

        var samplesPerBucket = Math.Max(1, samples.Length / FireWidth);

        for (var x = 0; x < FireWidth; x++)
        {
            var start = x * samplesPerBucket;
            float rawEnergy;

            if (start >= samples.Length)
            {
                // Buffer is smaller than the column count -- reuse the previous column's value
                // rather than reading past the end.
                rawEnergy = x > 0 ? _smoothedEnergy[x - 1] : 0f;
            }
            else
            {
                var end = x == FireWidth - 1 ? samples.Length : Math.Min(samples.Length, start + samplesPerBucket);
                double sumSquares = 0;
                var n = 0;
                for (var i = start; i < end; i++)
                {
                    var v = samples[i];
                    sumSquares += (double)v * v;
                    n++;
                }
                var rms = n > 0 ? (float)Math.Sqrt(sumSquares / n) : 0f;
                // Typical music RMS sits well under 1.0 -- push it up so normal listening
                // levels actually reach the upper half of the flame instead of just embers.
                rawEnergy = Math.Clamp(rms * 4.0f, 0f, 1f);
            }

            // Light exponential smoothing so the flame reacts quickly to real transients
            // without strobing between individual audio buffer callbacks.
            _smoothedEnergy[x] = _smoothedEnergy[x] * 0.55f + rawEnergy * 0.45f;

            var target = EmberHeat + _smoothedEnergy[x] * (MaxHeat - EmberHeat);
            var flicker = _rng.Next(-3, 4);
            _heat[bottomRowOffset + x] = (byte)Math.Clamp((int)target + flicker, 0, MaxHeat);
        }
    }

    /// <summary>
    /// The classic "Doom fire" propagation step: every cell above the bottom row inherits a
    /// slightly-cooled, slightly-sideways-drifted copy of the cell below it from last frame,
    /// which is what turns a flat row of heat into a flickering, tapering flame shape.
    /// </summary>
    private void PropagateFire()
    {
        for (var x = 0; x < FireWidth; x++)
        {
            for (var y = 1; y < FireHeight; y++)
            {
                var srcIndex = y * FireWidth + x;
                var decay = _rng.Next(4); // 0..3
                var driftedX = Math.Clamp(x - decay + 1, 0, FireWidth - 1);
                var dstIndex = (y - 1) * FireWidth + driftedX;

                var cooled = _heat[srcIndex] - (decay & 1);
                _heat[dstIndex] = (byte)Math.Max(0, cooled);
            }
        }
    }

    private void RenderHeatToBitmap()
    {
        for (var i = 0; i < _heat.Length; i++)
        {
            var color = Palette[_heat[i]];
            var offset = i * 4;
            _pixelBuffer[offset] = color.B;
            _pixelBuffer[offset + 1] = color.G;
            _pixelBuffer[offset + 2] = color.R;
            _pixelBuffer[offset + 3] = 255;
        }

        _bitmap.WritePixels(_fullBitmapRect, _pixelBuffer, FireWidth * 4, 0);
    }

    /// <summary>
    /// Builds a MaxHeat+1-entry black -> deep red -> orange -> yellow -> white-hot palette by
    /// interpolating between a handful of key colors, indexed by heat value.
    /// </summary>
    private static Color[] BuildPalette()
    {
        var stops = new (float T, Color Color)[]
        {
            (0.00f, Color.FromRgb(0x00, 0x00, 0x00)),
            (0.14f, Color.FromRgb(0x20, 0x00, 0x00)),
            (0.28f, Color.FromRgb(0x7A, 0x04, 0x03)),
            (0.42f, Color.FromRgb(0xC2, 0x18, 0x07)),
            (0.56f, Color.FromRgb(0xE8, 0x59, 0x0C)),
            (0.70f, Color.FromRgb(0xFF, 0xA5, 0x00)),
            (0.84f, Color.FromRgb(0xFF, 0xD5, 0x00)),
            (0.94f, Color.FromRgb(0xFF, 0xF3, 0xB0)),
            (1.00f, Color.FromRgb(0xFF, 0xFF, 0xFF)),
        };

        var palette = new Color[MaxHeat + 1];
        for (var i = 0; i < palette.Length; i++)
        {
            var t = i / (float)MaxHeat;

            // Find the bracket [stops[segmentIndex], stops[segmentIndex + 1]] that contains t
            // by walking forward while t has reached each successive stop's position.
            var segmentIndex = 0;
            for (var s = 0; s < stops.Length - 2; s++)
            {
                if (t >= stops[s + 1].T) segmentIndex = s + 1;
            }
            var lower = stops[segmentIndex];
            var upper = stops[Math.Min(segmentIndex + 1, stops.Length - 1)];

            var span = upper.T - lower.T;
            var localT = span > 0 ? (t - lower.T) / span : 0f;
            palette[i] = Color.FromRgb(
                (byte)(lower.Color.R + (upper.Color.R - lower.Color.R) * localT),
                (byte)(lower.Color.G + (upper.Color.G - lower.Color.G) * localT),
                (byte)(lower.Color.B + (upper.Color.B - lower.Color.B) * localT));
        }

        return palette;
    }
}
