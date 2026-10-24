using System;
using Microsoft.Win32;
using Serilog;
using WhisperPlus.Models;

namespace WhisperPlus.Services;

/// <summary>
/// Works out which Persian layout the machine actually has installed, so the layout fixer can
/// pick the right table without the user having to know which of the two they are running.
///
/// <para><b>Why the registry and not the keyboard APIs.</b> Windows identifies both Persian
/// layouts with the same language id (<c>0x0429</c>), so <c>GetKeyboardLayout</c> returns the same
/// low word for "Persian (Standard)" and the older "Persian (Farsi)" — they differ only in the
/// KLID's sort id (<c>00050429</c> vs <c>00000429</c>). The list of installed layouts is stored
/// verbatim in <c>HKCU\Keyboard Layout\Preload</c>, so reading it is both simpler and exact.</para>
///
/// <para><b>Precedence.</b> When both are installed the standard layout wins, because that is the
/// one Windows offers by default on current builds and the one the fixer treats as canonical. A
/// user who types with the legacy layout can pin it in Settings → Shortcuts.</para>
/// </summary>
public static class PersianLayoutDetector
{
    private const string PreloadKey = @"Keyboard Layout\Preload";
    private const string KlidStandard = "00050429";
    private const string KlidLegacy = "00000429";

    /// <summary>
    /// The layout to use: the only Persian layout installed, or the standard one when both are.
    /// Falls back to standard when nothing is installed (or the registry cannot be read).
    /// </summary>
    public static PersianLayoutVariant Detect()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(PreloadKey);
            if (key is null) return PersianLayoutVariant.Standard;

            bool legacy = false;
            foreach (var name in key.GetValueNames())
            {
                var klid = (key.GetValue(name) as string)?.Trim().ToUpperInvariant();
                if (string.IsNullOrEmpty(klid)) continue;

                if (klid == KlidStandard) return PersianLayoutVariant.Standard;
                if (klid == KlidLegacy) legacy = true;
            }

            return legacy ? PersianLayoutVariant.Legacy : PersianLayoutVariant.Standard;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Could not read the installed keyboard layouts; assuming the standard Persian layout.");
            return PersianLayoutVariant.Standard;
        }
    }
}
