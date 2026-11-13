using System.Globalization;
using Microsoft.SwiftUI;
using SentenceStudio.Native;
using SentenceStudio.Native.iOS.Features.Browse;
using SentenceStudio.Native.iOS.Styling;
using SentenceStudio.Shared.Models;
using Views = Microsoft.SwiftUI.SwiftUI;

namespace SentenceStudio.Native.iOS.Features.Vocabulary;

internal sealed record VocabularyListActions(
    Action<string> Search,
    Action OpenFilters,
    Action ToggleFilterPanel,
    Action ClearFilters,
    Action<string> OpenWord,
    Action Reload,
    Action<string> Sort,
    Action ToggleListMode,
    Action<string, string?> ChangeWideFilter,
    Action? OpenLookups = null,
    VocabularyDuplicateActions? Duplicates = null,
    Action<double>? PageWidthChanged = null,
    Action? AddWord = null,
    Action<string>? SearchField = null,
    Action<IReadOnlyList<string>>? KeepChips = null);

internal sealed record VocabularyFilterActions(
    Action<string, string?> Change,
    Action<string> Sort,
    Action Apply, Action Cancel, Action Clear);

/// <summary>
/// The Vocabulary list on the shared browse layout (<see cref="BrowserPage"/>, Captain
/// 2026-11-09): one scrolling column on a phone with filters and sort in a sheet; wide, a fixed
/// filter column beside the results, whose own scroll holds one row of counts above the lazily
/// built grid. The toolbar holds Recent lookups, the grid/list switch (wide only), Filters and
/// Add (+).
/// </summary>
/// <remarks>
/// The scene updates a mounted page in place (the search field keeps focus while typing), so a
/// toolbar button's action must not depend on the state it was built with, and its symbol, label
/// and selected state are the only parts the scene changes on it.
/// </remarks>
internal static class VocabularyListView
{
    private const string Prefix = "native.vocabulary";

    internal static bool IsWide(double viewportWidth) => BrowserPage.IsWide(viewportWidth);

    internal static double FilterColumnWidth(double viewportWidth, double textScale = 1) =>
        BrowserPage.FilterColumnWidth(viewportWidth, textScale);

    internal static int CardColumns(double width, double textScale,
        bool listMode, bool filterPanelShown, bool wide) =>
        BrowserPage.CardColumns(width, textScale, listMode, filterPanelShown, wide);

    /// <param name="pageWidth">
    /// The page's own width when it has reported one (<see cref="VocabularyListActions.PageWidthChanged"/>):
    /// an iPad sidebar takes part of the window, so the phone or wide layout, the filter column
    /// and the card columns follow it. Before it reports, the window's width stands in.
    /// </param>
    internal static SwiftView Page(
        VocabularyBrowserList state, SourceUiStyle style,
        Func<string, string> text, VocabularyListActions actions, double? pageWidth = null)
    {
        var width = BrowserPage.Width(pageWidth, style);
        var wide = IsWide(width);
        var ready = !state.Loading && !state.Blocked && state.ErrorKey is null;
        var page = wide && ready
            ? WideLayout(state, style, text, actions, width)
            : SingleColumn(state, style, text, actions, width);
        return BrowserPage.WithToolbar(Prefix, page, style, actions.PageWidthChanged,
            ToolbarActions(state, text, actions, wide, ready));
    }

    internal static SwiftView PhoneFilterSheet(
        VocabularyBrowserList state, SourceUiStyle style,
        Func<string, string> text, VocabularyFilterActions actions)
    {
        var fields = FilterFields(state, style, text, actions.Change, useDraft: true);
        // Captain 2026-11-09: sorting is one of the filter options (applied with them).
        fields.Insert(0, BrowserPage.SortField(Prefix, state.SheetSortField,
            state.SheetSortDescending, style, text, actions.Sort));
        return BrowserPage.FilterSheet(Prefix, style, text, fields,
            actions.Clear, actions.Cancel, actions.Apply);
    }

    private static SwiftView[] ToolbarActions(
        VocabularyBrowserList state, Func<string, string> text,
        VocabularyListActions actions, bool wide, bool ready)
    {
        var shown = new List<SwiftView>();
        // Recent lookups (decision 4 B): words looked up in Writing and Reading.
        if (actions.OpenLookups is { } openLookups)
            shown.Add(Views.Button(Views.Image("clock.arrow.circlepath"), openLookups)
                .WithAccessibilityLabel(text("Lookups_Title"))
                .WithAccessibilityIdentifier(
                    SentenceStudio.Native.Features.Lookups.RecentLookupsAccess.Open));
        // A phone always shows one column, so it has no grid/list switch; no words or column to
        // arrange while the list loads or cannot show.
        if (wide)
            shown.Add(BrowserPage.ViewToggle(Prefix, state.ListMode, actions.ToggleListMode,
                text("Vocabulary_GridView"), text("Vocabulary_ListView"), ready));
        // Wide: the filter column's show/hide. Otherwise (a phone, the Library split view's list
        // column) a native menu: sort and every filter, applied at once.
        shown.Add(wide
            ? BrowserPage.FiltersButton(Prefix, text("Vocabulary_Filters"),
                state.Filters.HasActiveFilters, wide, ready, !state.FilterPanelHidden,
                actions.OpenFilters, actions.ToggleFilterPanel)
            : BrowserPage.FilterMenu(Prefix, text("Vocabulary_Filters"),
                state.Filters.HasActiveFilters, enabled: true,
                FilterMenuItems(state, text, actions)));
        // Captain 2026-11-10: Add (+) on every browse page (source Vocabulary Add).
        if (actions.AddWord is { } addWord)
            // It waits for the list, which reads the resources a new word can join.
            shown.Add(BrowserPage.AddButton(Prefix, text("Vocabulary_Add"), addWord, ready));
        return shown.ToArray();
    }

    /// <summary>The filter menu's items: Sort, one submenu per filter, and Clear All Filters while one is on.</summary>
    private static IEnumerable<SwiftView> FilterMenuItems(
        VocabularyBrowserList state, Func<string, string> text, VocabularyListActions actions)
    {
        var items = new List<SwiftView> {
            BrowserPage.SortMenuSection(Prefix, state.SortField, state.SortDescending, text,
                actions.Sort)
        };
        foreach (var filter in FilterChoices(state, text))
            items.Add(BrowserPage.FilterSubmenu(Prefix, filter.Type, text(filter.LabelKey),
                text(filter.AllKey), filter.Values, state.Filters.FilterValue(filter.Type, false),
                value => actions.ChangeWideFilter(filter.Type, value), filter.ValueLabel));
        items.AddRange(BrowserPage.ClearAllMenuItems(Prefix, text, actions.ClearFilters,
            state.Filters.HasActiveFilters));
        return items;
    }

    /// <summary>The wide page's search row (the whole query as text, and clear).</summary>
    private static SwiftView SearchRow(
        VocabularyBrowserList state, SourceUiStyle style,
        Func<string, string> text, VocabularyListActions actions)
    {
        var search = style.TextInput(Views.TextField(
                text("Vocabulary_SearchPlaceholder"),
                state.Filters.CommittedQuery, actions.Search))
            .WithFlexibleWidth()
            .WithAccessibilityIdentifier("native.vocabulary.search");
        var clear = state.Filters.HasActiveFilters
            ? Views.Button(Views.Image("xmark"), actions.ClearFilters)
                .WithAccessibilityLabel(text("Vocabulary_ClearFilters"))
                .WithAccessibilityIdentifier("native.vocabulary.search.clear")
            : null;
        return BrowserPage.SearchRow(Prefix, search, clear);
    }

    /// <summary>The phone page, and the wide page while it loads or cannot show words.</summary>
    private static SwiftView SingleColumn(
        VocabularyBrowserList state, SourceUiStyle style,
        Func<string, string> text, VocabularyListActions actions, double width)
    {
        var children = new List<SwiftView>();
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
            AddResults(children, state, style, text, actions, width, wide: false);
        // A wide page shows this while it loads: its search stays at the top.
        if (IsWide(width))
            return BrowserPage.SingleColumnWithTopSearch(Prefix, children,
                SearchRow(state, style, text, actions), width);
        // No search while the owner is unknown.
        if (state.Blocked)
            return BrowserPage.SingleColumn(Prefix, children, width, search: null);
        // The system search field: the typed words, and the filters as tokens in the field.
        return BrowserPage.SingleColumn(Prefix, children, width, new BrowserPage.PageSearch(
            text("Vocabulary_SearchPlaceholder"), state.Filters.FieldText,
            actions.SearchField ?? actions.Search, SearchTokens(state, text),
            remaining => actions.KeepChips?.Invoke(remaining)));
    }

    /// <summary>
    /// The search field's tokens: the filters chosen from the menu (or restored), each named
    /// with its value as the menu names it.
    /// </summary>
    private static List<SwiftSearchToken> SearchTokens(
        VocabularyBrowserList state, Func<string, string> text)
    {
        if (state.Filters.ChipFilters.Count == 0)
            return [];
        var choices = FilterChoices(state, text);
        return state.Filters.ChipFilters.Select(filter =>
            {
                var choice = choices.FirstOrDefault(item =>
                    item.Type.Equals(filter.Type, StringComparison.OrdinalIgnoreCase));
                var known = choice?.Values.Any(value =>
                    value.Equals(filter.Value, StringComparison.OrdinalIgnoreCase)) == true;
                var title = known && choice!.ValueLabel is { } name ? name(filter.Value) : filter.Value;
                return new SwiftSearchToken(VocabularyFilterState.TokenId(filter), title);
            })
            .DistinctBy(token => token.Id)
            .ToList();
    }

    private static SwiftView WideLayout(
        VocabularyBrowserList state, SourceUiStyle style,
        Func<string, string> text, VocabularyListActions actions, double width)
    {
        var headerRows = new List<SwiftView> { StatsRow(state, style, text) };
        if (state.Filters.CommittedFilters.Count > 0)
            headerRows.Add(Chips(state, actions));
        var results = new List<SwiftView>();
        AddResults(results, state, style, text, actions, width, wide: true);
        return BrowserPage.Wide(Prefix, style,
            state.FilterPanelHidden ? null : FilterPanel(state, style, text, actions, width),
            SearchRow(state, style, text, actions),
            BrowserPage.ResultsHeader(Prefix, headerRows), results);
    }

    /// <summary>
    /// The wide filter column: sort, every filter (applied at once) and Clear All Filters while
    /// one is on.
    /// </summary>
    private static SwiftView FilterPanel(
        VocabularyBrowserList state, SourceUiStyle style,
        Func<string, string> text, VocabularyListActions actions, double width)
    {
        var fields = new List<SwiftView> {
            BrowserPage.SortField(Prefix, state.SortField, state.SortDescending, style, text,
                actions.Sort)
        };
        fields.AddRange(FilterFields(state, style, text, actions.ChangeWideFilter,
            useDraft: false));
        if (state.Filters.HasActiveFilters)
            fields.Add(BrowserPage.ClearAllButton("native.vocabulary.filters.panel.clear",
                style, text, actions.ClearFilters));
        return BrowserPage.FilterPanel(Prefix, style, width, fields);
    }

    /// <summary>
    /// The total and each status count in one row (Captain 2026-11-09), scrolling sideways when
    /// it does not fit (large text, a narrow window) rather than stacking.
    /// </summary>
    private static SwiftView StatsRow(
        VocabularyBrowserList state, SourceUiStyle style, Func<string, string> text)
    {
        var words = state.FilteredWords;
        var counts = new Dictionary<LearningStatus, int>();
        foreach (var word in words)
            counts[word.Status] = counts.GetValueOrDefault(word.Status) + 1;
        int Count(LearningStatus status) => counts.GetValueOrDefault(status);
        var badges = new[] {
            Badge("total", string.Format(CultureInfo.CurrentCulture,
                text("Vocabulary_StatsTotal"), words.Count, state.Catalog.Words.Count), false),
            Badge("unknown", string.Format(CultureInfo.CurrentCulture,
                text("Vocabulary_StatsUnknown"), Count(LearningStatus.Unknown)), false),
            Badge("learning", string.Format(CultureInfo.CurrentCulture,
                text("Vocabulary_StatsLearning"), Count(LearningStatus.Learning)), false),
            Badge("familiar", string.Format(CultureInfo.CurrentCulture,
                text("Vocabulary_StatsFamiliar"), Count(LearningStatus.Familiar)), false),
            Badge("known", string.Format(CultureInfo.CurrentCulture,
                text("Vocabulary_StatsKnown"), Count(LearningStatus.Known)), true)
        };
        var row = Views.HStack(badges)
            .WithAccessibilityIdentifier("native.vocabulary.stats.row");
        var stats = Views.HorizontalGrid(new double[badges.Length], row)
            .WithAccessibilityIdentifier("native.vocabulary.stats");
        stats.Spacing = SourceUiLayout.ItemGap;
        return stats;

        SwiftView Badge(string id, string value, bool known) =>
            style.Text(Views.Text(value), SourceTextRole.BadgeSmall)
                .WithPadding(6)
                .WithRoundedSurface(
                    known ? style.Appearance.Success.Light
                        : style.Appearance.BootstrapSecondary.Light,
                    known ? style.Appearance.Success.Dark
                        : style.Appearance.BootstrapSecondary.Dark, 12)
                .WithForegroundColor(
                    known ? style.Appearance.OnPrimary.Light
                        : style.Appearance.OnBootstrapSecondary.Light,
                    known ? style.Appearance.OnPrimary.Dark
                        : style.Appearance.OnBootstrapSecondary.Dark)
                .WithAccessibilityIdentifier($"native.vocabulary.stats.{id}");
    }

    /// <summary>The source's active-filter chips (a tap removes one), in one sideways row.</summary>
    private static SwiftView Chips(VocabularyBrowserList state, VocabularyListActions actions)
    {
        var chips = state.Filters.CommittedFilters.Select(filter =>
                Views.Button($"{filter.Type}: {filter.Value}",
                        () => actions.ChangeWideFilter(filter.Type, null))
                    .WithAccessibilityIdentifier($"native.vocabulary.chip.{filter.Type}"))
            .ToArray();
        return BrowserPage.ChipStrip("native.vocabulary.chips", chips, SourceUiLayout.ItemGap);
    }

    /// <summary>Feedback, the duplicate manager and the word cards (or why there are none).</summary>
    private static void AddResults(
        List<SwiftView> children, VocabularyBrowserList state, SourceUiStyle style,
        Func<string, string> text, VocabularyListActions actions, double width, bool wide)
    {
        if (!string.IsNullOrWhiteSpace(state.Feedback))
            children.Add((state.FeedbackWarning
                    ? style.Alert(
                        style.Text(Views.Text(state.Feedback),
                            SourceTextRole.Body2), success: false)
                    : style.Text(Views.Text(state.Feedback),
                        SourceTextRole.Body2, SourceTextTone.Body))
                .WithAccessibilityIdentifier("native.vocabulary.feedback"));
        // Source: the cleanup panel sits above the word cards.
        if (state.Duplicates is { } duplicates && actions.Duplicates is { } duplicateActions)
            children.Add(VocabularyDuplicatesView.Panel(
                duplicates, style, text, duplicateActions));
        if (state.FilteredWords.Count == 0)
        {
            children.Add(style.Text(Views.Text(text(state.Catalog.Words.Count == 0
                        ? "Vocabulary_NoWordsYet" : "Vocabulary_NoMatchFilter")),
                    SourceTextRole.Body1, SourceTextTone.Secondary)
                .WithFlexibleWidth(alignment: SwiftHorizontalAlignment.Center)
                .WithPadding(48)
                .WithAccessibilityIdentifier("native.vocabulary.status"));
            // Source: an empty Vocabulary page offers Add your first word.
            if (state.Catalog.Words.Count == 0 && actions.AddWord is { } addWord)
                children.Add(style.PrimaryButton(text("Vocabulary_AddFirstWord"), addWord)
                    .WithFlexibleWidth(alignment: SwiftHorizontalAlignment.Center)
                    .WithAccessibilityIdentifier("native.vocabulary.empty.add"));
            return;
        }
        var list = Views.LazyVGrid(
                CardColumns(width, style.TextScale, state.ListMode,
                    !state.FilterPanelHidden, wide),
                state.FilteredWords.Select(word =>
                    Card(word, style, text, actions.OpenWord)).ToArray())
            .WithAccessibilityIdentifier("native.vocabulary.cards");
        list.Spacing = SourceUiLayout.PageGap;
        children.Add(list);
    }

    /// <summary>
    /// One filter the page offers: its type, label, "All" label, values (from the catalog) and how
    /// a value reads. The filter column, the phone sheet and the filter menu share this list.
    /// </summary>
    private sealed record FilterChoice(string Type, string LabelKey, string AllKey,
        IReadOnlyList<string> Values, Func<string, string>? ValueLabel = null);

    private static List<FilterChoice> FilterChoices(
        VocabularyBrowserList state, Func<string, string> text)
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
        var choices = new List<FilterChoice> {
            new("association", "Vocabulary_Association", "Vocabulary_AllWords",
                ["associated", "orphaned"],
                value => text(value == "associated"
                    ? "Vocabulary_Associated" : "Vocabulary_Orphaned")),
            new("type", "NativeResources_TypeFilter", "Vocabulary_FilterTypeAll",
                ["word", "phrase", "sentence"], value => text(value switch {
                    "word" => "Vocabulary_FilterTypeWord",
                    "phrase" => "Vocabulary_FilterTypePhrase",
                    _ => "Vocabulary_FilterTypeSentence"
                })),
            new("status", "Vocabulary_StatusLabel", "Vocabulary_AllStatus",
                ["known", "familiar", "learning", "unknown"], value => text(value switch {
                    "known" => "Vocabulary_Known",
                    "familiar" => "Vocabulary_Familiar",
                    "learning" => "Vocabulary_Learning",
                    _ => "Vocabulary_Unknown"
                }))
        };
        if (resources.Count > 0)
            choices.Add(new("resource", "Vocabulary_ResourceLabel", "Vocabulary_AllResources",
                resources.Select(resource => resource.Title)
                    .Distinct(StringComparer.OrdinalIgnoreCase).ToArray()));
        if (tags.Length > 0)
            choices.Add(new("tag", "Vocabulary_TagLabel", "Vocabulary_AllTags", tags));
        if (lemmas.Length > 0)
            choices.Add(new("lemma", "Vocabulary_LemmaLabel", "Vocabulary_AllLemmas", lemmas));
        if (languages.Length > 0)
            choices.Add(new("language", "Vocabulary_LanguageLabel", "Vocabulary_AllLanguages",
                languages));
        choices.Add(new("encoding", "Vocabulary_EncodingStrength", "Vocabulary_AllEncoding",
            ["strong", "good", "basic"], value => text(value switch {
                "strong" => "Vocabulary_Strong",
                "good" => "Vocabulary_Good",
                _ => "Vocabulary_Basic"
            })));
        return choices;
    }

    private static List<SwiftView> FilterFields(
        VocabularyBrowserList state, SourceUiStyle style,
        Func<string, string> text, Action<string, string?> changed, bool useDraft) =>
        FilterChoices(state, text).Select(choice => Filter(choice.Type, choice.LabelKey,
                choice.AllKey, choice.Values, state, style, text, changed, useDraft,
                choice.ValueLabel))
            .ToList();

    private static SwiftView Filter(
        string type, string labelKey, string allKey,
        IReadOnlyList<string> values,
        VocabularyBrowserList state, SourceUiStyle style,
        Func<string, string> text, Action<string, string?> changed,
        bool useDraft, Func<string, string>? valueLabel = null) =>
        BrowserPage.FilterMenu(Prefix, type, text(labelKey), text(allKey), values,
            state.Filters.FilterValue(type, useDraft), style,
            value => changed(type, value), valueLabel);

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
