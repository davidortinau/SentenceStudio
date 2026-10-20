using System.Globalization;
using Microsoft.SwiftUI;
using SentenceStudio.Native;
using SentenceStudio.Native.iOS.Styling;
using SentenceStudio.Shared.Models;
using Views = Microsoft.SwiftUI.SwiftUI;

namespace SentenceStudio.Native.iOS.Features.Vocabulary;

internal sealed record VocabularyListActions(
    Action<string> Search,
    Action OpenFilters,
    Action ClearFilters,
    Action<string> OpenWord,
    Action Reload,
    Action<string> Sort,
    Action<bool> SetListMode,
    Action<string, string?> ChangeWideFilter);

internal sealed record VocabularyFilterActions(
    Action<string, string?> Change,
    Action Apply, Action Cancel, Action Clear);

internal static class VocabularyListView
{
    internal static SwiftView Page(
        VocabularyListPresentation state, SourceUiStyle style,
        Func<string, string> text, VocabularyListActions actions)
    {
        var children = new List<SwiftView>();
        var search = style.TextInput(Views.TextField(
                text("Vocabulary_SearchPlaceholder"),
                state.Filters.CommittedQuery, actions.Search))
            .WithFlexibleWidth()
            .WithAccessibilityIdentifier("native.vocabulary.search");
        var searchRow = new List<SwiftView> { search };
        if (state.Filters.HasActiveFilters)
        {
            searchRow.Add(Views.Button(Views.Image("xmark"),
                    actions.ClearFilters)
                .WithAccessibilityLabel(text("Vocabulary_ClearFilters"))
                .WithAccessibilityIdentifier("native.vocabulary.search.clear"));
        }
        var searchBar = Views.HStack(searchRow.ToArray())
            .WithFlexibleWidth()
            .WithAccessibilityIdentifier("native.vocabulary.search.row");
        searchBar.Spacing = SourceUiLayout.ItemGap;
        children.Add(searchBar);

        if (state.Loading)
            children.Add(Views.IndeterminateProgress(text("Common_Loading"))
                .WithAccessibilityIdentifier("native.vocabulary.status"));
        else if (state.Blocked || state.ErrorKey is not null)
        {
            children.Add(style.Alert(style.Text(
                    Views.Text(text(state.ErrorKey ?? "Vocabulary_NoActiveProfile")),
                    SourceTextRole.Body1), success: false)
                .WithAccessibilityIdentifier("native.vocabulary.status"));
            if (!state.Blocked)
                children.Add(Views.Button(text("Common_Retry"), actions.Reload)
                    .WithAccessibilityIdentifier("native.vocabulary.retry"));
        }
        else
        {
            if (style.ViewportWidth >= 768)
            {
                children.Add(WideControls(state, style, text, actions));
                children.Add(WideFilters(state, style, text,
                    actions.ChangeWideFilter));
                var chips = state.Filters.CommittedFilters.Select(filter =>
                    Views.Button($"{filter.Type}: {filter.Value}",
                            () => actions.ChangeWideFilter(filter.Type, null))
                        .WithAccessibilityIdentifier(
                            $"native.vocabulary.chip.{filter.Type}"))
                    .ToArray();
                if (chips.Length > 0)
                    children.Add(Views.HStack(chips)
                        .WithAdaptiveStack(680)
                        .WithAccessibilityIdentifier("native.vocabulary.chips"));
            }

            if (!string.IsNullOrWhiteSpace(state.Feedback))
                children.Add((state.FeedbackWarning
                        ? style.Alert(
                            style.Text(Views.Text(state.Feedback),
                                SourceTextRole.Body2), success: false)
                        : style.Text(Views.Text(state.Feedback),
                            SourceTextRole.Body2, SourceTextTone.Body))
                    .WithAccessibilityIdentifier("native.vocabulary.feedback"));
            if (state.FilteredWords.Count == 0)
            {
                children.Add(style.Text(Views.Text(text(state.Catalog.Words.Count == 0
                            ? "Vocabulary_NoWordsYet" : "Vocabulary_NoMatchFilter")),
                        SourceTextRole.Body1, SourceTextTone.Secondary)
                    .WithFlexibleWidth(alignment: SwiftHorizontalAlignment.Center)
                    .WithPadding(48)
                    .WithAccessibilityIdentifier("native.vocabulary.status"));
            }
            else
            {
                var list = Views.LazyVGrid(
                        state.ListMode && style.ViewportWidth >= 768
                            ? 1 : SourceUiLayout.ResourceColumns(style.ViewportWidth),
                        state.FilteredWords.Select(word =>
                            Card(word, style, text, actions.OpenWord)).ToArray())
                    .WithAccessibilityIdentifier("native.vocabulary.cards");
                list.Spacing = SourceUiLayout.PageGap;
                children.Add(list);
            }
        }
        var content = Views.VStack(children.ToArray())
            .WithPadding(SourceUiLayout.PageGutter(style.ViewportWidth))
            .WithFlexibleWidth()
            .WithAccessibilityIdentifier("native.vocabulary.scroll.content");
        content.Spacing = SourceUiLayout.PageGap;
        var page = Views.Scroll(content)
            .WithAccessibilityIdentifier("native.vocabulary.scroll");
        var toolbar = style.ViewportWidth < 768
            ? new[] { Views.Button(Views.Image(
                        state.Filters.HasActiveFilters
                            ? "line.3.horizontal.decrease.circle.fill"
                            : "line.3.horizontal.decrease.circle"),
                    actions.OpenFilters)
                .WithAccessibilityLabel(text("Vocabulary_Filters"))
                .WithAccessibilityIdentifier("native.vocabulary.filters.open") }
            : Array.Empty<SwiftView>();
        return Views.Toolbar(style.Page(page), toolbar)
            .WithAccessibilityIdentifier("native.vocabulary.page");
    }

    internal static SwiftView PhoneFilterSheet(
        VocabularyListPresentation state, SourceUiStyle style,
        Func<string, string> text, VocabularyFilterActions actions)
    {
        var fields = FilterFields(state, style, text, actions.Change,
            useDraft: true);
        var header = Views.HStack(
                style.Text(Views.Text(text("Vocabulary_Filters")),
                        SourceTextRole.Title3, sourceHeading: true)
                    .WithFlexibleWidth(),
                Views.Button(Views.Image("xmark"), actions.Cancel)
                    .WithAccessibilityLabel(text("Common_Close"))
                    .WithAccessibilityIdentifier("native.vocabulary.filters.close"))
            .WithAccessibilityIdentifier("native.vocabulary.filters.heading");
        fields.Insert(0, header);
        var clear = style.SecondaryButton(
                text("Vocabulary_ClearAllFilters"), actions.Clear)
            .WithAccessibilityIdentifier("native.vocabulary.filters.clear");
        var cancel = style.OutlineButton(
                Views.Text(text("Common_Cancel")), actions.Cancel)
            .WithAccessibilityIdentifier("native.vocabulary.filters.cancel");
        var apply = style.PrimaryButton(
                text("Vocabulary_BulkApply"), actions.Apply)
            .WithAccessibilityIdentifier("native.vocabulary.filters.apply");
        var buttons = Views.HStack(cancel, apply)
            .WithFlexibleWidth()
            .WithAccessibilityIdentifier("native.vocabulary.filters.actions");
        buttons.Spacing = SourceUiLayout.ItemGap;
        fields.Add(clear);
        fields.Add(buttons);
        var panel = Views.VStack(fields.ToArray())
            .WithPadding(SourceUiLayout.CardInset)
            .WithFlexibleWidth()
            .WithAccessibilityIdentifier("native.vocabulary.filters.sheet.content");
        panel.Spacing = SourceUiLayout.FormGap;
        return style.Page(Views.Scroll(panel))
            .WithAccessibilityIdentifier("native.vocabulary.filters.sheet");
    }

    private static SwiftView WideControls(
        VocabularyListPresentation state, SourceUiStyle style,
        Func<string, string> text, VocabularyListActions actions)
    {
        var total = state.FilteredWords.Count;
        var stats = new[] {
            string.Format(CultureInfo.CurrentCulture, text("Vocabulary_StatsTotal"),
                total, state.Catalog.Words.Count),
            string.Format(CultureInfo.CurrentCulture, text("Vocabulary_StatsUnknown"),
                state.FilteredWords.Count(word => word.Status == LearningStatus.Unknown)),
            string.Format(CultureInfo.CurrentCulture, text("Vocabulary_StatsLearning"),
                state.FilteredWords.Count(word => word.Status == LearningStatus.Learning)),
            string.Format(CultureInfo.CurrentCulture, text("Vocabulary_StatsFamiliar"),
                state.FilteredWords.Count(word => word.Status == LearningStatus.Familiar)),
            string.Format(CultureInfo.CurrentCulture, text("Vocabulary_StatsKnown"),
                state.FilteredWords.Count(word => word.Status == LearningStatus.Known))
        }.Select((value, index) =>
            style.Text(Views.Text(value), SourceTextRole.BadgeSmall)
                .WithPadding(6)
                .WithRoundedSurface(
                    index == 4 ? style.Appearance.Success.Light
                        : style.Appearance.BootstrapSecondary.Light,
                    index == 4 ? style.Appearance.Success.Dark
                        : style.Appearance.BootstrapSecondary.Dark, 12)
                .WithForegroundColor(
                    index == 4 ? style.Appearance.OnPrimary.Light
                        : style.Appearance.OnBootstrapSecondary.Light,
                    index == 4 ? style.Appearance.OnPrimary.Dark
                        : style.Appearance.OnBootstrapSecondary.Dark))
            .ToArray();
        var sort = Views.ButtonGroup([
                SortButton("added", "Vocabulary_SortAdded", state, style,
                    text, actions.Sort, SourceButtonGroupPosition.First),
                SortButton("updated", "Vocabulary_SortUpdated", state, style,
                    text, actions.Sort, SourceButtonGroupPosition.Last)
            ], selectedIndex: state.SortField == "updated" ? 1 : 0)
            .WithAccessibilityIdentifier("native.vocabulary.sort");
        var view = Views.ButtonGroup([
                style.ResourceViewModeButton("square.grid.3x3",
                        text("Vocabulary_GridView"), !state.ListMode,
                        () => actions.SetListMode(false),
                        SourceButtonGroupPosition.First)
                    .WithAccessibilityIdentifier("native.vocabulary.view.grid"),
                style.ResourceViewModeButton("list.bullet",
                        text("Vocabulary_ListView"), state.ListMode,
                        () => actions.SetListMode(true),
                        SourceButtonGroupPosition.Last)
                    .WithAccessibilityIdentifier("native.vocabulary.view.list")
            ], selectedIndex: state.ListMode ? 1 : 0)
            .WithAccessibilityIdentifier("native.vocabulary.view");
        var controls = Views.HStack([..stats, sort, view])
            .WithAdaptiveStack(940)
            .WithFlexibleWidth()
            .WithAccessibilityIdentifier("native.vocabulary.wide.controls");
        controls.Spacing = SourceUiLayout.ItemGap;
        return controls;
    }

    private static SwiftView SortButton(
        string sort, string labelKey,
        VocabularyListPresentation state, SourceUiStyle style,
        Func<string, string> text, Action<string> changed,
        SourceButtonGroupPosition position) =>
        state.SortField == sort
            ? style.PrimaryButton(
                    $"{text(labelKey)} {(state.SortDescending ? "↓" : "↑")}",
                    () => changed(sort), groupPosition: position)
                .WithAccessibilityIdentifier($"native.vocabulary.sort.{sort}")
            : style.OutlineButton(Views.Text(text(labelKey)),
                    () => changed(sort), groupPosition: position)
                .WithAccessibilityIdentifier($"native.vocabulary.sort.{sort}");

    private static SwiftView WideFilters(
        VocabularyListPresentation state, SourceUiStyle style,
        Func<string, string> text, Action<string, string?> changed)
    {
        var fields = FilterFields(state, style, text, changed, useDraft: false);
        var filters = Views.LazyVGrid(style.ViewportWidth < 1024 ? 2 : 4,
                fields.ToArray())
            .WithAccessibilityIdentifier("native.vocabulary.wide.filters");
        filters.Spacing = SourceUiLayout.ItemGap;
        return filters;
    }

    private static List<SwiftView> FilterFields(
        VocabularyListPresentation state, SourceUiStyle style,
        Func<string, string> text, Action<string, string?> changed, bool useDraft)
    {
        var words = state.Catalog.Words;
        var resources = state.Catalog.Resources;
        var tags = words
            .Where(word => !string.IsNullOrWhiteSpace(word.Tags))
            .SelectMany(word => word.Tags!.Split(',',
                StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(value => value, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
        var lemmas = words.Select(word => word.Lemma)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(value => value, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
        var languages = words
            .Select(word => word.Language ?? word.AssociatedResources
                .FirstOrDefault(resource =>
                    !string.IsNullOrWhiteSpace(resource.Language))?.Language)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(value => value, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
        var fields = new List<SwiftView> {
            Filter("association", "Vocabulary_Association",
                "Vocabulary_AllWords", ["associated", "orphaned"], state,
                style, text, changed, useDraft,
                value => text(value == "associated"
                    ? "Vocabulary_Associated" : "Vocabulary_Orphaned")),
            Filter("type", "NativeResources_TypeFilter", "Vocabulary_FilterTypeAll",
                ["word", "phrase", "sentence"], state, style, text,
                changed, useDraft, value => text(value switch {
                    "word" => "Vocabulary_FilterTypeWord",
                    "phrase" => "Vocabulary_FilterTypePhrase",
                    _ => "Vocabulary_FilterTypeSentence"
                })),
            Filter("status", "Vocabulary_StatusLabel", "Vocabulary_AllStatus",
                ["known", "familiar", "learning", "unknown"], state,
                style, text, changed, useDraft,
                value => text(value switch {
                    "known" => "Vocabulary_Known",
                    "familiar" => "Vocabulary_Familiar",
                    "learning" => "Vocabulary_Learning",
                    _ => "Vocabulary_Unknown"
                }))
        };
        if (resources.Count > 0)
            fields.Add(Filter("resource", "Vocabulary_ResourceLabel",
                "Vocabulary_AllResources",
                resources.Select(resource => resource.Title)
                    .Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
                state, style, text, changed, useDraft));
        if (tags.Length > 0)
            fields.Add(Filter("tag", "Vocabulary_TagLabel",
                "Vocabulary_AllTags", tags, state, style, text, changed, useDraft));
        if (lemmas.Length > 0)
            fields.Add(Filter("lemma", "Vocabulary_LemmaLabel",
                "Vocabulary_AllLemmas", lemmas, state, style, text,
                changed, useDraft));
        if (languages.Length > 0)
            fields.Add(Filter("language", "Vocabulary_LanguageLabel",
                "Vocabulary_AllLanguages", languages, state, style, text,
                changed, useDraft));
        fields.Add(Filter("encoding", "Vocabulary_EncodingStrength",
            "Vocabulary_AllEncoding", ["strong", "good", "basic"],
            state, style, text, changed, useDraft,
            value => text(value switch {
                "strong" => "Vocabulary_Strong",
                "good" => "Vocabulary_Good",
                _ => "Vocabulary_Basic"
            })));
        return fields;
    }

    private static SwiftView Filter(
        string type, string labelKey, string allKey,
        IReadOnlyList<string> values,
        VocabularyListPresentation state, SourceUiStyle style,
        Func<string, string> text, Action<string, string?> changed,
        bool useDraft, Func<string, string>? valueLabel = null)
    {
        var current = state.Filters.FilterValue(type, useDraft);
        var knownValueCount = values.Count;
        if (!string.IsNullOrWhiteSpace(current)
            && !values.Any(value => value.Equals(
                current, StringComparison.OrdinalIgnoreCase)))
            values = [..values, current];
        var options = new[] { text(allKey) }
            .Concat(values.Select((value, index) =>
                index < knownValueCount
                    ? valueLabel?.Invoke(value) ?? value : value))
            .ToArray();
        var index = values
            .Select((value, position) => (value, position))
            .FirstOrDefault(item => item.Item1.Equals(
                current, StringComparison.OrdinalIgnoreCase),
                (string.Empty, -1)).Item2 + 1;
        var picker = style.Input(Views.Picker(
                text(labelKey), options, index,
                selection => changed(type,
                    selection is > 0 && selection <= values.Count
                        ? values[selection - 1] : null))
            .WithPickerStyle(SwiftPickerStyle.Menu)
            .WithFlexibleWidth())
            .WithAccessibilityLabel(text(labelKey))
            .WithAccessibilityIdentifier($"native.vocabulary.filter.{type}");
        return Views.VStack(
                style.Text(Views.Text(text(labelKey)),
                    SourceTextRole.Body2, SourceTextTone.Secondary),
                picker)
            .WithFlexibleWidth()
            .WithAccessibilityIdentifier($"native.vocabulary.filter.field.{type}");
    }

    private static SwiftView Card(
        NativeVocabularyEntry word, SourceUiStyle style,
        Func<string, string> text, Action<string> open)
    {
        var title = style.Text(Views.Text(word.TargetTerm),
                SourceTextRole.Title3, sourceHeading: true)
            .WithFlexibleWidth()
            .WithAccessibilityIdentifier($"native.vocabulary.card.{word.Id}.target");
        var native = style.Text(Views.Text(word.NativeTerm),
                SourceTextRole.Body2, SourceTextTone.Secondary)
            .WithAccessibilityIdentifier($"native.vocabulary.card.{word.Id}.native");
        var status = style.Text(Views.Text(text(StatusKey(word))),
                SourceTextRole.BadgeSmall)
            .WithPadding(5, 10, 5, 10)
            .WithRoundedSurface(
                word.IsKnown ? style.Appearance.Success.Light
                    : style.Appearance.BootstrapSecondary.Light,
                word.IsKnown ? style.Appearance.Success.Dark
                    : style.Appearance.BootstrapSecondary.Dark, 12)
            .WithForegroundColor(
                word.IsKnown ? style.Appearance.OnPrimary.Light
                    : style.Appearance.OnBootstrapSecondary.Light,
                word.IsKnown ? style.Appearance.OnPrimary.Dark
                    : style.Appearance.OnBootstrapSecondary.Dark)
            .WithAccessibilityIdentifier($"native.vocabulary.card.{word.Id}.status");
        var heading = Views.HStack(Views.VStack(title, native).WithFlexibleWidth(),
                status)
            .WithFlexibleWidth();
        heading.Spacing = SourceUiLayout.ItemGap;
        var metadata = new List<string> {
            text(word.AssociatedResources.Count == 0
                ? "Vocabulary_OrphanedBadge" : "Vocabulary_ResourcesCount")
        };
        if (word.AssociatedResources.Count > 0)
            metadata[0] = string.Format(CultureInfo.CurrentCulture,
                metadata[0], word.AssociatedResources.Count);
        if (!string.IsNullOrWhiteSpace(word.Lemma))
            metadata.Add(word.Lemma);
        if (!string.IsNullOrWhiteSpace(word.Tags))
            metadata.AddRange(word.Tags.Split(',',
                StringSplitOptions.TrimEntries |
                StringSplitOptions.RemoveEmptyEntries).Take(3));
        var details = style.Text(Views.Text(
                $"{text(word.EncodingLabel switch {
                    "Strong" => "Vocabulary_Strong",
                    "Good" => "Vocabulary_Good",
                    _ => "Vocabulary_Basic"
                })} · {string.Join(" · ", metadata)}"),
                SourceTextRole.Small, SourceTextTone.Secondary)
            .WithAccessibilityIdentifier($"native.vocabulary.card.{word.Id}.details");
        var card = style.Card(Views.VStack(heading, details)
                .WithPadding(SourceUiLayout.CardInset)
                .WithFlexibleWidth());
        card.Spacing = SourceUiLayout.ItemGap;
        return Views.Button(card, () => open(word.Id))
            .WithButtonStyle(SwiftButtonStyle.Plain)
            .WithMinimumHeight(SourceUiLayout.MinimumTouchHeight)
            .WithAccessibilityIdentifier($"native.vocabulary.card.{word.Id}")
            .WithAccessibilityLabel(
                $"{word.TargetTerm}, {word.NativeTerm}, {text(StatusKey(word))}");
    }

    private static string StatusKey(NativeVocabularyEntry word) =>
        word.IsKnown ? "Vocabulary_Known" : word.Status switch {
            LearningStatus.Familiar => "Vocabulary_Familiar",
            LearningStatus.Learning => "Vocabulary_Learning",
            LearningStatus.Known => "Vocabulary_Known",
            _ => "Vocabulary_Unknown"
        };
}
