using System;
using System.Collections.Generic;
using System.Text;
using WhisperPlus.Models;

namespace WhisperPlus.Services;

/// <summary>
/// Bidirectional converter between English (US QWERTY) and Persian keyboard layouts, used to
/// fix text typed while the wrong layout was active.
///
/// <para><b>Problem it solves:</b> a user means to type Persian but the English layout is
/// active, producing <c>kldnhkl</c> instead of <c>نمیدانم</c> (and vice-versa). The physical
/// keys are identical; only the active layout differs. This class remaps each character to the
/// character produced by the <i>other</i> layout on the same physical key, and passes through
/// anything with no mapping (unmappable symbols, whitespace, emoji, etc.) unchanged.</para>
///
/// <para><b>Two layouts.</b> Iran has two layouts in circulation and they disagree on real
/// letters, not just punctuation: <c>m</c> is پ on the standard layout (ISIRI 9147) but ئ on the
/// older Arabic-derived one, <c>\</c> is پ on the old layout but a pass-through on the standard,
/// and the old layout keeps ASCII digits. The layout is chosen with
/// <see cref="PersianLayoutVariant"/>; the tables live in <see cref="PersianKeyboardLayouts"/>.
/// Fixing text with the wrong table silently produces plausible-looking garbage, so the variant
/// is a user setting rather than a guess.</para>
///
/// <para><b>Case handling.</b> Persian has no capital letters, so a Latin capital in the input is
/// either Caps Lock or a deliberate Shift+key — and the two need opposite treatment. The rule is
/// therefore per string, not per character:</para>
/// <list type="bullet">
///   <item><description><b>All capitals, no lowercase</b> — Caps Lock. Capitals map like their
///   lowercase form, so "SALAM" becomes "سشمشپ" (a literal key remap, not the word سلام).</description></item>
///   <item><description><b>Mixed with lowercase</b> — the capitals are real Shift presses. Shift+C
///   is ژ and Shift+H is آ on both layouts, so "C" in "Ckld" becomes ژ rather than ز.</description></item>
/// </list>
/// <para>The Shift layer is also used in the Persian → English direction, where it is never
/// ambiguous: ژ maps back to Shift+C and آ to Shift+H, so the physical keystrokes are reproducible.
/// Shifted symbols follow the layout too ("?" → "؟").</para>
/// </summary>
/// </summary>
public sealed class PersianEnglishLayoutConverter
{
    /// <summary>Conversion direction.</summary>
    public enum Direction
    {
        /// <summary>Auto-detect the dominant script and convert toward the other.</summary>
        Auto,
        /// <summary>Treat input as English keys → produce Persian text.</summary>
        EnglishToPersian,
        /// <summary>Treat input as Persian keys → produce English text.</summary>
        PersianToEnglish,
    }

    /// <summary>The Persian layout this instance inverts.</summary>
    public PersianLayoutVariant Variant { get; }

    // One lookup table per direction. They are built from the same physical-key rows, so the
    // two directions can never disagree about where a character lives.
    private readonly Dictionary<char, char> _enToFa;
    private readonly Dictionary<char, char> _enShiftedToFa;
    private readonly Dictionary<char, char> _faToEn;

    public PersianEnglishLayoutConverter(PersianLayoutVariant variant = PersianLayoutVariant.Standard)
    {
        Variant = variant;
        _enToFa = new Dictionary<char, char>(capacity: 96);
        _enShiftedToFa = new Dictionary<char, char>(capacity: 32);
        _faToEn = new Dictionary<char, char>(capacity: 96);

        foreach (var key in PersianKeyboardLayouts.Keys(variant))
        {
            // English → Persian, unshifted.
            if (key.En != '\0' && key.Fa != '\0' && key.En != key.Fa)
                _enToFa[key.En] = key.Fa;

            if (IsAsciiLetter(key.En))
            {
                // A capital is ambiguous (Caps Lock vs Shift+key), so it gets two entries: the
                // plain one for Caps Lock text, the shifted one for a real Shift press.
                if (key.EnShifted != '\0' && key.Fa != '\0')
                    _enToFa[key.EnShifted] = key.Fa;
                if (key.EnShifted != '\0' && key.FaShifted != '\0')
                    _enShiftedToFa[key.EnShifted] = key.FaShifted;
            }
            else if (key.EnShifted != '\0' && key.FaShifted != '\0' && key.EnShifted != key.FaShifted)
            {
                // Shifted symbols are never ambiguous: "?" can only come from Shift+/ , which
                // types ؟ on a Persian layout.
                _enToFa[key.EnShifted] = key.FaShifted;
            }

            // Persian → English: both layers, so the exact keystroke can be reproduced.
            if (key.Fa != '\0' && key.En != '\0' && key.Fa != key.En)
                _faToEn[key.Fa] = key.En;
            if (key.FaShifted != '\0' && key.EnShifted != '\0' && key.FaShifted != key.EnShifted)
                _faToEn[key.FaShifted] = key.EnShifted;
        }
    }

    private static bool IsAsciiLetter(char c) => (uint)(c - 'A') < 26u || (uint)(c - 'a') < 26u;

    /// <summary>
    /// Auto-detects the dominant script and converts toward the other layout. If the
    /// input is ambiguous (no mappable characters), the original string is returned
    /// unchanged. This is the method to call from the global hotkey handler.
    /// </summary>
    public string AutoConvert(string input) => Convert(input, Direction.Auto);

    /// <summary>
    /// Converts <paramref name="input"/> according to <paramref name="direction"/>. When
    /// <see cref="Direction.Auto"/>, the script with the majority of mappable characters
    /// wins and is converted to the other layout.
    /// </summary>
    public string Convert(string input, Direction direction)
    {
        if (string.IsNullOrEmpty(input)) return input ?? string.Empty;

        var effective = direction == Direction.Auto ? DetectDominantDirection(input) : direction;
        if (effective == Direction.Auto) return input;

        if (effective == Direction.PersianToEnglish)
        {
            var sb = new StringBuilder(input.Length);
            foreach (var ch in input)
                sb.Append(_faToEn.TryGetValue(ch, out var mapped) ? mapped : ch);
            return sb.ToString();
        }

        // English → Persian. Capitals are read as Caps Lock only when the whole string is
        // capitalised; otherwise they are real Shift presses and take the shifted Persian letter.
        bool capsLock = LooksLikeCapsLock(input);
        var result = new StringBuilder(input.Length);
        foreach (var ch in input)
        {
            if (!capsLock && _enShiftedToFa.TryGetValue(ch, out var shifted))
                result.Append(shifted);
            else if (_enToFa.TryGetValue(ch, out var plain))
                result.Append(plain);
            else
                result.Append(ch);
        }
        return result.ToString();
    }

    /// <summary>
    /// True when the text looks like it was typed with Caps Lock on: at least two capitals and no
    /// lowercase letters at all.
    ///
    /// <para>The two-capital floor matters. A lone capital is the *normal* way to reach the shifted
    /// Persian letters — Shift+H is آ, Shift+C is ژ — so treating "C" or "H" as Caps Lock turned
    /// exactly those letters into ز and ا and made them impossible to type. Two or more capitals in
    /// a row with nothing lowercase is what Caps Lock actually looks like.</para>
    /// </summary>
    private static bool LooksLikeCapsLock(string input)
    {
        int upper = 0;
        foreach (var ch in input)
        {
            if (ch is >= 'a' and <= 'z') return false;
            if (ch is >= 'A' and <= 'Z') upper++;
        }
        return upper >= 2;
    }

    /// <summary>
    /// Dual-role characters that exist on both layouts (ASCII punctuation on English, Shift-layer
    /// characters on Persian). These are treated as neutral in direction detection so short words
    /// like "]د" (typed as "On" on a Persian layout) or "[k" are not misclassified.
    /// </summary>
    private static bool IsDualRolePunctuation(char c) => c is '[' or ']' or '\\';

    /// <summary>
    /// Counts mappable English vs Persian characters and returns the direction to
    /// convert <i>from</i>. Returns <see cref="Direction.Auto"/> if neither script is
    /// present (nothing to do).
    /// </summary>
    private Direction DetectDominantDirection(string input)
    {
        int en = 0, fa = 0;
        foreach (var ch in input)
        {
            if (IsDualRolePunctuation(ch)) continue;

            if (IsPersian(ch) || _faToEn.ContainsKey(ch)) fa++;
            else if (_enToFa.ContainsKey(ch)) en++;
        }
        if (en == 0 && fa == 0) return Direction.Auto;
        return en >= fa ? Direction.EnglishToPersian : Direction.PersianToEnglish;
    }

    /// <summary>
    /// Returns true if <paramref name="c"/> is in the Persian/Arabic letter Unicode
    /// ranges (used for selection heuristics elsewhere; not relied on for mapping).
    /// </summary>
    public static bool IsPersian(char c) =>
        c is >= '\u0621' and <= '\u064A'      // Arabic block letters
        or >= '\u0660' and <= '\u0669'        // Arabic-Indic digits ٠-٩
        or >= '\u06F0' and <= '\u06F9'        // Extended Arabic-Indic digits ۰-۹
        or '\u067E' or '\u0686' or '\u0698'   // پ چ ژ
        or '\u06A9' or '\u06AF' or '\u06CC';  // ک گ ی
}
