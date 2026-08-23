using System;
using System.Windows;
using System.Windows.Media;

namespace MkvPlayer.Controls;

/// <summary>
/// A small CRT-styled "scope" window: a live waveform trace that oscillates with whatever
/// is playing on the Audio tab. <see cref="PushSamples"/> is called from the audio thread
/// (via <see cref="MkvPlayer.Audio.SampleTapProvider"/>) with the exact buffer about to reach
/// the speakers; the control itself repaints on the UI thread via
/// <see cref="CompositionTarget.Rendering"/> so the trace stays smooth regardless of how
/// often audio callbacks fire.
/// </summary>
public sealed class OscilloscopeLineControl : FrameworkElement
{
    private readonly object _lock = new();
    private readonly Pen _tracePen;
    private readonly Pen _gridPen;
    private float[] _latestSamples = Array.Empty<float>();
    private bool _renderingHooked;

    public OscilloscopeLineControl()
    {
        var traceBrush = new SolidColorBrush(Color.FromRgb(0x39, 0xFF, 0x14)); // classic phosphor green
        traceBrush.Freeze();
        _tracePen = new Pen(traceBrush, 1.5);
        _tracePen.Freeze();

        var gridBrush = new SolidColorBrush(Color.FromRgb(0x0F, 0x3D, 0x0A));
        gridBrush.Freeze();
        _gridPen = new Pen(gridBrush, 1.0);
        _gridPen.Freeze();

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

    /// <summary>Clears the trace, e.g. when playback stops.</summary>
    public void Clear()
    {
        lock (_lock)
        {
            _latestSamples = Array.Empty<float>();
        }
        Dispatcher.Invoke(InvalidateVisual);
    }

    private void OnRendering(object? sender, EventArgs e) => InvalidateVisual();

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);

        var width = ActualWidth;
        var height = ActualHeight;
        if (width <= 0 || height <= 0) return;

        dc.DrawRectangle(Brushes.Black, null, new Rect(0, 0, width, height));

        var midY = height / 2;
        dc.DrawLine(_gridPen, new Point(0, midY), new Point(width, midY));
        dc.DrawLine(_gridPen, new Point(width / 4, 0), new Point(width / 4, height));
        dc.DrawLine(_gridPen, new Point(width / 2, 0), new Point(width / 2, height));
        dc.DrawLine(_gridPen, new Point(3 * width / 4, 0), new Point(3 * width / 4, height));

        float[] samples;
        lock (_lock)
        {
            samples = _latestSamples;
        }
        if (samples.Length < 2) return;

        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            var step = width / (samples.Length - 1);
            var firstY = midY - Math.Clamp(samples[0], -1f, 1f) * midY;
            ctx.BeginFigure(new Point(0, firstY), false, false);

            for (var i = 1; i < samples.Length; i++)
            {
                var x = i * step;
                var y = midY - Math.Clamp(samples[i], -1f, 1f) * midY;
                ctx.LineTo(new Point(x, y), true, false);
            }
        }
        geometry.Freeze();

        dc.DrawGeometry(null, _tracePen, geometry);
    }
}
