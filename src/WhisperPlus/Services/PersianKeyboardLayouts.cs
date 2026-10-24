using System.Collections.Generic;
using WhisperPlus.Models;

namespace WhisperPlus.Services;

/// <summary>
/// One physical key: the character the US layout produces and the character the Persian layout
/// produces on the same key, in both the unshifted and the shifted (Shift held) state.
/// A <c>'\0'</c> means "this key produces nothing in that state".
/// </summary>
public readonly record struct PersianLayoutKey(char En, char Fa, char EnShifted, char FaShifted);

/// <summary>
/// The two Persian layouts as physical-key tables.
///
/// <para><b>Where the data comes from.</b> The legacy table was read straight out of the
/// installed Windows layout (KLID 00000429) with <c>ToUnicodeEx</c>, so it is exactly what the
/// user's keyboard produces rather than a transcription. The standard table follows ISIRI 9147,
/// which is what Windows ships as <i>Persian (Standard)</i> (KLID 00050429) — the mapping the
/// standard defines for every key, including the Shift layer.</para>
///
/// <para>Both tables are ordered by physical key so a reader can compare them row by row.</para>
/// </summary>
public static class PersianKeyboardLayouts
{
    // Verified against the layout Windows actually ships for KLID 00050429: every row below was
    // read back with ToUnicodeEx rather than transcribed from a chart, which is how the previous
    // hand-written table had k/l shifts swapped, Shift+X as a combining mark, Shift+B as a Latin
    // "B" and 9/0 shifts reversed.
    private static readonly PersianLayoutKey[] StandardKeys =
    {
        // ---- Number row (Persian digits unshifted, Persian symbols shifted) ----
        // Backtick produces an invisible joiner on this layout; it is deliberately left as a
        // pass-through so a stray backtick cannot inject a zero-width character into the text.
        new('`', '`', '~', '~'),
        new('1', '۱', '!', '!'),
        new('2', '۲', '@', '٬'),      // U+066C ARABIC THOUSANDS SEPARATOR
        new('3', '۳', '#', '٫'),      // U+066B ARABIC DECIMAL SEPARATOR
        new('4', '۴', '$', '\0'),     // no shifted character on this key
        new('5', '۵', '%', '٪'),
        new('6', '۶', '^', '×'),
        new('7', '۷', '&', '،'),
        new('8', '۸', '*', '*'),
        new('9', '۹', '(', ')'),
        new('0', '۰', ')', '('),
        new('-', '-', '_', 'ـ'),      // U+0640 ARABIC TATWEEL
        new('=', '=', '+', '+'),

        // ---- QWERTY row ----
        new('q', 'ض', 'Q', 'ْ'),
        new('w', 'ص', 'W', 'ٌ'),
        new('e', 'ث', 'E', 'ٍ'),
        new('r', 'ق', 'R', 'ً'),
        new('t', 'ف', 'T', 'ُ'),
        new('y', 'غ', 'Y', 'ِ'),
        new('u', 'ع', 'U', 'َ'),
        new('i', 'ه', 'I', 'ّ'),
        new('o', 'خ', 'O', ']'),
        new('p', 'ح', 'P', '['),
        new('[', 'ج', '{', '}'),
        new(']', 'چ', '}', '{'),
        new('\\', '\\', '|', '|'),

        // ---- ASDF row ----
        new('a', 'ش', 'A', 'ؤ'),
        new('s', 'س', 'S', 'ئ'),
        new('d', 'ی', 'D', 'ي'),
        new('f', 'ب', 'F', 'إ'),
        new('g', 'ل', 'G', 'أ'),
        new('h', 'ا', 'H', 'آ'),
        new('j', 'ت', 'J', 'ة'),
        new('k', 'ن', 'K', '»'),
        new('l', 'م', 'L', '«'),
        new(';', 'ک', ':', ':'),
        new('\'', 'گ', '"', '؛'),

        // ---- ZXCV row ----
        new('z', 'ظ', 'Z', 'ك'),
        new('x', 'ط', 'X', 'ط'),
        new('c', 'ز', 'C', 'ژ'),
        new('v', 'ر', 'V', 'ٰ'),
        // Shift+B is ZWNJ (نیم‌فاصله) on the standard layout, not a Latin "B".
        new('b', 'ذ', 'B', '\u200C'),
        new('n', 'د', 'N', 'ٔ'),
        new('m', 'پ', 'M', 'ء'),
        new(',', 'و', '<', '>'),
        new('.', '.', '>', '<'),
        new('/', '/', '?', '؟'),
    };

    private static readonly PersianLayoutKey[] LegacyKeys =
    {
        // ---- Number row: Latin digits are kept as-is on this layout ----
        new('`', '÷', '~', '×'),
        new('1', '1', '!', '!'),
        new('2', '2', '@', '@'),
        new('3', '3', '#', '#'),
        new('4', '4', '$', '$'),
        new('5', '5', '%', '%'),
        new('6', '6', '^', '^'),
        new('7', '7', '&', '&'),
        new('8', '8', '*', '*'),
        new('9', '9', '(', '('),
        new('0', '0', ')', ')'),
        new('-', '-', '_', '_'),
        new('=', '=', '+', '+'),

        // ---- QWERTY row ----
        new('q', 'ض', 'Q', 'ً'),
        new('w', 'ص', 'W', 'ٌ'),
        new('e', 'ث', 'E', 'ٍ'),
        new('r', 'ق', 'R', '\0'),
        new('t', 'ف', 'T', '،'),
        new('y', 'غ', 'Y', '؛'),
        new('u', 'ع', 'U', ','),
        new('i', 'ه', 'I', ']'),
        new('o', 'خ', 'O', '['),
        new('p', 'ح', 'P', '\\'),
        new('[', 'ج', '{', '}'),
        new(']', 'چ', '}', '{'),

        // ---- ASDF row ----
        new('a', 'ش', 'A', 'َ'),
        new('s', 'س', 'S', 'ُ'),
        new('d', 'ی', 'D', 'ِ'),
        new('f', 'ب', 'F', 'ّ'),
        new('g', 'ل', 'G', 'ۀ'),
        new('h', 'ا', 'H', 'آ'),
        new('j', 'ت', 'J', 'ـ'),
        new('k', 'ن', 'K', '«'),
        new('l', 'م', 'L', '»'),
        new(';', 'ک', ':', ':'),
        new('\'', 'گ', '"', '"'),

        // ---- ZXCV row ----
        new('z', 'ظ', 'Z', 'ة'),
        new('x', 'ط', 'X', 'ي'),
        new('c', 'ز', 'C', 'ژ'),
        new('v', 'ر', 'V', 'ؤ'),
        new('b', 'ذ', 'B', 'إ'),
        new('n', 'د', 'N', 'أ'),
        new('m', 'ئ', 'M', 'ء'),
        new(',', 'و', '<', '<'),
        new('.', '.', '>', '>'),
        new('/', '/', '?', '؟'),
        new('\\', 'پ', '|', '|'),
    };

    /// <summary>The key table for a layout, in physical order.</summary>
    public static IReadOnlyList<PersianLayoutKey> Keys(PersianLayoutVariant variant) =>
        variant == PersianLayoutVariant.Legacy ? LegacyKeys : StandardKeys;
}
