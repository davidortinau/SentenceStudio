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
    Action? OpenDuplicates = null,
    Action<double>? PageWidthChanged = null,
    Action? AddWord = null,
    Action<string>? SearchField = null,
    Action<IReadOnlyList<string>>? KeepChips = null,
    Action<string>? RequestDelete = null,
    Action? ConfirmDelete = null,
    Action? CancelDelete = null,
    Action<string>? Study = null,
    Action? ShowMoreRows = null,
    Action<string>? ChooseFilterValue = null);

internal sealed record VocabularyFilterActions(
    Action<string, string?> Change,
    Action<string> Sort,
    Action Apply, Action Cancel, Action Clear);

/// <summary>
/// The Vocabulary list on the shared browse layout (<see cref="BrowserPage"/>, Captain
/// 2026-11-09): one scrolling column on a phone with filters and sort in a sheet; wide, a fixed
/// filter column beside the results, whose own scroll holds one row of counts above the lazily
/// built grid. The toolbar holds Recent lookups, Clean Up Duplicates, the grid/list switch
/// (wide only), Filters and Add (+).
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
        Func<string, string> text, VocabularyListActions actions, double? pageWidth = null,
        string? deleteMessage = null)
    {
        var width = BrowserPage.Width(pageWidth, style);
        var wide = IsWide(width);
        var ready = !state.Loading && !state.Blocked && state.ErrorKey is null;
        var page = wide && ready
            ? WideLayout(state, style, text, actions, width)
            : SingleColumn(state, style, text, actions, width);
        page = DeleteConfirmation(page, deleteMessage, text, actions);
        return BrowserPage.WithToolbar(Prefix, page, style, actions.PageWidthChanged,
            ToolbarActions(state, text, actions, wide, ready));
    }

    /// <summary>
    /// A row's swipe Delete asks first: a native confirmation dialog with the source's message
    /// naming the word. The dialog is always there, shown while a delete waits, so the page
    /// updates in place.
    /// </summary>
    private static SwiftView DeleteConfirmation(SwiftView page, string? message,
        Func<string, string> text, VocabularyListActions actions)
    {
        var dialog = Views.ConfirmationDialog(page, message ?? string.Empty,
                () => actions.CancelDelete?.Invoke(),
                Views.Button(text("Common_Delete"), () => actions.ConfirmDelete?.Invoke())
                    .WithButtonRole(SwiftButtonRole.Destructive)
                    .WithAccessibilityIdentifier("native.vocabulary.delete.confirm"),
                Views.Button(text("Common_Cancel"), () => actions.CancelDelete?.Invoke())
                    .WithButtonRole(SwiftButtonRole.Cancel)
                    .WithAccessibilityIdentifier("native.vocabulary.delete.cancel"))
            .WithAccessibilityIdentifier("native.vocabulary.delete.dialog");
        dialog.IsPresented = message is not null;
        return dialog;
    }

    /// <summary>
    /// The words show as a native List (Captain 2026-11-12: Library lists are native lists with
    /// swipe actions): always in one column (a phone, the Library split view's list column),
    /// and in list mode on a wide page (its grid stays a grid). One column
    /// is a List in every state (loading, an error, no words, no matches: a row each), as Skills
    /// is, so the page keeps its outline under the system search field, updates in place and
    /// the field keeps its focus and Korean composition: one jamo matches no words, and the
    /// page mounted anew under it closed the keyboard on every keystroke (Duotopia 2026-11-13).
    /// The wide layout shows only a loaded list.
    /// </summary>
    internal static bool AsNativeList(VocabularyBrowserList state, bool wide) =>
        !wide || state.ListMode;

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
        // Clean Up Duplicates (Captain 2026-11-14): its own page, beside the list.
        if (actions.OpenDuplicates is { } openDuplicates)
            shown.Add(Views.Button(Views.Image("square.on.square"), openDuplicates)
                .WithAccessibilityLabel(text("Vocabulary_DuplicatesCleanUp"))
                .WithAccessibilityIdentifier($"{Prefix}.duplicates.open"));
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
            items.Add(UsesChooser(filter, actions)
                ? ChooserMenuItem(filter, state, text, actions.ChooseFilterValue!)
                : BrowserPage.FilterSubmenu(Prefix, filter.Type, text(filter.LabelKey),
                    text(filter.AllKey), filter.Values, state.Filters.FilterValue(filter.Type, false),
                    value => actions.ChangeWideFilter(filter.Type, value), filter.ValueLabel));
        items.AddRange(BrowserPage.ClearAllMenuItems(Prefix, text, actions.ClearFilters,
            state.Filters.HasActiveFilters));
        return items;
    }

    /// <summary>
    /// The most values a filter lists inline (in the menu or as a picker). A learner's lemmas,
    /// tags and resources can run to thousands, and a menu builds every item each time the
    /// toolbar lays out (2026-11-12: 2,908 lemmas on Duotopia held the app past the system's
    /// background redraw time). A longer filter opens <see cref="Chooser"/> instead.
    /// </summary>
    internal const int InlineValueLimit = 30;

    /// <summary>The most values the chooser lists at once; its search finds the rest.</summary>
    internal const int ChooserRowLimit = 100;

    private static bool UsesChooser(FilterChoice filter, VocabularyListActions actions) =>
        filter.Values.Count > InlineValueLimit && actions.ChooseFilterValue is not null;

    /// <summary>The filter's current choice as the menu names it (its All label when off).</summary>
    private static string ChoiceTitle(FilterChoice filter, VocabularyBrowserList state,
        Func<string, string> text)
    {
        var value = state.Filters.FilterValue(filter.Type, false);
        return string.IsNullOrWhiteSpace(value) ? text(filter.AllKey)
            : filter.ValueLabel?.Invoke(value) ?? value;
    }

    private static SwiftView ChooserMenuItem(FilterChoice filter, VocabularyBrowserList state,
        Func<string, string> text, Action<string> choose) =>
        Views.Button($"{text(filter.LabelKey)}: {ChoiceTitle(filter, state, text)}",
                () => choose(filter.Type))
            .WithAccessibilityIdentifier($"{Prefix}.filter.{filter.Type}.choose");

    /// <summary>The filter <paramref name="type"/> as the page offers it now (its values from the catalog); null when it is not offered.</summary>
    internal static FilterChoice? Choice(VocabularyBrowserList state, Func<string, string> text,
        string type) => FilterChoices(state, text).FirstOrDefault(item => item.Type == type);

    /// <summary>
    /// A long filter's chooser (a sheet), with the system containers (Captain 2026-11-12, native
    /// first, as the Choose My Own selector): a navigation stack titled with the filter, Cancel in
    /// its toolbar, the system search field, and a native list of All and the matching values
    /// (the first <see cref="ChooserRowLimit"/>), the current one checked. <paramref name="rows"/>
    /// is the list the coordinator refills as the search changes, so the field keeps focus.
    /// </summary>
    internal static SwiftView Chooser(FilterChoice filter, string current, string query,
        SourceUiStyle style, Func<string, string> text, Action<string> search,
        Action<string?> choose, Action cancel, out SwiftView rows)
    {
        rows = BrowserPage.NativeList($"{Prefix}.chooser.list",
            ChooserRows(filter, current, query, style, text, choose), style);
        var searchable = Views.Searchable(rows, text("Common_Search"), query, search)
            .WithAccessibilityIdentifier($"{Prefix}.chooser.search");
        var page = Views.Toolbar(searchable,
                Views.Button(text("Common_Cancel"), cancel)
                    .WithAccessibilityIdentifier($"{Prefix}.chooser.cancel"))
            .WithNavigationTitle(text(filter.LabelKey))
            .WithInlineNavigationTitle()
            .WithAccessibilityIdentifier($"{Prefix}.chooser");
        return Views.NavigationStack(page);
    }

    /// <summary>
    /// All, the current choice when the first rows would not show it, then the values matching
    /// <paramref name="query"/> (the first <see cref="ChooserRowLimit"/>), and how many more match.
    /// Each row's automation ID names its value, so it stays the same as the search changes.
    /// </summary>
    internal static List<SwiftView> ChooserRows(FilterChoice filter, string current,
        string query, SourceUiStyle style, Func<string, string> text, Action<string?> choose)
    {
        var rows = new List<SwiftView>
        {
            Row(text(filter.AllKey), null, string.IsNullOrWhiteSpace(current), "all")
        };
        var needle = query.Trim();
        var matches = filter.Values.Where(value => needle.Length == 0
                || value.Contains(needle, StringComparison.CurrentCultureIgnoreCase)
                || filter.ValueLabel?.Invoke(value).Contains(needle,
                    StringComparison.CurrentCultureIgnoreCase) == true)
            .ToList();
        var shown = matches.Take(ChooserRowLimit).ToList();
        // The current choice stays visible (and checked) even past the first rows.
        if (!string.IsNullOrWhiteSpace(current) && needle.Length == 0
            && !shown.Any(value => value.Equals(current, StringComparison.OrdinalIgnoreCase)))
            rows.Add(Row(filter.ValueLabel?.Invoke(current) ?? current, current, true, $"value.{current}"));
        foreach (var value in shown)
            rows.Add(Row(filter.ValueLabel?.Invoke(value) ?? value, value,
                value.Equals(current, StringComparison.OrdinalIgnoreCase), $"value.{value}"));
        if (matches.Count > ChooserRowLimit)
            rows.Add(style.Text(Views.Text(string.Format(CultureInfo.CurrentCulture,
                        text("Vocabulary_ChooserMore"), matches.Count - ChooserRowLimit)),
                    SourceTextRole.Small, SourceTextTone.Secondary)
                .WithAccessibilityIdentifier($"{Prefix}.chooser.more"));
        return rows;

        SwiftView Row(string title, string? value, bool selected, string id)
        {
            var label = selected
                ? Views.HStack(Views.Text(title).WithFlexibleWidth(), Views.Image("checkmark"))
                : Views.HStack(Views.Text(title).WithFlexibleWidth());
            return Views.Button(label.WithFlexibleWidth(), () => choose(value))
                .WithButtonStyle(SwiftButtonStyle.Plain)
                .WithFlexibleWidth()
                .WithMinimumHeight(SourceUiLayout.MinimumTouchHeight)
                .WithAccessibilityLabel(title)
                .WithAccessibilitySelected(selected)
                .WithAccessibilityIdentifier($"{Prefix}.chooser.option.{id}");
        }
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
            remaining => actions.KeepChips?.Invoke(remaining)),
            AsNativeList(state, wide: false) ? style : null);
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
            BrowserPage.ResultsHeader(Prefix, headerRows), results,
            asList: AsNativeList(state, wide: true));
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
            useDraft: false, actions));
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
        // The first rows only (RowLimit), then Load More for the next page: every built row is a
        // view the system redraws, and thousands of them held the app past its background
        // redraw time.
        if (AsNativeList(state, wide))
        {
            children.AddRange(state.ShownWords.Select(word =>
                ListRow(word, style, text, actions)));
            AddLoadMore(children, state, style, text, actions);
            return;
        }
        var list = Views.LazyVGrid(
                CardColumns(width, style.TextScale, state.ListMode,
                    !state.FilterPanelHidden, wide),
                state.ShownWords.Select(word =>
                    Card(word, style, text, actions.OpenWord)).ToArray())
            .WithAccessibilityIdentifier("native.vocabulary.cards");
        list.Spacing = SourceUiLayout.PageGap;
        children.Add(list);
        AddLoadMore(children, state, style, text, actions);
    }

    /// <summary>Load More after the shown rows while more words match (the next <see cref="VocabularyBrowser.RowPage"/>).</summary>
    private static void AddLoadMore(List<SwiftView> children, VocabularyBrowserList state,
        SourceUiStyle style, Func<string, string> text, VocabularyListActions actions)
    {
        if (state.HiddenWordCount == 0 || actions.ShowMoreRows is not { } more)
            return;
        children.Add(Views.Button(text("Common_LoadMore"), more)
            .WithFlexibleWidth(alignment: SwiftHorizontalAlignment.Center)
            .WithMinimumHeight(SourceUiLayout.MinimumTouchHeight)
            .WithTintColor(style.Appearance.Primary.Light, style.Appearance.Primary.Dark)
            .WithAccessibilityIdentifier("native.vocabulary.loadMore"));
    }

    /// <summary>
    /// One filter the page offers: its type, label, "All" label, values (from the catalog) and how
    /// a value reads. The filter column, the phone sheet and the filter menu share this list.
    /// </summary>
    internal sealed record FilterChoice(string Type, string LabelKey, string AllKey,
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
        Func<string, string> text, Action<string, string?> changed, bool useDraft,
        VocabularyListActions? actions = null) =>
        FilterChoices(state, text).Select(choice =>
                !useDraft && actions is not null && UsesChooser(choice, actions)
                    ? ChooserField(choice, state, style, text, actions.ChooseFilterValue!)
                    : Filter(choice.Type, choice.LabelKey,
                        choice.AllKey, choice.Values, state, style, text, changed, useDraft,
                        choice.ValueLabel))
            .ToList();

    /// <summary>A long filter in the wide filter column: its label and a button naming the choice, which opens the chooser.</summary>
    private static SwiftView ChooserField(FilterChoice filter, VocabularyBrowserList state,
        SourceUiStyle style, Func<string, string> text, Action<string> choose)
    {
        var label = text(filter.LabelKey);
        var current = ChoiceTitle(filter, state, text);
        var button = style.Input(Views.Button(
                    Views.HStack(Views.Text(current).WithFlexibleWidth(),
                            Views.Image("chevron.up.chevron.down"))
                        .WithFlexibleWidth(),
                    () => choose(filter.Type))
                .WithButtonStyle(SwiftButtonStyle.Plain)
                .WithFlexibleWidth())
            .WithAccessibilityLabel($"{label}, {current}")
            .WithAccessibilityIdentifier($"{Prefix}.filter.{filter.Type}.choose");
        return Views.VStack(
                style.Text(Views.Text(label), SourceTextRole.Body2, SourceTextTone.Secondary),
                button)
            .WithFlexibleWidth()
            .WithAccessibilityIdentifier($"{Prefix}.filter.field.{filter.Type}");
    }

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
        var details = style.Text(Views.Text(Details(word, text)),
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

    /// <summary>
    /// A native List row (Captain 2026-11-12: native rows, icons kept): the word's type icon,
    /// its terms and details, and its status in words. Swipe left to delete it (asks first),
    /// right to study the resources it belongs to.
    /// </summary>
    private static SwiftView ListRow(NativeVocabularyEntry word, SourceUiStyle style,
        Func<string, string> text, VocabularyListActions actions)
    {
        var icon = Views.Image(word.LexicalUnitType switch
            {
                LexicalUnitType.Phrase => "text.quote",
                LexicalUnitType.Sentence => "text.alignleft",
                _ => "textformat"
            })
            .WithForegroundColor(style.Appearance.Primary.Light, style.Appearance.Primary.Dark);
        var target = style.Text(Views.Text(word.TargetTerm), SourceTextRole.ListTitle)
            .WithLineLimit(1)
            .WithFlexibleWidth()
            .WithAccessibilityIdentifier($"native.vocabulary.card.{word.Id}.target");
        var native = style.Text(Views.Text(word.NativeTerm),
                SourceTextRole.Body2, SourceTextTone.Secondary)
            .WithLineLimit(1)
            .WithFlexibleWidth()
            .WithAccessibilityIdentifier($"native.vocabulary.card.{word.Id}.native");
        var details = style.Text(Views.Text(Details(word, text)),
                SourceTextRole.Small, SourceTextTone.Secondary)
            .WithLineLimit(1)
            .WithFlexibleWidth()
            .WithAccessibilityIdentifier($"native.vocabulary.card.{word.Id}.details");
        var terms = Views.VStack(target, native, details).WithFlexibleWidth();
        terms.Spacing = 2;
        var status = style.Text(Views.Text(text(StatusKey(word))), SourceTextRole.Small,
                word.IsKnown ? SourceTextTone.Success : SourceTextTone.Secondary)
            .WithAccessibilityIdentifier($"native.vocabulary.card.{word.Id}.status");
        var label = Views.HStack(icon, terms, status).WithFlexibleWidth();
        label.Spacing = SourceUiLayout.ItemGap;
        var row = Views.Button(label, () => actions.OpenWord(word.Id))
            .WithButtonStyle(SwiftButtonStyle.Plain)
            .WithFlexibleWidth()
            .WithMinimumHeight(SourceUiLayout.MinimumTouchHeight)
            .WithAccessibilityIdentifier($"native.vocabulary.card.{word.Id}")
            .WithAccessibilityLabel(
                $"{word.TargetTerm}, {word.NativeTerm}, {text(StatusKey(word))}");
        var trailing = actions.RequestDelete is not { } delete ? []
            : new[] {
                // Red, not destructive: a destructive swipe action removes its row at once, and
                // this one asks first.
                Views.Button(Views.Label(text("Common_Delete"), "trash"), () => delete(word.Id))
                    .WithTintColor(style.Appearance.Danger.Light, style.Appearance.Danger.Dark)
                    .WithAccessibilityIdentifier($"native.vocabulary.card.{word.Id}.delete")
            };
        // A word in no resource has nothing Choose My Own can study.
        var leading = actions.Study is not { } study || word.AssociatedResources.Count == 0 ? []
            : new[] {
                Views.Button(Views.Label(text("Library_StudyThis"), "graduationcap"),
                        () => study(word.Id))
                    .WithTintColor(style.Appearance.Primary.Light, style.Appearance.Primary.Dark)
                    .WithAccessibilityIdentifier($"native.vocabulary.card.{word.Id}.study")
            };
        return Views.SwipeRow(row, trailing, leading)
            .WithAccessibilityIdentifier($"native.vocabulary.card.{word.Id}.swipe");
    }

    /// <summary>The card's details line: encoding, resources (or orphaned), lemma and up to three tags.</summary>
    private static string Details(NativeVocabularyEntry word, Func<string, string> text)
    {
        var metadata = new List<string> {
            word.AssociatedResources.Count == 0 ? text("Vocabulary_OrphanedBadge")
                : string.Format(CultureInfo.CurrentCulture, text("Vocabulary_ResourcesCount"),
                    word.AssociatedResources.Count)
        };
        if (!string.IsNullOrWhiteSpace(word.Lemma))
            metadata.Add(word.Lemma);
        if (!string.IsNullOrWhiteSpace(word.Tags))
            metadata.AddRange(word.Tags.Split(',',
                StringSplitOptions.TrimEntries |
                StringSplitOptions.RemoveEmptyEntries).Take(3));
        return $"{text(word.EncodingLabel switch {
            "Strong" => "Vocabulary_Strong",
            "Good" => "Vocabulary_Good",
            _ => "Vocabulary_Basic"
        })} · {string.Join(" · ", metadata)}";
    }

    private static string StatusKey(NativeVocabularyEntry word) =>
        word.IsKnown ? "Vocabulary_Known" : word.Status switch {
            LearningStatus.Familiar => "Vocabulary_Familiar",
            LearningStatus.Learning => "Vocabulary_Learning",
            LearningStatus.Known => "Vocabulary_Known",
            _ => "Vocabulary_Unknown"
        };
}
