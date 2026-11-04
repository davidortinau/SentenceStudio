namespace SentenceStudio.Native;

/// <summary>
/// Platform-neutral adaptive layout rules shared by the iOS and Android heads.
/// </summary>
public static class NativeAdaptiveLayout
{
    /// <summary>
    /// Below this text-scaled width a label-and-icon tile gets a full row.
    /// It is the narrowest window either platform lays out (320 pt or dp), so at
    /// the default text size every real window keeps the source's two columns.
    /// </summary>
    public const double SingleColumnWidth = 320;

    /// <summary>
    /// Choose My Own activity tiles: source Index.razor <c>col-6 col-md-4 col-lg-3</c>,
    /// two, three and four columns from 768 and 992 CSS pixels. Larger text
    /// counts as a narrower window, the way browser zoom shrinks the CSS width
    /// the web breakpoints measure, so a tile's label keeps whole words (Apple HIG
    /// Typography and Material 3 adaptive layout: reduce columns as text grows).
    /// Smaller-than-default text never adds columns.
    /// </summary>
    public static int ActivityColumns(double width, double textScale = 1)
    {
        var scale = TextScale(textScale);
        var effective = width / scale;
        if (scale > 1 && effective < SingleColumnWidth)
            return 1;
        return effective < 768 ? 2 : effective < 992 ? 3 : 4;
    }

    /// <summary>The width the layout breakpoints see at this text scale.</summary>
    public static double EffectiveWidth(double width, double textScale) =>
        width / TextScale(textScale);

    private static double TextScale(double textScale) =>
        double.IsFinite(textScale) && textScale > 1 ? textScale : 1;
}
