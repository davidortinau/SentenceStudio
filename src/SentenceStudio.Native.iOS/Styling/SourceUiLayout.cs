namespace SentenceStudio.Native.iOS.Styling;

// Original app.css:790-797 supplies mobile gutters; Bootstrap p-3/p-4 and
// app.css:569-586,1046-1065 supply card and touch-target dimensions.
internal static class SourceUiLayout
{
    internal const double PhoneGutter = 12;
    internal const double PageGap = 12;
    internal const double SectionGap = 20;
    internal const double FormGap = 16;
    internal const double ItemGap = 8;
    internal const double CardInset = 16;
    internal const double AuthOuterInset = 16;
    internal const double LargeCardInset = 24;
    internal const double MinimumTouchHeight = 44;
    // Pinned VocabQuiz.razor:258-286 sizes each prompt-row icon button to 44x44.
    internal const double QuizPromptButtonDiameter = 44;
    // Pinned app.css:648-651 sizes .btn glyphs at 14px × the selected font scale.
    internal const double QuizPromptSymbolBaseSize = 14;

    /// <summary>The source's md breakpoint: from here pages use their desktop layout.</summary>
    internal const double DesktopWidth = 768;

    internal static double PageGutter(double viewportWidth) =>
        viewportWidth < DesktopWidth ? PhoneGutter : CardInset;

    internal static int ResourceColumns(double viewportWidth) =>
        viewportWidth < 768 ? 1 : viewportWidth < 992 ? 2 : 3;

    internal static bool ShowResourceListMetadata(double viewportWidth) =>
        viewportWidth >= 768;

    /// <summary>
    /// Source col-6 col-md-4 col-lg-3 by width, with fewer columns as text grows
    /// (shared <see cref="NativeAdaptiveLayout.ActivityColumns"/>).
    /// </summary>
    internal static int ActivityColumns(double viewportWidth, double textScale = 1) =>
        NativeAdaptiveLayout.ActivityColumns(viewportWidth, textScale);

    /// <summary>
    /// The system text size relative to the default (Large): Apple's Body point
    /// size for each Dynamic Type size (HIG Typography, "Dynamic Type sizes",
    /// iOS: 14, 15, 16, 17, 19, 21, 23, then 28, 33, 40, 47 and 53 for the
    /// accessibility sizes) divided by 17. <paramref name="dynamicTypeSize"/> is the
    /// facade's <c>SwiftDynamicTypeSize</c> value (XSmall = 0 ... Accessibility5 = 11).
    /// </summary>
    internal static double DynamicTypeScale(int dynamicTypeSize) =>
        (dynamicTypeSize is >= 0 and < 12 ? BodyPointSizes[dynamicTypeSize] : 17) / 17.0;

    private static readonly int[] BodyPointSizes = [14, 15, 16, 17, 19, 21, 23, 28, 33, 40, 47, 53];
}
