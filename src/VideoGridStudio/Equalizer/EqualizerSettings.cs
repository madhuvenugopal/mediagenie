namespace MkvPlayer.Equalizer;

/// <summary>
/// The one equalizer model shared by both playback engines. The Preferences window edits
/// an instance of this directly; <see cref="NAudioEqualizer"/> and
/// <see cref="LibVlcEqualizerAdapter"/> each translate it into their own engine's native
/// equalizer so a single set of sliders affects video and audio playback alike.
/// </summary>
public class EqualizerSettings
{
    /// <summary>Display labels for the 7 UI bands.</summary>
    public static readonly string[] BandLabels = { "60 Hz", "310 Hz", "600 Hz", "1 kHz", "3 kHz", "6 kHz", "12 kHz" };

    /// <summary>Center frequencies (Hz) used by the NAudio biquad chain for the Audio tab.</summary>
    public static readonly float[] BandCenterFrequencies = { 60f, 310f, 600f, 1000f, 3000f, 6000f, 12000f };

    /// <summary>
    /// LibVLC's native equalizer has 10 fixed bands (60, 170, 310, 600, 1000, 3000, 6000,
    /// 12000, 14000, 16000 Hz). Our 7 UI sliders map onto 7 of them so the Video tab's
    /// curve lines up with the Audio tab's; the other 3 LibVLC bands (170, 14000, 16000 Hz,
    /// each close neighbors of a band we do use) are left at 0 dB.
    /// </summary>
    public static readonly int[] LibVlcBandIndices = { 0, 2, 3, 4, 5, 6, 7 };

    public const double MinGainDb = -12.0;
    public const double MaxGainDb = 12.0;

    public bool Enabled { get; set; } = true;

    public double PreampDb { get; set; } = 0;

    public double[] BandGainsDb { get; set; } = new double[BandCenterFrequencies.Length];

    public EqualizerSettings Clone() => new()
    {
        Enabled = Enabled,
        PreampDb = PreampDb,
        BandGainsDb = (double[])BandGainsDb.Clone(),
    };

    public static EqualizerSettings Flat() => new();
}
