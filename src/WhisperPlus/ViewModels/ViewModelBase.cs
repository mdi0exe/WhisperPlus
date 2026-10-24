using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace WhisperPlus.ViewModels;

/// <summary>
/// Lightweight INotifyPropertyChanged base for view models that don't need source
/// generators. For richer VMs we'd use CommunityToolkit.Mvvm's [ObservableProperty],
/// but most of our settings bind directly to the AppSettings model, so a simple base
/// suffices.
/// </summary>
public abstract class ViewModelBase : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Raises <see cref="PropertyChanged"/> for the given property.</summary>
    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    /// <summary>Sets a field and raises change notification if the value differs.</summary>
    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }
}
