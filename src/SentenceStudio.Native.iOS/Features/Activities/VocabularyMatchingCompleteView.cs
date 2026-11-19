using System.Globalization;
using Microsoft.SwiftUI;
using SentenceStudio.Native.iOS.Styling;
using Views = Microsoft.SwiftUI.SwiftUI;

namespace SentenceStudio.Native.iOS.Features.Activities;

/// <summary>
/// The Vocabulary Matching completion block (source VocabMatching.razor,
/// isGameComplete): the heading at the page's leading edge, then the line, the
/// stats card and Play Again, each centered in the page. The bridge's VStack is
/// leading-aligned and the card's centered frame takes the full width, so the
/// line and the button carry their own centered frames (issue #234). The Captain
/// took the heading out of the source's centered column and dropped its trophy,
/// which read as an emoji (pull request 238).
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
                style.Text(Views.Text(text(NativeMatchingText.Congratulations)),
                        SourceTextRole.Title1, sourceHeading: true)
                    .WithFlexibleWidth(alignment: SwiftHorizontalAlignment.Leading),
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
