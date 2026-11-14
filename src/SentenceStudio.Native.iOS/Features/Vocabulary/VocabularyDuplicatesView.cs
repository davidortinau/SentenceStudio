using System.Globalization;
using Microsoft.SwiftUI;
using SentenceStudio.Native;
using SentenceStudio.Native.iOS.Styling;
using Views = Microsoft.SwiftUI.SwiftUI;

namespace SentenceStudio.Native.iOS.Features.Vocabulary;

/// <param name="ToggleCopy">A copy (term, word ID) goes into, or out of, its group's Merge.</param>
/// <param name="EditCopy">Opens a copy (word ID) in the word editor, above the page.</param>
internal sealed record VocabularyDuplicateActions(
    Action MergeAll, Action ShowMore, Action<string> Merge, Action<string> Skip,
    Action<string, string>? ToggleCopy = null, Action<string>? EditCopy = null);

/// <summary>
/// The duplicate page (docs/duplicate-merge-plan.md), pushed beside the Vocabulary list (the
/// Library split view's detail column, full width on a phone): one Merge button for every safe
/// group, then the groups that need review (what merging would lose, Merge or Skip) and the
/// ones that cannot merge (why). A grouped Form of system rows and buttons.
/// </summary>
internal static class VocabularyDuplicatesView
{
    internal const string Prefix = "native.vocabulary.duplicates";

    /// <summary>
    /// The page's Form, whose Sections the scene swaps in place: NativeForm.Page gives the Form
    /// the screen's ID (as Import's ScrollId).
    /// </summary>
    internal const string FormId = Prefix;

    internal static SwiftView Page(
        VocabularyDuplicatePanel panel, SourceUiStyle style,
        Func<string, string> text, VocabularyDuplicateActions actions)
    {
        var sections = new List<SwiftView> { Summary(panel, style, text, actions) };
        var review = panel.ReviewGroups;
        if (review.Count > 0)
        {
            sections.Add(Views.Section(text("Vocabulary_DuplicatesNeedReview"),
                    style.Text(Views.Text(text("Vocabulary_DuplicatesNeedReviewNote")),
                            SourceTextRole.Small, SourceTextTone.Secondary)
                        .WithFlexibleWidth()
                        .WithAccessibilityIdentifier($"{Prefix}.review.note")));
            foreach (var group in review)
                sections.Add(ReviewGroup(panel, group, style, text, actions));
        }
        var blocked = panel.BlockedGroups;
        if (blocked.Count > 0)
            sections.Add(Views.Section(text("Vocabulary_DuplicatesCantMerge"),
                    blocked.Select(group => BlockedRow(panel, group, style, text, actions)).ToArray()));
        if (!panel.Running && panel.RemainingGroupCount > 0)
            sections.Add(Views.Section(string.Empty,
                NativeForm.Bordered(style, string.Format(CultureInfo.CurrentCulture,
                        text("Vocabulary_DuplicatesShowMore"), panel.RemainingGroupCount),
                    actions.ShowMore, $"{Prefix}.more")));
        return NativeForm.Page(style, sections, FormId, Prefix);
    }

    /// <summary>
    /// The scan's status, the focus note, the safe groups' count and the one Merge button for
    /// all of them, then what the merges did.
    /// </summary>
    private static SwiftView Summary(VocabularyDuplicatePanel panel, SourceUiStyle style,
        Func<string, string> text, VocabularyDuplicateActions actions)
    {
        var rows = new List<SwiftView>();
        if (panel.Running)
            rows.Add(Views.IndeterminateProgress(panel.Status)
                .WithAccessibilityIdentifier($"{Prefix}.status"));
        else
            rows.Add(NativeForm.Notice(style, panel.Status, $"{Prefix}.status", error: false));
        if (!string.IsNullOrWhiteSpace(panel.FocusLabel))
            rows.Add(style.Text(Views.Text(VocabularyDuplicateFinder.FocusNote(panel.FocusLabel, text)),
                    SourceTextRole.Small, SourceTextTone.Secondary)
                .WithFlexibleWidth()
                .WithAccessibilityIdentifier($"{Prefix}.focus"));
        if (panel.CanMergeAll)
        {
            rows.Add(style.Text(Views.Text(Format(text, "Vocabulary_DuplicatesSafeSummary",
                        panel.SafeGroupCount, panel.MergeAllCopyCount)), SourceTextRole.Body1)
                .WithFlexibleWidth()
                .WithAccessibilityIdentifier($"{Prefix}.summary"));
            rows.Add(NativeForm.CenteredRow(NativeForm.Prominent(style,
                Format(text, "Vocabulary_DuplicatesMergeAll", panel.MergeAllCopyCount),
                actions.MergeAll, $"{Prefix}.mergeall")));
        }
        else if (!panel.Running && panel.Groups.Count > 0)
            rows.Add(style.Text(Views.Text(text("Vocabulary_DuplicatesNothingSafe")),
                    SourceTextRole.Body1)
                .WithFlexibleWidth()
                .WithAccessibilityIdentifier($"{Prefix}.summary"));
        if (!panel.Running)
            for (var index = 0; index < panel.Results.Count; index++)
                rows.Add(NativeForm.Notice(style, panel.Results[index],
                    $"{Prefix}.result.{index}", error: false));
        return Views.Section(string.Empty, rows.ToArray());
    }

    /// <summary>
    /// A group to review, titled by its term: what merging would lose (a warning symbol and
    /// words, not color alone), its copies with the keeper first, then Merge and Skip. With three
    /// or more copies each copy is a choice (Captain 2026-11-14: "merge some matching duplicates
    /// but not all"): Merge folds the chosen ones into the first chosen copy.
    /// </summary>
    private static SwiftView ReviewGroup(VocabularyDuplicatePanel panel,
        NativeVocabularyDuplicateGroup group, SourceUiStyle style, Func<string, string> text,
        VocabularyDuplicateActions actions)
    {
        var id = GroupId(group);
        var differences = string.Join(", ", VocabularyDuplicateFinder.Differences(group, text));
        var warning = Views.HStack(
                Views.Image("exclamationmark.triangle.fill")
                    .WithForegroundColor(style.Appearance.Warning.Light, style.Appearance.Warning.Dark),
                style.Text(Views.Text(differences), SourceTextRole.Body2Strong)
                    .WithFlexibleWidth()
                    .WithAccessibilityIdentifier($"{id}.differences"))
            .WithFlexibleWidth();
        warning.Spacing = SourceUiLayout.ItemGap;
        var rows = new List<SwiftView> { warning };
        var choice = VocabularyDuplicatePanel.OffersChoice(group) && actions.ToggleCopy is not null;
        if (choice)
            rows.Add(style.Text(Views.Text(text("Vocabulary_DuplicatesChooseHint")),
                    SourceTextRole.Small, SourceTextTone.Secondary)
                .WithFlexibleWidth()
                .WithAccessibilityIdentifier($"{id}.hint"));
        var keeper = panel.KeeperOf(group);
        var copies = VocabularyDuplicateFinder.KeeperFirst(group, keeper);
        for (var index = 0; index < copies.Count; index++)
        {
            var word = copies[index];
            // Identical copies read the same, so VoiceOver also says which one (copy 2 of 3).
            var position = Format(text, "Vocabulary_DuplicatesCopyPosition", index + 1, copies.Count);
            rows.Add(WithEdit(choice
                    ? ChoiceRow(panel, group, word, keeper, position, style, text, actions.ToggleCopy!)
                    : CopyRow(panel, group, word, keeper, style, text),
                panel, word, position, style, text, actions.EditCopy));
        }
        var merge = NativeForm.Bordered(style, text("Vocabulary_DuplicatesMerge"),
            () => actions.Merge(group.NormalizedTerm), $"{id}.merge");
        var skip = NativeForm.Bordered(style, text("Vocabulary_DuplicatesSkip"),
            () => actions.Skip(group.NormalizedTerm), $"{id}.skip");
        merge.IsEnabled = !panel.Running && panel.Chosen(group).Count >= 2;
        skip.IsEnabled = !panel.Running;
        var buttons = Views.HStack(merge, skip).WithFlexibleWidth();
        buttons.Spacing = SourceUiLayout.ItemGap;
        rows.Add(buttons);
        // No ID on the Section: SwiftUI gives a Section's ID to every row in it, over the rows' own.
        return Views.Section(group.DisplayTerm, rows.ToArray());
    }

    /// <summary>
    /// A copy's row with Edit beside it (Captain 2026-11-14: "go directly to a vocabulary word so
    /// that I can modify the entry"): the word editor opens above the page. Edit is its own
    /// borderless button, so a tap on the copy itself still chooses it.
    /// </summary>
    private static SwiftView WithEdit(SwiftView copy, VocabularyDuplicatePanel panel,
        NativeVocabularyDuplicateWord word, string position, SourceUiStyle style,
        Func<string, string> text, Action<string>? editCopy)
    {
        if (editCopy is null)
            return copy;
        var label = text("Vocabulary_DuplicatesEdit");
        var edit = Views.Button(Views.Label(label, "pencil"), () => editCopy(word.Id))
            .WithButtonStyle(SwiftButtonStyle.Bordered)
            .WithTintColor(style.Appearance.Primary.Light, style.Appearance.Primary.Dark)
            .WithForegroundColor(style.Appearance.Primary.Light, style.Appearance.Primary.Dark)
            .WithAccessibilityLabel($"{label}, {word.NativeTerm}, {position}")
            .WithAccessibilityIdentifier($"{Prefix}.word.{word.Id}.edit");
        edit.IsEnabled = !panel.Running;
        var row = Views.HStack(copy.WithFlexibleWidth(), edit).WithFlexibleWidth();
        row.Spacing = SourceUiLayout.ItemGap;
        return row;
    }

    /// <summary>
    /// A copy the learner can leave out of the Merge: the whole row is one button with a checkmark
    /// circle (chosen) or an empty circle (left out), selected for VoiceOver when chosen.
    /// </summary>
    private static SwiftView ChoiceRow(VocabularyDuplicatePanel panel,
        NativeVocabularyDuplicateGroup group, NativeVocabularyDuplicateWord word, string keeperId,
        string position, SourceUiStyle style, Func<string, string> text, Action<string, string> toggle)
    {
        var included = panel.IsIncluded(word);
        var mark = (included ? style.Appearance.Primary : style.Appearance.SecondaryText);
        var label = Views.HStack(
                Views.Image(included ? "checkmark.circle.fill" : "circle")
                    .WithFixedSymbolSize(22)
                    .WithForegroundColor(mark.Light, mark.Dark),
                CopyRow(panel, group, word, keeperId, style, text))
            .WithFlexibleWidth();
        label.Spacing = SourceUiLayout.ItemGap;
        var spoken = new List<string> { word.NativeTerm, position,
            VocabularyDuplicateFinder.CopyDetails(group, word, text).Replace('\n', ' ') };
        if (word.Id == keeperId)
            spoken.Add(text("Vocabulary_DuplicatesKeep"));
        var button = Views.Button(label, () => toggle(group.NormalizedTerm, word.Id))
            .WithButtonStyle(SwiftButtonStyle.Plain)
            .WithFlexibleWidth()
            .WithAccessibilitySelected(included)
            .WithAccessibilityLabel(string.Join(", ", spoken))
            .WithAccessibilityIdentifier($"{Prefix}.word.{word.Id}.choose");
        button.IsEnabled = !panel.Running;
        return button;
    }

    /// <summary>A copy: its meaning, what differs and its memory aids, and which copy Merge keeps.</summary>
    private static SwiftView CopyRow(VocabularyDuplicatePanel panel,
        NativeVocabularyDuplicateGroup group, NativeVocabularyDuplicateWord word, string keeperId,
        SourceUiStyle style, Func<string, string> text)
    {
        var id = $"{Prefix}.word.{word.Id}";
        var keeper = word.Id == keeperId;
        var rows = new List<SwiftView>
        {
            style.Text(Views.Text(word.NativeTerm), SourceTextRole.Body1,
                    strongElement: keeper)
                .WithFlexibleWidth()
                .WithAccessibilityIdentifier($"{id}.native"),
            style.Text(Views.Text(VocabularyDuplicateFinder.CopyDetails(group, word, text)),
                    SourceTextRole.Small, SourceTextTone.Secondary)
                .WithFlexibleWidth()
                .WithAccessibilityIdentifier($"{id}.details")
        };
        if (keeper)
            rows.Add(Marker(style, "checkmark.circle.fill", text("Vocabulary_DuplicatesKeep"),
                style.Appearance.Success, SourceTextTone.Success, $"{id}.keeper"));
        if (word.Id == panel.FocusedWordId)
            rows.Add(Marker(style, "scope", text("Vocabulary_DuplicatesCurrentWord"),
                style.Appearance.Primary, SourceTextTone.Primary, $"{id}.current"));
        var column = Views.VStack(rows.ToArray()).WithFlexibleWidth();
        column.Spacing = 2;
        return column.WithAccessibilityIdentifier(id);
    }

    /// <summary>
    /// A group whose copies cannot all merge: its term and copies, and why. With a copy to keep,
    /// Merge still moves the learner's history and resources onto it (the shared copies stay).
    /// </summary>
    private static SwiftView BlockedRow(VocabularyDuplicatePanel panel,
        NativeVocabularyDuplicateGroup group, SourceUiStyle style, Func<string, string> text,
        VocabularyDuplicateActions actions)
    {
        var id = GroupId(group);
        var rows = new List<SwiftView>
        {
            style.Text(Views.Text($"{group.DisplayTerm} ({Format(text, "Vocabulary_DuplicatesCopies", group.Words.Count)})"),
                    SourceTextRole.Body1Strong)
                .WithFlexibleWidth()
                .WithAccessibilityIdentifier($"{id}.term"),
            style.Text(Views.Text(VocabularyDuplicateFinder.BlockedReason(group, text)),
                    SourceTextRole.Small, SourceTextTone.Secondary)
                .WithFlexibleWidth()
                .WithAccessibilityIdentifier($"{id}.reason")
        };
        if (!string.IsNullOrWhiteSpace(group.RecommendedKeeperId))
        {
            var merge = NativeForm.Bordered(style, text("Vocabulary_DuplicatesMerge"),
                () => actions.Merge(group.NormalizedTerm), $"{id}.merge");
            merge.IsEnabled = !panel.Running;
            rows.Add(merge);
        }
        var column = Views.VStack(rows.ToArray()).WithFlexibleWidth();
        column.Spacing = 4;
        return column.WithAccessibilityIdentifier(id);
    }

    /// <summary>A symbol and its words (the state is never told by color alone).</summary>
    private static SwiftView Marker(SourceUiStyle style, string symbol, string label,
        SourceColorPair color, SourceTextTone tone, string id)
    {
        var row = Views.HStack(
                Views.Image(symbol).WithForegroundColor(color.Light, color.Dark),
                style.Text(Views.Text(label), SourceTextRole.Small, tone)
                    .WithAccessibilityIdentifier(id))
            .WithFlexibleWidth();
        row.Spacing = 4;
        return row;
    }

    /// <summary>A group's automation ID, by its term (stable while other groups merge).</summary>
    internal static string GroupId(NativeVocabularyDuplicateGroup group) =>
        $"{Prefix}.group.{Uri.EscapeDataString(group.NormalizedTerm)}";

    private static string Format(Func<string, string> text, string key, params object[] values) =>
        string.Format(CultureInfo.CurrentCulture, text(key), values);
}
