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

    internal static double PageGutter(double viewportWidth) =>
        viewportWidth < 768 ? PhoneGutter : CardInset;

    internal static int ResourceColumns(double viewportWidth) =>
        viewportWidth < 768 ? 1 : viewportWidth < 992 ? 2 : 3;

    internal static bool ShowResourceListMetadata(double viewportWidth) =>
        viewportWidth >= 768;

    internal static int ActivityColumns(double viewportWidth) =>
        viewportWidth < 768 ? 2 : viewportWidth < 992 ? 3 : 4;
}
