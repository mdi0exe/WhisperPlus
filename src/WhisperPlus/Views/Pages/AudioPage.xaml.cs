using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using WhisperPlus.Services;

namespace WhisperPlus.Views.Pages;

/// <summary>
/// Audio settings: microphone selection, output directory, and recording format.
/// Changes persist immediately.
/// </summary>
public partial class AudioPage
{
    private readonly ISettingsService _settings;
    private readonly AudioDeviceService _devices;
    private IReadOnlyList<Models.AudioDeviceItem> _deviceList = new List<Models.AudioDeviceItem>();
    private bool _loading;

    public AudioPage(ISettingsService settings, AudioDeviceService devices)
    {
        _settings = settings;
        _devices = devices;
        InitializeComponent();
        Load();
    }

    private void Load()
    {
        _loading = true;
        try
        {
            // Populate devices and select the saved one (or the default).
            _deviceList = _devices.ListInputDevices();
            DeviceCombo.ItemsSource = _deviceList;
            for (var i = 0; i < _deviceList.Count; i++)
            {
                if (_deviceList[i].Index == _settings.Current.RecordingDeviceIndex)
                {
                    DeviceCombo.SelectedIndex = i;
                    break;
                }
            }

            // Format combo.
            var fmt = _settings.Current.RecordingFormat;
            for (var i = 0; i < FormatCombo.Items.Count; i++)
            {
                if (FormatCombo.Items[i] is ComboBoxItem ci && (ci.Content as string) == fmt)
                {
                    FormatCombo.SelectedIndex = i;
                    break;
                }
            }

            OutputDirLabel.Text = _settings.Current.RecordingOutputDirectory;
            SaveRecordingsToggle.IsChecked = _settings.Current.SaveRecordings;
            UpdateSaveDependentCards();
        }
        finally { _loading = false; }
    }

    /// <summary>
    /// Where a recording goes and what format it is written in only mean something when it is
    /// written at all, so both cards dim when saving is off rather than silently doing nothing.
    /// </summary>
    private void UpdateSaveDependentCards()
    {
        var save = SaveRecordingsToggle.IsChecked == true;
        OutputDirCard.IsEnabled = save;
        FormatCard.IsEnabled = save;
    }

    private void SaveRecordingsToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        var save = SaveRecordingsToggle.IsChecked == true;
        _settings.Update(s => s.SaveRecordings = save);
        UpdateSaveDependentCards();
    }

    private void DeviceCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        if (DeviceCombo.SelectedItem is Models.AudioDeviceItem item)
            _settings.Update(s => s.RecordingDeviceIndex = item.Index);
    }

    private void FormatCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        var fmt = (FormatCombo.SelectedItem as ComboBoxItem)?.Content as string ?? "wav";
        _settings.Update(s => s.RecordingFormat = fmt);
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Choose recording output folder",
            InitialDirectory = _settings.Current.RecordingOutputDirectory,
        };
        if (dialog.ShowDialog() == true)
        {
            var path = dialog.FolderName;
            _settings.Update(s => s.RecordingOutputDirectory = path);
            OutputDirLabel.Text = path;
        }
    }
}
