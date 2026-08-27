using System.Globalization;

namespace SentenceStudio.Contracts.Theme;

/// <summary>
/// An sRGB colour parsed from a <c>#rrggbb</c> hex literal, with the WCAG 2.x relative-luminance
/// and contrast-ratio maths needed to describe a palette's accessibility without guessing.
/// </summary>
/// <remarks>
/// Contrast metadata in <see cref="ThemePalette"/> is <b>computed</b> from the same hex values the
/// CSS uses rather than hand-recorded next to them. Hand-recorded ratios drift silently the first
/// time somebody nudges a hex; computed ones cannot.
/// </remarks>
public readonly record struct SrgbColor
{
    private SrgbColor(byte r, byte g, byte b, string hex)
    {
        R = r;
        G = g;
        B = b;
        Hex = hex;
    }

    public byte R { get; }
    public byte G { get; }
    public byte B { get; }

    /// <summary>The canonical uppercase <c>#RRGGBB</c> form.</summary>
    public string Hex { get; }

    /// <summary>
    /// Parses <c>#rgb</c> or <c>#rrggbb</c>. Throws for anything else — palette literals are
    /// authored in this repository, so a malformed one is a build-time mistake, not untrusted input.
    /// </summary>
    public static SrgbColor Parse(string hex)
    {
        if (!TryParse(hex, out var color))
        {
            throw new FormatException($"'{hex}' is not a #rgb or #rrggbb colour literal.");
        }

        return color;
    }

    public static bool TryParse(string? hex, out SrgbColor color)
    {
        color = default;
        if (string.IsNullOrWhiteSpace(hex))
        {
            return false;
        }

        var trimmed = hex.Trim();
        if (trimmed.Length is not (4 or 7) || trimmed[0] != '#')
        {
            return false;
        }

        var digits = trimmed[1..];
        if (digits.Length == 3)
        {
            digits = string.Concat(digits[0], digits[0], digits[1], digits[1], digits[2], digits[2]);
        }

        if (!byte.TryParse(digits.AsSpan(0, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var r)
            || !byte.TryParse(digits.AsSpan(2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var g)
            || !byte.TryParse(digits.AsSpan(4, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var b))
        {
            return false;
        }

        color = new SrgbColor(r, g, b, $"#{r:X2}{g:X2}{b:X2}");
        return true;
    }

    /// <summary>WCAG 2.x relative luminance, 0.0 (black) to 1.0 (white).</summary>
    public double RelativeLuminance =>
        (0.2126 * Linearize(R)) + (0.7152 * Linearize(G)) + (0.0722 * Linearize(B));

    /// <summary>WCAG 2.x contrast ratio against <paramref name="other"/>, 1.0 to 21.0.</summary>
    public double ContrastRatio(SrgbColor other)
    {
        var a = RelativeLuminance;
        var b = other.RelativeLuminance;
        var (lighter, darker) = a >= b ? (a, b) : (b, a);
        return (lighter + 0.05) / (darker + 0.05);
    }

    private static double Linearize(byte channel)
    {
        var c = channel / 255.0;
        return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
    }

    public override string ToString() => Hex;
}

/// <summary>
/// Contrast facts about one colour placed on one surface, derived from the two hexes.
/// </summary>
/// <param name="Foreground">The colour being placed (a theme primary or accent).</param>
/// <param name="Background">The surface it sits on (the theme's body background for that mode).</param>
/// <param name="Ratio">WCAG 2.x contrast ratio, 1.0 to 21.0.</param>
public readonly record struct ThemeContrast(SrgbColor Foreground, SrgbColor Background, double Ratio)
{
    public static ThemeContrast Between(SrgbColor foreground, SrgbColor background) =>
        new(foreground, background, foreground.ContrastRatio(background));

    /// <summary>Meets WCAG 2.1 AA for normal-size body text (4.5:1).</summary>
    public bool MeetsAaNormalText => Ratio >= 4.5;

    /// <summary>Meets WCAG 2.1 AA for large text and UI component boundaries (3:1).</summary>
    public bool MeetsAaLargeText => Ratio >= 3.0;

    /// <summary>
    /// Black or white — whichever is more legible <b>on</b> <see cref="Foreground"/>. Used by the
    /// theme swatch so a theme's name can be printed over its own colour and stay readable.
    /// </summary>
    public string ReadableTextOnForeground
    {
        get
        {
            var white = SrgbColor.Parse("#FFFFFF");
            var black = SrgbColor.Parse("#000000");
            return Foreground.ContrastRatio(white) >= Foreground.ContrastRatio(black)
                ? white.Hex
                : black.Hex;
        }
    }
}
