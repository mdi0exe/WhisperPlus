using System.Diagnostics;
using System.Windows;
using WhisperPlus.Services;

namespace WhisperPlus.Views.Pages;

/// <summary>About page: version, data location, and default hotkey reference.</summary>
public partial class AboutPage
{
    public AboutPage()
    {
        InitializeComponent();
    }

    private void OpenDataFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = App.AppDataDirectory,
                UseShellExecute = true,
                Verb = "open",
            });
        }
        catch { /* ignore — folder may not exist yet */ }
    }
}
