using System.Globalization;
using Microsoft.SwiftUI;
using SentenceStudio.Native;
using SentenceStudio.Native.iOS.Styling;
using Views = Microsoft.SwiftUI.SwiftUI;

namespace SentenceStudio.Native.iOS.Features.Vocabulary;

internal sealed record VocabularyDuplicateActions(
    Action Close, Action ShowMore, Action<string> Merge, Action<string> EditRecord);

/// <summary>
/// The Vocabulary list's duplicate manager (source Vocabulary.razor
/// "vocabulary-cleanup-panel" with showDuplicatePanel), in SwiftUI.
/// </summary>
internal static class VocabularyDuplicatesView
{
    internal static SwiftView Panel(
        VocabularyDuplicatePanel panel, SourceUiStyle style,
        Func<string, string> text, VocabularyDuplicateActions actions)
    {
        var close = Views.Button(Views.Image("xmark"), actions.Close)
            .WithAccessibilityLabel(text("Common_Close"))
            .WithAccessibilityIdentifier("native.vocabulary.duplicates.close");
        var header = Views.HStack(
                style.Text(Views.Text(panel.Title), SourceTextRole.Title3,
                        sourceHeading: true)
                    .WithFlexibleWidth()
                    .WithAccessibilityIdentifier("native.vocabulary.duplicates.title"),
                close)
            .WithFlexibleWidth();
        header.Spacing = SourceUiLayout.ItemGap;
        var items = new List<SwiftView> { header };

        if (panel.Running)
            items.Add(Views.IndeterminateProgress(panel.Status)
                .WithAccessibilityIdentifier("native.vocabulary.duplicates.status"));
        else
        {
            items.Add(style.Text(Views.Text(panel.Status), SourceTextRole.Body2)
                .WithAccessibilityIdentifier("native.vocabulary.duplicates.status"));
            if (!string.IsNullOrWhiteSpace(panel.FocusLabel))
                items.Add(style.Text(Views.Text(
                        VocabularyDuplicateFinder.FocusNote(panel.FocusLabel, text)),
                        SourceTextRole.Small, SourceTextTone.Secondary)
                    .WithAccessibilityIdentifier("native.vocabulary.duplicates.focus"));
            items.Add(style.Text(Views.Text(text("NativeVocabulary_DuplicatesCombinedNote")),
                    SourceTextRole.Small, SourceTextTone.Secondary));
            if (panel.Groups.Count == 0)
                items.Add(style.InfoAlert(style.Text(
                        Views.Text(text("Vocabulary_DuplicatesNoRecords")),
                        SourceTextRole.Body2), compact: true)
                    .WithAccessibilityIdentifier("native.vocabulary.duplicates.empty"));
            else
            {
                for (var index = 0; index < panel.Groups.Count; index++)
                    items.Add(Group(panel, panel.Groups[index], index, style, text, actions));
                if (panel.RemainingGroupCount > 0)
                    items.Add(style.OutlineButton(Views.Text(string.Format(
                            CultureInfo.CurrentCulture, text("Vocabulary_DuplicatesShowMore"),
                            panel.RemainingGroupCount)), actions.ShowMore)
                        .WithAccessibilityIdentifier("native.vocabulary.duplicates.more"));
            }
        }
        if (!panel.Running)
            for (var index = 0; index < panel.Results.Count; index++)
                items.Add(style.InfoAlert(style.Text(Views.Text(panel.Results[index]),
                        SourceTextRole.Body2), compact: true)
                    .WithAccessibilityIdentifier($"native.vocabulary.duplicates.result.{index}"));

        var content = Views.VStack(items.ToArray())
            .WithPadding(SourceUiLayout.CardInset)
            .WithFlexibleWidth();
        content.Spacing = SourceUiLayout.FormGap;
        return style.Card(content)
            .WithAccessibilityIdentifier("native.vocabulary.duplicates");
    }

    private static SwiftView Group(
        VocabularyDuplicatePanel panel, NativeVocabularyDuplicateGroup group, int index,
        SourceUiStyle style, Func<string, string> text, VocabularyDuplicateActions actions)
    {
        var id = $"native.vocabulary.duplicates.group.{index}";
        var badges = new List<SwiftView> {
            style.Text(Views.Text(group.DisplayTerm), SourceTextRole.Body1Bold)
                .WithAccessibilityIdentifier($"{id}.term"),
            Badge(style, string.Format(CultureInfo.CurrentCulture,
                    text("Vocabulary_DuplicatesCopies"), group.Words.Count),
                style.Appearance.BootstrapSecondary, style.Appearance.OnBootstrapSecondary,
                $"{id}.copies")
        };
        if (!group.CanMergeAutomatically)
            badges.Add(Badge(style, text("NativeVocabulary_DuplicatesEditBeforeMerge"),
                style.Appearance.Warning, new("#212529", "#212529"), $"{id}.blocked"));
        if (panel.IsFocused(group))
            badges.Add(Badge(style, text("Vocabulary_DuplicatesFocusedBadge"),
                style.Appearance.Primary, style.Appearance.OnPrimary, $"{id}.focused"));
        var heading = Views.HStack(badges.ToArray())
            .WithAdaptiveStack(420)
            .WithFlexibleWidth();
        heading.Spacing = SourceUiLayout.ItemGap;
        var rows = new List<SwiftView> { heading };
        foreach (var word in group.Words)
            rows.Add(WordRow(group, word, panel.FocusedWordId, style, text, actions));
        if (!group.CanMergeAutomatically)
            rows.Add(WarningAlert(style, Views.VStack(
                    style.Text(Views.Text(text("NativeVocabulary_DuplicatesMergeUnavailable")),
                        SourceTextRole.Body2Strong),
                    style.Text(Views.Text(VocabularyDuplicateFinder.BlockedReason(group, text)),
                            SourceTextRole.Small)
                        .WithAccessibilityIdentifier($"{id}.reason"),
                    style.Text(Views.Text(text("NativeVocabulary_DuplicatesMergeUnavailableHelp")),
                        SourceTextRole.Small)))
                .WithAccessibilityIdentifier($"{id}.unavailable"));
        else
            rows.Add(style.OutlineButton(
                    Views.Text(text("NativeVocabulary_DuplicatesMergeKeepRecommended")),
                    () => actions.Merge(group.NormalizedTerm))
                .WithAccessibilityIdentifier($"{id}.merge"));
        var column = Views.VStack(rows.ToArray())
            .WithPadding(SourceUiLayout.CardInset)
            .WithFlexibleWidth();
        column.Spacing = SourceUiLayout.ItemGap;
        return style.BorderedCard(column, radius: 8)
            .WithAccessibilityIdentifier(id);
    }

    private static SwiftView WordRow(
        NativeVocabularyDuplicateGroup group, NativeVocabularyDuplicateWord word,
        string? focusedWordId, SourceUiStyle style, Func<string, string> text,
        VocabularyDuplicateActions actions)
    {
        var id = $"native.vocabulary.duplicates.word.{word.Id}";
        var keeper = word.Id == group.RecommendedKeeperId;
        var (encodingFill, encodingText) = EncodingColors(style.Appearance, word.EncodingLabel);
        var badges = new List<SwiftView> {
            style.Text(Views.Text(word.NativeTerm), SourceTextRole.Body2Strong)
                .WithAccessibilityIdentifier($"{id}.native"),
            Badge(style, text(VocabularyDuplicateFinder.EncodingKey(word.EncodingLabel)),
                encodingFill, encodingText, $"{id}.encoding")
        };
        if (keeper)
            badges.Add(Badge(style, text("Vocabulary_DuplicatesRecommendedKeeper"),
                style.Appearance.Success, style.Appearance.OnPrimary, $"{id}.keeper"));
        if (word.Id == focusedWordId)
            badges.Add(Badge(style, text("Vocabulary_DuplicatesCurrentWord"),
                style.Appearance.Primary, style.Appearance.OnPrimary, $"{id}.current"));
        var heading = Views.HStack(badges.ToArray())
            .WithAdaptiveStack(420)
            .WithFlexibleWidth();
        heading.Spacing = SourceUiLayout.ItemGap;
        var rows = new List<SwiftView> {
            heading,
            style.Text(Views.Text(VocabularyDuplicateFinder.MemoryAidSummary(word, text)),
                    SourceTextRole.Small, SourceTextTone.Secondary)
                .WithAccessibilityIdentifier($"{id}.aids")
        };
        if (keeper)
            rows.Add(style.Text(Views.Text(text("NativeVocabulary_DuplicatesKeeperNote")),
                SourceTextRole.Small, SourceTextTone.Success));
        if (!group.CanMergeAutomatically)
            rows.Add(style.OutlineButton(
                    Views.Text(text("NativeVocabulary_DuplicatesEditRecord")),
                    () => actions.EditRecord(word.Id))
                .WithAccessibilityIdentifier($"{id}.edit"));
        var column = Views.VStack(rows.ToArray())
            .WithPadding(8)
            .WithFlexibleWidth();
        column.Spacing = 4;
        return style.BorderedCard(column, radius: 6)
            .WithAccessibilityIdentifier(id);
    }

    /// <summary>Source GetEncodingBadgeClass (see <see cref="VocabularyEncodingBadge"/>).</summary>
    internal static (SourceColorPair Fill, SourceColorPair Text) EncodingColors(
        SourceUiAppearance appearance, string encodingLabel) =>
        appearance.EncodingBadge(VocabularyWordCommands.EncodingTone(encodingLabel));

    /// <summary>Bootstrap 5.3 warning emphasis text, as in the warning alert.</summary>
    private static readonly SourceColorPair WarningText = new("#664D03", "#FFDA6A");

    /// <summary>Source "alert alert-warning py-2" (Bootstrap 5.3 warning alert tokens).</summary>
    private static SwiftView WarningAlert(SourceUiStyle style, SwiftView content)
    {
        var column = content.WithForegroundColor(WarningText.Light, WarningText.Dark)
            .WithPadding(8, 16, 8, 16)
            .WithFlexibleWidth();
        return column.WithRoundedSurface("#FFF3CD", "#332701",
            style.Appearance.BootstrapRadius, "#FFE69C", "#997404", 1);
    }

    private static SwiftView Badge(SourceUiStyle style, string value, SourceColorPair fill,
        SourceColorPair foreground, string id) =>
        style.Text(Views.Text(value), SourceTextRole.BadgeSmall)
            .WithPadding(5, 10, 5, 10)
            .WithRoundedSurface(fill.Light, fill.Dark, 12)
            .WithForegroundColor(foreground.Light, foreground.Dark)
            .WithAccessibilityIdentifier(id);
}
