using NAudio.Lame;
using NAudio.Wave;

namespace MkvPlayer.Audio;

/// <summary>
/// Offline (non-real-time) rendering of the Track Separation sliders to an MP3 file. Builds its
/// own throwaway reader/filter chain rather than reusing <see cref="AudioEngine"/>'s live one, so
/// exporting never touches whatever is currently coming out of the speakers and can run on a
/// background thread while playback continues undisturbed.
/// </summary>
public static class TrackSeparationExporter
{
    public static void ExportToMp3(string sourceFilePath, string destinationMp3Path, TrackSeparationSettings settings, int bitRateKbps = 192)
    {
        using var reader = new AudioFileReader(sourceFilePath);

        var instrumentReducer = new InstrumentBandReducerSampleProvider(reader)
        {
            BassReduction = settings.BassReduction,
            DrumsReduction = settings.DrumsReduction,
            GuitarReduction = settings.GuitarReduction,
        };
        var vocalReducer = new VocalReducerSampleProvider(instrumentReducer) { Reduction = settings.VoiceReduction };

        using var writer = new LameMP3FileWriter(destinationMp3Path, vocalReducer.WaveFormat, bitRateKbps);

        var buffer = new float[vocalReducer.WaveFormat.SampleRate * vocalReducer.WaveFormat.Channels];
        var byteBuffer = new byte[buffer.Length * sizeof(float)];
        int samplesRead;
        while ((samplesRead = vocalReducer.Read(buffer, 0, buffer.Length)) > 0)
        {
            System.Buffer.BlockCopy(buffer, 0, byteBuffer, 0, samplesRead * sizeof(float));
            writer.Write(byteBuffer, 0, samplesRead * sizeof(float));
        }
    }
}
