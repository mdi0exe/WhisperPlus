using System;
using System.Threading;
using System.Threading.Tasks;
using Serilog;
using WhisperPlus.Interop;
using WhisperPlus.Models;

namespace WhisperPlus.Services;

/// <summary>
/// Orchestrates the "fix wrong keyboard layout" feature end-to-end: capture the active
/// selection (or last word), bidirectionally convert it between English and Persian using
/// <see cref="PersianEnglishLayoutConverter"/>, and inject the corrected text back into
/// the focused app via <see cref="TextInjector"/>. Invoked from the global hotkey handler.
///
/// <para>The whole cycle borrows the clipboard twice — once to read the selection, once to paste
/// the correction — so this class snapshots it up front and puts it back at the end, rather than
/// letting each step save and restore around itself. Nested save/restore is what used to destroy
/// the clipboard: each step faithfully restored what it found, which by then was the *previous
/// step's* scratch value, and the user's own copied text was gone by the second one.</para>
/// </summary>
public sealed class LayoutFixService
{
    private readonly TextSelectionService _selection;
    private readonly TextInjector _injector;
    private readonly ISettingsService _settings;

    // One converter per layout variant, built on first use: the tables are immutable, and a
    // Ctrl+Space press should not rebuild two dictionaries.
    private PersianEnglishLayoutConverter? _standardConverter;
    private PersianEnglishLayoutConverter? _legacyConverter;
    private PersianEnglishLayoutConverter? _autoConverter;

    // Guard against re-entrancy: the global hotkey can fire again while we are mid-paste.
    private int _busy;

    public LayoutFixService(TextSelectionService selection, TextInjector injector, ISettingsService settings)
    {
        _selection = selection;
        _injector = injector;
        _settings = settings;
    }

    /// <summary>
    /// How long to leave the converted text on the clipboard after the paste chord before putting
    /// the user's own contents back.
    ///
    /// <para>Ctrl+V only <i>asks</i> the target app to paste; the app reads the clipboard when it
    /// processes the message, which for browsers, Electron apps and Office can be a couple of
    /// frames later. Restoring immediately therefore let the app paste the <i>previous</i>
    /// clipboard instead of the correction — an intermittent "it pasted the wrong thing".</para>
    /// </summary>
    private const int PasteSettleMs = 250;

    /// <summary>
    /// The converter for the layout the user selected in Settings → Shortcuts. The choice is read
    /// per call so switching it takes effect on the next press, without a restart.
    /// </summary>
    private PersianEnglishLayoutConverter Converter
    {
        get
        {
            switch (PersianLayoutVariants.Normalize(_settings.Current.PersianLayout))
            {
                case PersianLayoutVariants.Legacy:
                    return _legacyConverter ??= new PersianEnglishLayoutConverter(PersianLayoutVariant.Legacy);

                case PersianLayoutVariants.Standard:
                    return _standardConverter ??= new PersianEnglishLayoutConverter(PersianLayoutVariant.Standard);

                default:
                    // "auto": read the installed layouts once per session. A layout installed while
                    // the app is running is picked up on the next start, which is soon enough.
                    return _autoConverter ??= new PersianEnglishLayoutConverter(PersianLayoutDetector.Detect());
            }
        }
    }

    /// <summary>
    /// True when the converted text is mostly Persian, which is the language the keyboard should be
    /// switched to. Counts letters only, so digits and punctuation do not decide it.
    /// </summary>
    private static bool IsPersianText(string text)
    {
        int persian = 0, latin = 0;
        foreach (var ch in text)
        {
            if (PersianEnglishLayoutConverter.IsPersian(ch)) persian++;
            else if (char.IsAsciiLetter(ch)) latin++;
        }
        return persian > 0 && persian >= latin;
    }

    /// <summary>
    /// Runs the full fix cycle. Returns the converted text if anything was changed,
    /// or null if there was nothing to convert (no selection / no last word).
    /// </summary>
    public async Task<string?> FixAsync(CancellationToken ct = default)
    {
        // Re-entrancy guard: drop overlapping invocations (MOD_NOREPEAT mostly prevents this,
        // but a user can still mash the key).
        if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0)
        {
            Log.Debug("LayoutFix already in progress; ignoring re-entrant call.");
            return null;
        }

        // Taken before anything touches the clipboard, and detached from the app that owns it so it
        // survives being overwritten. Null while the clipboard-free path is running, which is the
        // whole point: nothing to restore because nothing was disturbed.
        ClipboardSnapshot? backup = null;
        var usedClipboard = false;

        try
        {
            // 1. Capture what the user meant to fix. The clipboard-free path reads the selection
            //    through UI Automation; only when the app does not expose it does the clipboard get
            //    borrowed (and then restored).
            TextSelectionService.CaptureResult? capture = null;
            if (_settings.Current.LayoutFixClipboardFree)
            {
                var free = await _selection.CaptureWithoutClipboardAsync(fallbackToLastWord: true, ct);
                if (free.Supported)
                {
                    capture = free.Result;
                }
                else
                {
                    Log.Debug("LayoutFix: the focused app does not expose its selection; using the clipboard.");
                }
            }

            if (capture is null)
            {
                usedClipboard = true;
                backup = await _injector.CaptureClipboardAsync().ConfigureAwait(true);
                capture = await _selection.CaptureAsync(fallbackToLastWord: true, ct);
            }

            if (!capture.Success || string.IsNullOrWhiteSpace(capture.Text))
            {
                Log.Information("LayoutFix: nothing to convert (source={Source}).", capture.Source);
                return null;
            }

            // 2. Convert it.
            var converted = Converter.AutoConvert(capture.Text);
            if (string.Equals(converted, capture.Text, StringComparison.Ordinal))
            {
                Log.Information("LayoutFix: input unchanged after conversion ({Text}).", capture.Text);
                return null;
            }

            // 3. Put the correction back. When the selection was read through UI Automation there is
            //    nothing on the clipboard and nothing to clean up; the text is typed instead of
            //    pasted, so the clipboard is never written to at all.
            bool applied;
            if (backup is null)
            {
                applied = await _injector.TypeAsync(converted, ct);
                if (!applied)
                    Log.Warning("LayoutFix: could not type the correction; leaving the text unchanged.");
            }
            else
            {
                applied = await _injector.PasteAsync(converted, ct);
                if (!applied)
                {
                    // The clipboard was locked or the chord could not be sent. Saying "converted"
                    // here would be a lie the user only notices as "nothing happened".
                    Log.Warning("LayoutFix: could not paste the correction; leaving the text unchanged.");
                    return null;
                }

                // Let the target app actually read the clipboard before it is restored underneath it.
                await Task.Delay(PasteSettleMs, ct);
                await _injector.RestoreAsync(backup).ConfigureAwait(true);
                backup = null;
            }

            if (!applied) return null;

            // 4. Optionally put the keyboard in the language the text is now in, so the user can keep
            //    typing instead of reaching for Alt+Shift.
            if (_settings.Current.LayoutFixSwitchLanguage)
                VirtualKeyboard.ActivateInputLanguage(IsPersianText(converted));

            Log.Information("LayoutFix: '{Before}' -> '{After}' (source={Source}, clipboardFree={Free}).",
                capture.Text, converted, capture.Source, usedClipboard is false);
            return converted;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "LayoutFix failed.");
            return null;
        }
        finally
        {
            // Only a path that actually borrowed the clipboard has anything to put back; the
            // success path already cleared it.
            if (backup is not null)
            {
                try { await _injector.RestoreAsync(backup).ConfigureAwait(true); }
                catch (Exception ex) { Log.Warning(ex, "Could not restore the clipboard after a failed layout fix."); }
            }
            Volatile.Write(ref _busy, 0);
        }
    }
}
