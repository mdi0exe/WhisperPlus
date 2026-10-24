using System;
using WhisperPlus.Models;
using WhisperPlus.Services;

namespace WhisperPlus.Tests.Services.Fakes;

/// <summary>
/// In-memory <see cref="ISettingsService"/> for tests that need configurable settings without
/// touching %APPDATA%. <see cref="SettingsService"/> reads and writes a JSON file under the app's
/// roaming directory, which is both slow and global state; the router and sanitizer tests only
/// care what <see cref="Current"/> holds, so this holds a single mutable snapshot.
/// </summary>
public sealed class FakeSettingsService : ISettingsService
{
    public FakeSettingsService() : this(new AppSettings()) { }

    public FakeSettingsService(AppSettings initial)
    {
        // Run the same sanitizer the real service does on load, so the snapshot a test builds by
        // hand looks like one that came off disk.
        SettingsService.SanitizeLanguageRouting(initial);
        Current = initial;
    }

    public AppSettings Current { get; set; }

    public event EventHandler? Changed;

    public void Save() { }

    public void Reload() { }

    public void Update(Action<AppSettings> mutator)
    {
        mutator(Current);
        SettingsService.SanitizeLanguageRouting(Current);
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
