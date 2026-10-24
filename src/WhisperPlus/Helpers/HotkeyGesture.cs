using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Windows.Input;
using WhisperPlus.Interop;

namespace WhisperPlus.Helpers;

/// <summary>
/// Bidirectional conversion between a human hotkey gesture string ("Ctrl+Shift+R")
/// and the (modifiers, virtual-key) pair that RegisterHotKey expects. Handles the
/// modifier spelling variants ("Ctrl" vs "Control", "Win" vs "Windows"/"Super") and
/// supports letters, digits, F-keys, and common special keys.
/// </summary>
public static class HotkeyGesture
{
    /// <summary>Parses a gesture string into modifiers + virtual key. Returns null if invalid.</summary>
    public static (uint Modifiers, uint VirtualKey)? Parse(string? gesture)
    {
        if (string.IsNullOrWhiteSpace(gesture)) return null;

        uint modifiers = 0;
        uint vk = 0;

        var trimmed = gesture.Trim();
        string[] parts;
        if (trimmed == "+")
        {
            parts = new[] { "+" };
        }
        else if (trimmed.EndsWith('+'))
        {
            var beforeLastPlus = trimmed[..^1].TrimEnd();
            if (beforeLastPlus.EndsWith('+'))
            {
                var modPart = beforeLastPlus[..^1].TrimEnd();
                var modTokens = modPart.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                var list = new List<string>(modTokens) { "+" };
                parts = list.ToArray();
            }
            else
            {
                return null;
            }
        }
        else
        {
            parts = trimmed.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        }

        foreach (var part in parts)
        {
            var p = part;
            var last = p[^1];
            if (p.Equals("Ctrl", StringComparison.OrdinalIgnoreCase) ||
                p.Equals("Control", StringComparison.OrdinalIgnoreCase))
            {
                modifiers |= Win32.MOD_CONTROL;
            }
            else if (p.Equals("Alt", StringComparison.OrdinalIgnoreCase))
            {
                modifiers |= Win32.MOD_ALT;
            }
            else if (p.Equals("Shift", StringComparison.OrdinalIgnoreCase))
            {
                modifiers |= Win32.MOD_SHIFT;
            }
            else if (p.Equals("Win", StringComparison.OrdinalIgnoreCase) ||
                     p.Equals("Windows", StringComparison.OrdinalIgnoreCase) ||
                     p.Equals("Super", StringComparison.OrdinalIgnoreCase) ||
                     p.Equals("Meta", StringComparison.OrdinalIgnoreCase))
            {
                modifiers |= Win32.MOD_WIN;
            }
            else if (p.Length == 1 && char.IsAsciiLetterOrDigit(last))
            {
                // Only ASCII letters and digits share their code point with the Win32 virtual-key
                // code (A-Z = 0x41-0x5A, 0-9 = 0x30-0x39). Punctuation does NOT: '-' is 0x2D, which
                // Win32 reads as VK_INSERT, and '[' is 0x5B, which is VK_LWIN. Casting any single
                // character straight to a VK therefore turned "Ctrl+-" into Ctrl+Insert and
                // "Ctrl+[" into a Windows-key hotkey. Punctuation goes through TryParseSpecialKey.
                vk = (uint)char.ToUpperInvariant(last);
            }
            else
            {
                if (TryParseSpecialKey(p, out vk)) continue;
                return null; // unknown token
            }
        }

        if (vk == 0) return null;
        return (modifiers | Win32.MOD_NOREPEAT, vk);
    }

    /// <summary>
    /// Formats a (modifiers, vk) pair back to a display string, canonicalizing modifier
    /// order as Ctrl+Alt+Shift+Win.
    /// </summary>
    public static string Format(uint modifiers, uint vk)
    {
        var sb = new StringBuilder();
        if ((modifiers & Win32.MOD_CONTROL) != 0) sb.Append("Ctrl+");
        if ((modifiers & Win32.MOD_ALT) != 0) sb.Append("Alt+");
        if ((modifiers & Win32.MOD_SHIFT) != 0) sb.Append("Shift+");
        if ((modifiers & Win32.MOD_WIN) != 0) sb.Append("Win+");
        sb.Append(FormatKey(vk));
        return sb.ToString();
    }

    /// <summary>Returns the WPF <see cref="ModifierKeys"/> equivalent of Win32 modifiers.</summary>
    public static ModifierKeys ToWpfModifiers(uint win32Modifiers)
    {
        ModifierKeys mk = ModifierKeys.None;
        if ((win32Modifiers & Win32.MOD_CONTROL) != 0) mk |= ModifierKeys.Control;
        if ((win32Modifiers & Win32.MOD_ALT) != 0) mk |= ModifierKeys.Alt;
        if ((win32Modifiers & Win32.MOD_SHIFT) != 0) mk |= ModifierKeys.Shift;
        if ((win32Modifiers & Win32.MOD_WIN) != 0) mk |= ModifierKeys.Windows;
        return mk;
    }

    private static bool TryParseSpecialKey(string token, out uint vk)
    {
        vk = 0;
        switch (token.ToUpperInvariant())
        {
            // OEM / punctuation keys. These are the VK_OEM_* codes RegisterHotKey expects, and
            // they are what the rebind button produces when the user presses the physical key.
            case "-": case "MINUS": vk = 0xBD; return true;   // VK_OEM_MINUS
            case "=": case "+": case "PLUS": vk = 0xBB; return true; // VK_OEM_PLUS
            case "ADD": vk = 0x6B; return true;               // VK_ADD
            case "SUBTRACT": vk = 0x6D; return true;          // VK_SUBTRACT
            case "*": case "MULTIPLY": vk = 0x6A; return true;
            case "DIVIDE": vk = 0x6F; return true;
            case "[": vk = 0xDB; return true;   // VK_OEM_4
            case "]": vk = 0xDD; return true;   // VK_OEM_6
            case "\\": vk = 0xDC; return true;  // VK_OEM_5
            case ";": vk = 0xBA; return true;   // VK_OEM_1
            case "'": vk = 0xDE; return true;   // VK_OEM_7
            case ",": vk = 0xBC; return true;   // VK_OEM_COMMA
            case ".": vk = 0xBE; return true;   // VK_OEM_PERIOD
            case "/": vk = 0xBF; return true;   // VK_OEM_2
            case "`": case "~": vk = 0xC0; return true; // VK_OEM_3
            case "SPACE": vk = 0x20; return true;
            case "ENTER": case "RETURN": vk = 0x0D; return true;
            case "TAB": vk = 0x09; return true;
            case "ESC": case "ESCAPE": vk = 0x1B; return true;
            case "BACK": case "BACKSPACE": vk = 0x08; return true;
            case "DEL": case "DELETE": vk = 0x2E; return true;
            case "INS": case "INSERT": vk = 0x2D; return true;
            case "HOME": vk = 0x24; return true;
            case "END": vk = 0x23; return true;
            case "PGUP": case "PAGEUP": vk = 0x21; return true;
            case "PGDN": case "PAGEDOWN": vk = 0x22; return true;
            case "UP": vk = 0x26; return true;
            case "DOWN": vk = 0x28; return true;
            case "LEFT": vk = 0x25; return true;
            case "RIGHT": vk = 0x27; return true;
            case "PRTSC": case "PRINTSCREEN": vk = 0x2C; return true;
            case "NUMLOCK": vk = 0x90; return true;
            case "SCROLL": vk = 0x91; return true;
        }

        // F1..F24
        if (token.Length > 1 && (token[0] == 'F' || token[0] == 'f') &&
            int.TryParse(token[1..], NumberStyles.None, CultureInfo.InvariantCulture, out int f) &&
            f is >= 1 and <= 24)
        {
            vk = (uint)(0x6F + f);
            return true;
        }
        return false;
    }

    private static string FormatKey(uint vk)
    {
        if (vk == 0x20) return "Space";
        if (vk == 0x0D) return "Enter";
        if (vk == 0x09) return "Tab";
        if (vk == 0x1B) return "Esc";
        if (vk == 0x08) return "Backspace";
        if (vk == 0x2E) return "Delete";
        if (vk == 0x2D) return "Insert";
        if (vk == 0x24) return "Home";
        if (vk == 0x23) return "End";
        if (vk == 0x21) return "PageUp";
        if (vk == 0x22) return "PageDown";
        if (vk == 0x26) return "Up";
        if (vk == 0x28) return "Down";
        if (vk == 0x25) return "Left";
        if (vk == 0x27) return "Right";
        if (vk == 0x2C) return "PrintScreen";
        if (vk is >= 0x70 and <= 0x87) return "F" + (vk - 0x6F);
        // OEM keys round-trip as the character the user pressed, so Parse can map them back.
        switch (vk)
        {
            case 0xBD: return "-";
            case 0xBB: return "=";
            case 0xDB: return "[";
            case 0xDD: return "]";
            case 0xDC: return "\\";
            case 0xBA: return ";";
            case 0xDE: return "'";
            case 0xBC: return ",";
            case 0xBE: return ".";
            case 0xBF: return "/";
            case 0xC0: return "`";
        }
        if (vk >= 0x30 && vk <= 0x5A) return ((char)vk).ToString();
        return $"0x{vk:X}";
    }
}
