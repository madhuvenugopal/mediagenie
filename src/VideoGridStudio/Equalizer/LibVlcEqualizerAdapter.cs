using LibVLCSharp.Shared;

namespace MkvPlayer.Equalizer;

/// <summary>
/// Translates the shared <see cref="EqualizerSettings"/> into LibVLC's native equalizer and
/// applies it to a <see cref="MediaPlayer"/> -- this is what makes the Video tab's playback
/// respect the same 5 sliders the Audio tab's <see cref="NAudioEqualizer"/> uses.
/// </summary>
public static class LibVlcEqualizerAdapter
{
    /// <summary>Builds a LibVLC <see cref="LibVLCSharp.Shared.Equalizer"/> from the shared settings.</summary>
    public static LibVLCSharp.Shared.Equalizer Build(EqualizerSettings settings)
    {
        var equalizer = new LibVLCSharp.Shared.Equalizer();
        equalizer.SetPreamp(settings.Enabled ? (float)settings.PreampDb : 0f);

        for (var i = 0; i < EqualizerSettings.LibVlcBandIndices.Length; i++)
        {
            var bandIndex = (uint)EqualizerSettings.LibVlcBandIndices[i];
            var gainDb = settings.Enabled ? (float)settings.BandGainsDb[i] : 0f;
            equalizer.SetAmp(gainDb, bandIndex);
        }

        return equalizer;
    }

    /// <summary>
    /// Applies the settings to a live MediaPlayer. LibVLC copies the equalizer's values when
    /// SetEqualizer is called, so the LibVLCSharp wrapper object can be disposed right after.
    /// </summary>
    public static void Apply(MediaPlayer player, EqualizerSettings settings)
    {
        using var equalizer = Build(settings);
        player.SetEqualizer(equalizer);
    }
}
