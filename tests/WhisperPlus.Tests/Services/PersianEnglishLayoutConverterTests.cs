using WhisperPlus.Models;
using WhisperPlus.Services;
using Xunit;

namespace WhisperPlus.Tests.Services;

/// <summary>
/// Unit tests for <see cref="PersianEnglishLayoutConverter"/>. The headline case is the
/// spec example: <c>kldnhkl</c> ↔ <c>نمیدانم</c>. All other rows of the ISIRI 2901
/// layout are exercised to guard against mapping regressions.
/// </summary>
public class PersianEnglishLayoutConverterTests
{
    private readonly PersianEnglishLayoutConverter _c = new();

    // ---- Headline spec example ----
    [Theory]
    [InlineData("kldnhkl", "نمیدانم")]   // forward: English keys → Persian
    [InlineData("نمیدانم", "kldnhkl")]   // reverse: Persian keys → English
    public void AutoConvert_SpecExample_Bidirectional(string input, string expected)
    {
        Assert.Equal(expected, _c.AutoConvert(input));
    }

    // ---- Auto-detection picks the dominant script ----
    // The expected strings are derived directly from the ISIRI 2901 layout table, so any
    // change to the mapping will surface as a test failure here. Note ر is on the 'v' key,
    // ج is on the '[' key, ذ is on the 'b' key.
    [Theory]
    [InlineData("sghl", "سلام")]            // pure English keys → Persian ("salam" spelling)
    [InlineData("vb", "رذ")]                // pure English keys → Persian
    [InlineData("سلام", "sghl")]            // pure Persian → English keys
    [InlineData("رذ", "vb")]                // pure Persian → English keys
    public void AutoConvert_DominantScript(string input, string expected)
    {
        Assert.Equal(expected, _c.AutoConvert(input));
    }

    // ---- Full QWERTY letter rows, forward direction ----
    [Fact]
    public void EnglishToPersian_FullLetterRows()
    {
        Assert.Equal("ضصثقفغعهخح", _c.Convert("qwertyuiop", PersianEnglishLayoutConverter.Direction.EnglishToPersian));
        // ASDF row: a s d f g h j k l  → ش س ی ب ل ا ت ن م  (ISIRI 2901)
        Assert.Equal("شسیبلاتنم", _c.Convert("asdfghjkl", PersianEnglishLayoutConverter.Direction.EnglishToPersian));
        // n m p  → د پ ح  (ZXCV-row + QWERTY-row tail; ن is on the 'k' key, not 'n')
        Assert.Equal("دپح", _c.Convert("nmp", PersianEnglishLayoutConverter.Direction.EnglishToPersian));
        Assert.Equal("ظطزرذد", _c.Convert("zxcvbn", PersianEnglishLayoutConverter.Direction.EnglishToPersian));
    }

    // ---- Caps Lock text: capitals map like their lowercase form ----
    // "SALAM" maps per the layout to س ش م ش پ (literal key remap, not the Persian word
    // "سلام"). All-capitals input is read as Caps Lock, not as five Shift presses.
    [Theory]
    [InlineData("SALAM", "سشمشپ")]
    [InlineData("SALAM!", "سشمشپ!")]
    [InlineData("salam", "سشمشپ")]
    public void EnglishToPersian_AllCapsIsTreatedAsCapsLock(string input, string expected)
    {
        Assert.Equal(expected, _c.Convert(input, PersianEnglishLayoutConverter.Direction.EnglishToPersian));
    }

    // ---- A capital inside lowercase text is a real Shift press ----
    // Shift+C is ژ and Shift+H is آ on both Persian layouts, so a word typed with them must keep
    // the shifted letter instead of collapsing to ز / ا.
    [Theory]
    [InlineData("Ckd", "ژنی")]   // C→ژ (Shift+C), k→ن, d→ی
    [InlineData("Hkh", "آنا")]   // H→آ (Shift+H), k→ن, h→ا
    public void EnglishToPersian_MixedCaseUsesTheShiftLayer(string input, string expected)
    {
        Assert.Equal(expected, _c.Convert(input, PersianEnglishLayoutConverter.Direction.EnglishToPersian));
    }

    // ---- A lone capital is a Shift press, never Caps Lock ----
    // The Caps Lock heuristic used to fire on any all-capital string, so the single letters that
    // reach the shifted Persian rows — C for ژ, H for آ — were mapped to their unshifted forms.
    [Theory]
    [InlineData("C", "ژ")]
    [InlineData("H", "آ")]
    [InlineData("C1", "ژ۱")]
    [InlineData("H?", "آ؟")]   // "?" is Shift+/ → ؟ on this layout
    public void EnglishToPersian_SingleCapitalUsesShiftLayer(string input, string expected)
    {
        Assert.Equal(expected, _c.Convert(input, PersianEnglishLayoutConverter.Direction.EnglishToPersian));
    }

    // Two or more capitals with nothing lowercase is still Caps Lock.
    [Theory]
    [InlineData("AB", "شذ")]
    [InlineData("SALAM", "سشمشپ")]
    public void EnglishToPersian_AllCapsIsCapsLock(string input, string expected)
    {
        Assert.Equal(expected, _c.Convert(input, PersianEnglishLayoutConverter.Direction.EnglishToPersian));
    }

    // ---- Shift layer, verified against KLID 00050429 with ToUnicodeEx ----
    // These rows are what the shipped layout actually produces; the hand-written table had k/l
    // shifts swapped, Shift+X as a combining mark, Shift+B as a Latin "B" and 9/0 reversed.
    [Theory]
    [InlineData("K", "»")]
    [InlineData("L", "«")]
    [InlineData("X", "ط")]
    [InlineData("B", "\u200C")]   // ZWNJ — نیم‌فاصله
    [InlineData("(", ")")]         // Shift+9
    [InlineData(")", "(")]         // Shift+0
    [InlineData("+", "+")]         // Shift+= on this layout
    public void EnglishToPersian_ShiftLayerMatchesTheInstalledLayout(string input, string expected)
    {
        Assert.Equal(expected, _c.Convert(input, PersianEnglishLayoutConverter.Direction.EnglishToPersian));
    }

    // ---- Persian digits ↔ Latin digits ----
    [Theory]
    [InlineData("12345", "۱۲۳۴۵")]
    [InlineData("0123456789", "۰۱۲۳۴۵۶۷۸۹")]
    public void EnglishToPersian_Digits(string input, string expected)
    {
        Assert.Equal(expected, _c.Convert(input, PersianEnglishLayoutConverter.Direction.EnglishToPersian));
    }

    [Theory]
    [InlineData("۱۲۳۴۵", "12345")]
    [InlineData("۰۱۲۳۴۵۶۷۸۹", "0123456789")]
    public void PersianToEnglish_Digits(string input, string expected)
    {
        Assert.Equal(expected, _c.Convert(input, PersianEnglishLayoutConverter.Direction.PersianToEnglish));
    }

    // ---- Unmapped characters pass through unchanged ----
    [Theory]
    [InlineData(".", ".")]               // period has no Persian counterpart
    [InlineData("/", "/")]               // slash pass-through
    [InlineData("", "")]                 // empty in, empty out
    [InlineData("   ", "   ")]           // whitespace untouched
    public void Convert_UnmappedCharsPassThrough(string input, string expected)
    {
        Assert.Equal(expected, _c.AutoConvert(input));
    }

    // ---- Shifted symbols pass through (Shift+1 == '!' in BOTH layouts) ----
    [Theory]
    [InlineData("!", "!")]               // '!' must NOT become '۱'
    [InlineData("a!b", "ش!ذ")]           // letters map (a→ش, b→ذ), '!' passes through
    [InlineData("hello!", "اثممخ!")]     // letters map, shifted '!' preserved
    public void Convert_ShiftedSymbolsPassThrough(string input, string expected)
    {
        Assert.Equal(expected, _c.AutoConvert(input));
    }

    // ---- Mixed input keeps each run on the right track ----
    [Fact]
    public void AutoConvert_MixedRunsAreHandled()
    {
        // 'sghl' is English-keys-dominant → converted to Persian; '123' → Persian digits.
        var result = _c.AutoConvert("sghl 123");
        Assert.Equal("سلام ۱۲۳", result);
    }

    // ---- Forced direction overrides auto-detection ----
    [Fact]
    public void Convert_ForcedDirection_OverridesAuto()
    {
        // Persian input but forced English→Persian: Persian chars have no English-key
        // mapping, so they pass through; nothing to do.
        var forced = _c.Convert("سلام", PersianEnglishLayoutConverter.Direction.EnglishToPersian);
        Assert.Equal("سلام", forced);
    }

    // ---- IsPersian helper ----
    [Theory]
    [InlineData('س', true)]
    [InlineData('ی', true)]     // Persian Yeh
    [InlineData('گ', true)]     // Gaf
    [InlineData('پ', true)]     // Peh
    [InlineData('۹', true)]     // Persian digit
    [InlineData('s', false)]
    [InlineData(' ', false)]
    [InlineData('!', false)]
    public void IsPersian_Detector(char c, bool expected)
    {
        Assert.Equal(expected, PersianEnglishLayoutConverter.IsPersian(c));
    }

    // ---- Round-trip stability (fa→en→fa) for letter-only strings ----
    [Fact]
    public void RoundTrip_PersianToEnglishAndBack()
    {
        const string original = "نمیدانم";
        var toEn = _c.Convert(original, PersianEnglishLayoutConverter.Direction.PersianToEnglish);
        var back = _c.Convert(toEn, PersianEnglishLayoutConverter.Direction.EnglishToPersian);
        Assert.Equal(original, back);
    }

    // ===================== Two layouts =====================
    // The two Persian layouts disagree on real letters, so the same keystrokes must produce
    // different text depending on which one the user types with. These rows are the ones that
    // actually differ; everything else is shared.

    [Theory]
    [InlineData('m', 'پ')]   // standard: پ sits on M
    [InlineData('\\', '\\')] // standard: backslash is the layer-3 key, not a letter
    [InlineData('\'', 'گ')]  // both layouts put گ on the quote key
    [InlineData(';', 'ک')]
    public void Standard_MapsTheKeysTheStandardDefines(char en, char fa)
    {
        Assert.Equal(fa.ToString(),
            _c.Convert(en.ToString(), PersianEnglishLayoutConverter.Direction.EnglishToPersian));
    }

    [Theory]
    [InlineData('m', 'ئ')]   // legacy: M is ئ, not پ
    [InlineData('\\', 'پ')]  // legacy: پ is on the backslash key
    [InlineData('\'', 'گ')]
    [InlineData(';', 'ک')]
    public void Legacy_MapsTheArabicDerivedKeys(char en, char fa)
    {
        var legacy = new PersianEnglishLayoutConverter(PersianLayoutVariant.Legacy);
        Assert.Equal(fa.ToString(),
            legacy.Convert(en.ToString(), PersianEnglishLayoutConverter.Direction.EnglishToPersian));
    }

    [Fact]
    public void Legacy_KeepsLatinDigits()
    {
        var legacy = new PersianEnglishLayoutConverter(PersianLayoutVariant.Legacy);
        // The old layout types ASCII digits, so converting them to ۰-۹ would be an invention.
        Assert.Equal("12345",
            legacy.Convert("12345", PersianEnglishLayoutConverter.Direction.EnglishToPersian));
    }

    [Fact]
    public void Standard_TurnsDigitsIntoPersianDigits()
    {
        Assert.Equal("۱۲۳۴۵",
            _c.Convert("12345", PersianEnglishLayoutConverter.Direction.EnglishToPersian));
    }

    // Shifted symbols follow the layout: "?" can only come from Shift+/, which types ؟.
    [Fact]
    public void ShiftedSymbol_QuestionMarkBecomesPersianQuestionMark()
    {
        Assert.Equal("؟", _c.Convert("?", PersianEnglishLayoutConverter.Direction.EnglishToPersian));
    }

    // The Shift layer is used Persian → English so the physical keystroke is reproducible.
    [Theory]
    [InlineData('ژ', 'C')]   // Shift+C on both layouts
    [InlineData('آ', 'H')]   // Shift+H on both layouts
    [InlineData('ز', 'c')]
    public void PersianToEnglish_UsesTheShiftLayer(char fa, char en)
    {
        Assert.Equal(en.ToString(),
            _c.Convert(fa.ToString(), PersianEnglishLayoutConverter.Direction.PersianToEnglish));
    }

    // A Persian word with a letter that only exists on one layout must round-trip within that layout.
    [Fact]
    public void RoundTrip_IsStableWithinEachLayout()
    {
        foreach (var variant in new[] { PersianLayoutVariant.Standard, PersianLayoutVariant.Legacy })
        {
            var c = new PersianEnglishLayoutConverter(variant);
            const string keys = "asdfghjkl";           // ش س ی ب ل ا ت ن م on both layouts
            var fa = c.Convert(keys, PersianEnglishLayoutConverter.Direction.EnglishToPersian);
            var back = c.Convert(fa, PersianEnglishLayoutConverter.Direction.PersianToEnglish);
            Assert.Equal(keys, back);
        }
    }

    // ---- Dual-role punctuation ([, ], \) in dominant direction detection ----
    [Theory]
    [InlineData("]د", "On")]     // Shift+O (]) + n (د) typed on Persian layout → "On"
    [InlineData("[د", "Pn")]     // Shift+P ([) + n (د) typed on Persian layout → "Pn"
    [InlineData("سلام]", "sghlO")] // Persian text with bracket converts Persian → English
    [InlineData("hello]", "اثممخچ")] // English text with bracket converts English → Persian
    [InlineData("]", "]")]       // Lone bracket is neutral, left as-is
    public void AutoConvert_DualRolePunctuation_HandledNeutrally(string input, string expected)
    {
        Assert.Equal(expected, _c.AutoConvert(input));
    }
}
