using System;
using System.Diagnostics;
using System.Reflection;
using System.Windows;
using WhisperPlus.Services;

namespace WhisperPlus.Views.Pages;

/// <summary>About page: version, data location, and default hotkey reference.</summary>
public partial class AboutPage
{
    public AboutPage()
    {
        InitializeComponent();

        // Read from the assembly rather than the XAML: the badge used to be a literal, and it
        // went stale the moment the package version moved on. AssemblyVersion is what
        // Directory.Build.props drives, so this always matches the shipped build.
        var version = Assembly.GetExecutingAssembly().GetName().Version;
        if (version is not null)
        {
            Header.Badge = $"Version {version.Major}.{version.Minor}.{version.Build}";
        }
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
