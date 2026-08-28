using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using MkvPlayer.Audio;

namespace MkvPlayer;

/// <summary>
/// The Track Separation window: four Winamp-style faders for voice/drums/bass/guitar removal
/// (see <see cref="InstrumentBandReducerSampleProvider"/> and <see cref="VocalReducerSampleProvider"/>
/// for how -- it's frequency-band DSP, not true stem isolation) plus a button to render the
/// currently loaded Audio-tab track through those same settings and save it as an MP3. Every
/// slider move calls back into <see cref="_onPreview"/> immediately, same as the Preferences
/// equalizer, so changes are heard live.
/// </summary>
public partial class TrackSeparationWindow : Window
{
    private readonly Action<TrackSeparationSettings> _onPreview;
    private readonly string? _currentFilePath;
    private readonly Slider[] _sliders;
    private readonly TextBlock[] _valueLabels;
    private bool _initialized;
    private bool _exporting;

    public TrackSeparationSettings ResultSettings { get; private set; }

    public TrackSeparationWindow(TrackSeparationSettings currentSettings, string? currentFilePath, Action<TrackSeparationSettings> onPreview)
    {
        InitializeComponent();

        _onPreview = onPreview;
        _currentFilePath = currentFilePath;
        ResultSettings = currentSettings.Clone();

        _sliders = new[] { VoiceSlider, DrumsSlider, BassSlider, GuitarSlider };
        _valueLabels = new[] { VoiceValueLabel, DrumsValueLabel, BassValueLabel, GuitarValueLabel };

        VoiceSlider.Value = ResultSettings.VoiceReduction * 100;
        DrumsSlider.Value = ResultSettings.DrumsReduction * 100;
        BassSlider.Value = ResultSettings.BassReduction * 100;
        GuitarSlider.Value = ResultSettings.GuitarReduction * 100;

        TrackLabel.Text = string.IsNullOrEmpty(_currentFilePath)
            ? "No track loaded -- open a track on the Audio tab to save it as MP3."
            : $"Track: {Path.GetFileName(_currentFilePath)}";
        SaveButton.IsEnabled = !string.IsNullOrEmpty(_currentFilePath);

        UpdateValueLabels();
        _initialized = true;
    }

    private void Slider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_initialized) return;
        ApplyUiToSettings();
        UpdateValueLabels();
        _onPreview(ResultSettings);
    }

    private void ApplyUiToSettings()
    {
        ResultSettings.VoiceReduction = (float)(VoiceSlider.Value / 100.0);
        ResultSettings.DrumsReduction = (float)(DrumsSlider.Value / 100.0);
        ResultSettings.BassReduction = (float)(BassSlider.Value / 100.0);
        ResultSettings.GuitarReduction = (float)(GuitarSlider.Value / 100.0);
    }

    private void UpdateValueLabels()
    {
        for (var i = 0; i < _sliders.Length; i++)
        {
            _valueLabels[i].Text = $"{_sliders[i].Value:0}%";
        }
    }

    private async void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        if (_exporting || string.IsNullOrEmpty(_currentFilePath)) return;

        var dialog = new SaveFileDialog
        {
            Title = "Save as MP3",
            Filter = "MP3 Audio (*.mp3)|*.mp3",
            FileName = Path.GetFileNameWithoutExtension(_currentFilePath) + ".mp3",
        };
        if (dialog.ShowDialog(this) != true) return;

        var settings = ResultSettings.Clone();
        var sourcePath = _currentFilePath;
        var destinationPath = dialog.FileName;

        _exporting = true;
        SaveButton.IsEnabled = false;
        StatusLabel.Foreground = System.Windows.Media.Brushes.LightGray;
        StatusLabel.Text = "Saving...";

        try
        {
            await Task.Run(() => TrackSeparationExporter.ExportToMp3(sourcePath, destinationPath, settings));
            StatusLabel.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x6F, 0xBF, 0x6F));
            StatusLabel.Text = $"Saved to {Path.GetFileName(destinationPath)}";
        }
        catch (Exception ex)
        {
            StatusLabel.Foreground = System.Windows.Media.Brushes.IndianRed;
            StatusLabel.Text = $"Save failed: {ex.Message}";
        }
        finally
        {
            _exporting = false;
            SaveButton.IsEnabled = true;
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
