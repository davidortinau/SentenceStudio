using System.Globalization;
using Microsoft.SwiftUI;
using SentenceStudio.Native.iOS.Styling;
using Views = Microsoft.SwiftUI.SwiftUI;

namespace SentenceStudio.Native.iOS.Features.Activities;

/// <summary>
/// The Vocabulary Matching completion block (source VocabMatching.razor,
/// isGameComplete): heading, line, stats card and Play Again, each centered in
/// the page like the source's align-items-center column. The bridge's VStack is
/// leading-aligned and the card's centered frame takes the full width, so every
/// other row carries its own centered frame (issue #234).
/// </summary>
internal static class VocabularyMatchingCompleteView
{
    internal static SwiftView Render(NativeMatchingState state, SourceUiStyle style,
        Func<string, string> text, Action playAgain)
    {
        var stats = Views.HStack(
            Stat(state.MatchedPairs, NativeMatchingText.Matched, SourceTextTone.Success,
                NativeMatchingAccess.CompleteMatched, style, text),
            Stat(state.Misses, NativeMatchingText.Misses, SourceTextTone.Danger,
                NativeMatchingAccess.CompleteMisses, style, text));
        stats.Spacing = 32;
        static SwiftView Centered(SwiftView view) =>
            view.WithFlexibleWidth(alignment: SwiftHorizontalAlignment.Center);
        var card = Views.VStack(
                Centered(Views.HStack(
                    Views.Image("trophy.fill").WithForegroundColor(
                        style.Appearance.Warning.Light, style.Appearance.Warning.Dark),
                    style.Text(Views.Text(text(NativeMatchingText.Congratulations)),
                        SourceTextRole.Title1, sourceHeading: true))),
                Centered(style.Text(Views.Text(text(NativeMatchingText.AllPairsMatched)),
                    SourceTextRole.Body1, SourceTextTone.Secondary)),
                style.Card(stats.WithPadding(SourceUiLayout.CardInset), maxWidth: 320),
                Centered(Views.HStack(style.PrimaryButton(text(NativeMatchingText.PlayAgain), playAgain)
                    .WithAccessibilityIdentifier(NativeMatchingAccess.PlayAgain))))
            .WithFlexibleWidth(alignment: SwiftHorizontalAlignment.Center)
            .WithAccessibilityIdentifier(NativeMatchingAccess.Complete);
        card.Spacing = SourceUiLayout.PageGap;
        return card;
    }

    private static SwiftView Stat(int value, string labelKey, SourceTextTone tone,
        string id, SourceUiStyle style, Func<string, string> text)
    {
        var stat = Views.VStack(
                style.Text(Views.Text(value.ToString(CultureInfo.CurrentCulture)),
                    SourceTextRole.Title2, tone),
                style.Text(Views.Text(text(labelKey)), SourceTextRole.Small,
                    SourceTextTone.Secondary))
            .WithAccessibilityLabel($"{text(labelKey)} {value}")
            .WithAccessibilityIdentifier(id);
        return stat;
    }
}
