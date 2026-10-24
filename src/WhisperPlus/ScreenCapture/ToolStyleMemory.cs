using System;

namespace WhisperPlus.ScreenCapture;

/// <summary>
/// Remembers the annotation tool settings between captures: colour, width, font, step style
/// and blur strength. The overlay reads and updates this object directly, so a choice made
/// once survives the next screenshot without touching the JSON settings file.
/// </summary>
public sealed class ToolStyleMemory
{
    public string StrokeColor { get; set; } = "#FFEA6F12";
    public string FillColor { get; set; } = "#00000000";
    public double StrokeWidth { get; set; } = 4;
    public double ArrowHeadScale { get; set; } = 1.0;

    public string FontFamily { get; set; } = "Segoe UI";
    public double FontSize { get; set; } = 20;
    public bool FontBold { get; set; } = true;
    public bool FontItalic { get; set; } = false;
    public string TextColor { get; set; } = "#FFFFFFFF";
    public bool TextShadow { get; set; } = true;
    public bool TextStroke { get; set; } = false;
    public string TextStrokeColor { get; set; } = "#FF000000";
    public bool TextRibbon { get; set; } = true;
    public string TextRibbonColor { get; set; } = "#CC1F2430";
    public TextAlignmentMode TextAlignment { get; set; } = TextAlignmentMode.Left;

    public StepShape StepShape { get; set; } = StepShape.Circle;
    public string StepColor { get; set; } = "#FFE5484D";
    public string StepTextColor { get; set; } = "#FFFFFFFF";
    public double StepSize { get; set; } = 28;

    public double BlurStrength { get; set; } = 8;
    public bool BlurPixelate { get; set; } = false;
}

/// <summary>The badge shapes a numbered step marker can be drawn as.</summary>
public enum StepShape
{
    Circle,
    Square,
    Bubble,
}

/// <summary>Horizontal alignment of a text annotation.</summary>
public enum TextAlignmentMode
{
    Left,
    Center,
    Right,
}

/// <summary>Image formats a capture can be encoded to.</summary>
public enum ImageFormat
{
    Png,
    Jpeg,
    Webp,
}
