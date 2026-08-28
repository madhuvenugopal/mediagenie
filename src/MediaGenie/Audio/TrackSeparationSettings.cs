namespace MkvPlayer.Audio;

/// <summary>
/// The four Track Separation sliders. All values are 0.0 (untouched) - 1.0 (fully removed).
/// Voice removal reuses <see cref="AudioEngine.VocalLevel"/>'s existing mid/side cancellation;
/// Drums/Bass/Guitar drive <see cref="InstrumentBandReducerSampleProvider"/>'s frequency-band
/// cuts. See that class for why this is an approximation, not true stem separation.
/// </summary>
public class TrackSeparationSettings
{
    public float VoiceReduction { get; set; }
    public float DrumsReduction { get; set; }
    public float BassReduction { get; set; }
    public float GuitarReduction { get; set; }

    public TrackSeparationSettings Clone() => new()
    {
        VoiceReduction = VoiceReduction,
        DrumsReduction = DrumsReduction,
        BassReduction = BassReduction,
        GuitarReduction = GuitarReduction,
    };

    public static TrackSeparationSettings Flat() => new();
}
