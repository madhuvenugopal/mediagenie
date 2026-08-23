using System;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using MkvPlayer.Equalizer;

namespace MkvPlayer;

/// <summary>
/// The Preferences window: the 5-band equalizer (shared by video and audio playback) and
/// the default media folder setting. Every slider move calls back into <see cref="_onPreview"/>
/// immediately so changes are heard live, not just after Save; Cancel restores whatever was
/// active when the window was opened.
/// </summary>
public partial class PreferencesWindow : Window
{
    private readonly Action<EqualizerSettings> _onPreview;
    private readonly Slider[] _bandSliders;
    private readonly TextBlock[] _bandValueLabels;
    private bool _initialized;

    public EqualizerSettings ResultSettings { get; private set; }
    public string DefaultMediaFolder => DefaultFolderTextBox.Text;
    public bool ApplyMachineWide => ApplyMachineWideCheckBox.IsChecked == true;

    public PreferencesWindow(EqualizerSettings currentSettings, string defaultMediaFolder, Action<EqualizerSettings> onPreview)
    {
        InitializeComponent();

        _onPreview = onPreview;
        ResultSettings = currentSettings.Clone();

        _bandSliders = new[] { Band0Slider, Band1Slider, Band2Slider, Band3Slider, Band4Slider };
        _bandValueLabels = new[] { Band0ValueLabel, Band1ValueLabel, Band2ValueLabel, Band3ValueLabel, Band4ValueLabel };

        EqEnabledCheckBox.IsChecked = ResultSettings.Enabled;
        PreampSlider.Value = ResultSettings.PreampDb;
        for (var i = 0; i < _bandSliders.Length && i < ResultSettings.BandGainsDb.Length; i++)
        {
            _bandSliders[i].Value = ResultSettings.BandGainsDb[i];
        }

        DefaultFolderTextBox.Text = defaultMediaFolder;

        UpdateValueLabels();
        _initialized = true;
    }

    private void EqSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_initialized) return;
        ApplyUiToSettings();
        UpdateValueLabels();
        _onPreview(ResultSettings);
    }

    private void EqControl_Changed(object sender, RoutedEventArgs e)
    {
        if (!_initialized) return;
        ApplyUiToSettings();
        _onPreview(ResultSettings);
    }

    private void ResetEqButton_Click(object sender, RoutedEventArgs e)
    {
        PreampSlider.Value = 0;
        foreach (var slider in _bandSliders)
        {
            slider.Value = 0;
        }
        // ValueChanged on each slider above already re-applies and previews the reset.
    }

    private void ApplyUiToSettings()
    {
        ResultSettings.Enabled = EqEnabledCheckBox.IsChecked == true;
        ResultSettings.PreampDb = PreampSlider.Value;
        for (var i = 0; i < _bandSliders.Length; i++)
        {
            ResultSettings.BandGainsDb[i] = _bandSliders[i].Value;
        }
    }

    private void UpdateValueLabels()
    {
        PreampValueLabel.Text = $"{PreampSlider.Value:+0.0;-0.0;0.0} dB";
        for (var i = 0; i < _bandSliders.Length; i++)
        {
            _bandValueLabels[i].Text = $"{_bandSliders[i].Value:+0.0;-0.0;0.0} dB";
        }
    }

    private void BrowseFolderButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Choose a default media folder",
        };

        if (dialog.ShowDialog() == true)
        {
            DefaultFolderTextBox.Text = dialog.FolderName;
        }
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        ApplyUiToSettings();
        DialogResult = true;
        Close();
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
