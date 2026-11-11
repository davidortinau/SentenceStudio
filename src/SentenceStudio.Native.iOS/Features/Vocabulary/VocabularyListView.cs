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
    Action ToggleFilterPanel,
    Action ClearFilters,
    Action<string> OpenWord,
    Action Reload,
    Action<string> Sort,
    Action ToggleListMode,
    Action<string, string?> ChangeWideFilter,
    Action? OpenLookups = null,
    VocabularyDuplicateActions? Duplicates = null,
    Action<double>? PageWidthChanged = null);

internal sealed record VocabularyFilterActions(
    Action<string, string?> Change,
    Action<string> Sort,
    Action Apply, Action Cancel, Action Clear);

/// <summary>
/// The Vocabulary list. Phone: one scrolling column, filters and sort in a sheet. Wide (Captain
/// 2026-11-09): a fixed filter column (sort and every filter) beside the results: the search
/// field, then their own scroll with one row of counts above the lazily built grid (only the
/// search stays put, so a short window with the keyboard up still shows results). The toolbar
/// holds Recent lookups, the grid/list switch (wide only) and Filters (the sheet on a phone, the
/// filter column's show/hide when wide).
/// </summary>
/// <remarks>
/// The scene updates a mounted page in place (the search field keeps focus while typing), so a
/// toolbar button's action must not depend on the state it was built with, and its symbol, label
/// and selected state are the only parts the scene changes on it.
/// </remarks>
internal static class VocabularyListView
{
    /// <summary>From the source md breakpoint the filter column sits beside the results.</summary>
    internal static bool IsWide(double viewportWidth) =>
        viewportWidth >= SourceUiLayout.DesktopWidth;

    /// <summary>
    /// The wide filter column: about a third of the window (Captain 2026-11-09), at least 280
    /// points and at most 400 so the results keep the rest. Larger text widens it in proportion
    /// (up to half the window) so its sort buttons and menus keep their words.
    /// </summary>
    internal static double FilterColumnWidth(double viewportWidth, double textScale = 1)
    {
        var scale = double.IsFinite(textScale) && textScale > 1 ? textScale : 1;
        return Math.Clamp(Math.Round(viewportWidth / 3 * scale), 280,
            Math.Max(280, Math.Min(viewportWidth / 2, 400 * scale)));
    }

    /// <summary>
    /// Word card columns for a page <paramref name="width"/> points wide. Phone layout: the
    /// source's one column. Wide: one in list mode; in grid mode as many as the results column
    /// holds at this text size (one under 440 points, two under 760, then three), larger text
    /// counting as a narrower column.
    /// </summary>
    internal static int CardColumns(double width, double textScale,
        bool listMode, bool filterPanelShown, bool wide)
    {
        if (!wide)
            return SourceUiLayout.ResourceColumns(width);
        if (listMode)
            return 1;
        var results = width - 2 * SourceUiLayout.CardInset
            - (filterPanelShown ? FilterColumnWidth(width, textScale) + 1 : 0);
        var effective = NativeAdaptiveLayout.EffectiveWidth(results, textScale);
        return effective < 440 ? 1 : effective < 760 ? 2 : 3;
    }

    /// <param name="pageWidth">
    /// The page's own width when it has reported one (<see cref="VocabularyListActions.PageWidthChanged"/>):
    /// an iPad sidebar takes part of the window, so the phone or wide layout, the filter column
    /// and the card columns follow it. Before it reports, the window's width stands in.
    /// </param>
    internal static SwiftView Page(
        VocabularyBrowserList state, SourceUiStyle style,
        Func<string, string> text, VocabularyListActions actions, double? pageWidth = null)
    {
        var width = pageWidth is > 0 and var measured && double.IsFinite(measured)
            ? measured : style.ViewportWidth;
        var wide = IsWide(width);
        var ready = !state.Loading && !state.Blocked && state.ErrorKey is null;
        var page = wide && ready
            ? WideLayout(state, style, text, actions, width)
            : SingleColumn(state, style, text, actions, width);
        var content = style.Page(page);
        if (actions.PageWidthChanged is { } changed)
            content.OnViewportChanged((measuredWidth, _) => changed(measuredWidth));
        return Views.Toolbar(content, ToolbarActions(state, text, actions, wide, ready))
            .WithAccessibilityIdentifier("native.vocabulary.page");
    }

    internal static SwiftView PhoneFilterSheet(
        VocabularyBrowserList state, SourceUiStyle style,
        Func<string, string> text, VocabularyFilterActions actions)
    {
        var fields = FilterFields(state, style, text, actions.Change,
            useDraft: true);
        // Captain 2026-11-09: sorting is one of the filter options (applied with them).
        fields.Insert(0, SortField(state.SheetSortField, state.SheetSortDescending,
            style, text, actions.Sort));
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
        if (wide)
        {
            // Captain 2026-11-09: the grid/list switch is a toolbar button showing the layout a
            // tap switches to. A phone always shows one column, so it has no switch.
            var toggle = Views.Button(Views.Image(state.ListMode
                            ? "square.grid.3x3" : "list.bullet"),
                        actions.ToggleListMode)
                .WithAccessibilityLabel(text(state.ListMode
                    ? "Vocabulary_GridView" : "Vocabulary_ListView"))
                .WithAccessibilityIdentifier("native.vocabulary.view.toggle");
            // No words or column to arrange while the list loads or cannot show.
            toggle.IsEnabled = ready;
            shown.Add(toggle);
        }
        // Filters: the sheet on a phone; the filter column's show/hide when wide (selected while
        // the column shows). Filled while a filter or search is on.
        var filters = Views.Button(Views.Image(
                    state.Filters.HasActiveFilters
                        ? "line.3.horizontal.decrease.circle.fill"
                        : "line.3.horizontal.decrease.circle"),
                wide ? actions.ToggleFilterPanel : actions.OpenFilters)
            .WithAccessibilityLabel(text("Vocabulary_Filters"))
            .WithAccessibilitySelected(wide && ready && !state.FilterPanelHidden)
            .WithAccessibilityIdentifier("native.vocabulary.filters.open");
        filters.IsEnabled = !wide || ready;
        shown.Add(filters);
        return shown.ToArray();
    }

    private static SwiftView SearchRow(
        VocabularyBrowserList state, SourceUiStyle style,
        Func<string, string> text, VocabularyListActions actions)
    {
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
        var row = Views.HStack(searchRow.ToArray())
            .WithFlexibleWidth()
            .WithAccessibilityIdentifier("native.vocabulary.search.row");
        row.Spacing = SourceUiLayout.ItemGap;
        return row;
    }

    /// <summary>The phone page, and the wide page while it loads or cannot show words.</summary>
    private static SwiftView SingleColumn(
        VocabularyBrowserList state, SourceUiStyle style,
        Func<string, string> text, VocabularyListActions actions, double width)
    {
        var children = new List<SwiftView> { SearchRow(state, style, text, actions) };
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
        var content = Views.VStack(children.ToArray())
            .WithPadding(SourceUiLayout.PageGutter(style.ViewportWidth))
            .WithFlexibleWidth()
            .WithAccessibilityIdentifier("native.vocabulary.scroll.content");
        content.Spacing = SourceUiLayout.PageGap;
        return Views.Scroll(content)
            .WithAccessibilityIdentifier("native.vocabulary.scroll");
    }

    private static SwiftView WideLayout(
        VocabularyBrowserList state, SourceUiStyle style,
        Func<string, string> text, VocabularyListActions actions, double width)
    {
        var gutter = SourceUiLayout.PageGutter(style.ViewportWidth);
        var columns = new List<SwiftView>();
        if (!state.FilterPanelHidden)
        {
            columns.Add(FilterPanel(state, style, text, actions, width));
            columns.Add(Views.Divider()
                .WithAccessibilityIdentifier("native.vocabulary.filters.panel.divider"));
        }
        var searchRow = SearchRow(state, style, text, actions)
            .WithPadding(gutter, gutter, 0, gutter);
        var headerRows = new List<SwiftView> { StatsRow(state, style, text) };
        if (state.Filters.CommittedFilters.Count > 0)
            headerRows.Add(Chips(state, actions));
        var header = Views.VStack(headerRows.ToArray())
            .WithFlexibleWidth()
            .WithAccessibilityIdentifier("native.vocabulary.results.header");
        header.Spacing = SourceUiLayout.ItemGap;
        var results = new List<SwiftView> { header };
        AddResults(results, state, style, text, actions, width, wide: true);
        var content = Views.VStack(results.ToArray())
            .WithPadding(0, gutter, gutter, gutter)
            .WithFlexibleWidth()
            .WithAccessibilityIdentifier("native.vocabulary.results.content");
        content.Spacing = SourceUiLayout.PageGap;
        // The results scroll on their own (lazily built cards); the search stays put.
        var scroll = Views.Scroll(content)
            .WithAccessibilityIdentifier("native.vocabulary.results.scroll");
        var column = Views.VStack(searchRow, scroll)
            .WithFlexibleWidth()
            .WithAccessibilityIdentifier("native.vocabulary.results");
        column.Spacing = SourceUiLayout.PageGap;
        columns.Add(column);
        var layout = Views.HStack(columns.ToArray())
            .WithFlexibleWidth()
            .WithAccessibilityIdentifier("native.vocabulary.wide.layout");
        layout.Spacing = 0;
        return layout;
    }

    /// <summary>
    /// The wide filter column: sort, every filter (applied at once) and Clear All Filters while
    /// one is on. It scrolls on its own and keeps a fixed width beside the results.
    /// </summary>
    private static SwiftView FilterPanel(
        VocabularyBrowserList state, SourceUiStyle style,
        Func<string, string> text, VocabularyListActions actions, double width)
    {
        var fields = new List<SwiftView> {
            SortField(state.SortField, state.SortDescending, style, text, actions.Sort)
        };
        fields.AddRange(FilterFields(state, style, text, actions.ChangeWideFilter,
            useDraft: false));
        if (state.Filters.HasActiveFilters)
            fields.Add(style.SecondaryButton(
                    text("Vocabulary_ClearAllFilters"), actions.ClearFilters)
                .WithAccessibilityIdentifier("native.vocabulary.filters.panel.clear"));
        var content = Views.VStack(fields.ToArray())
            .WithPadding(SourceUiLayout.PageGutter(style.ViewportWidth))
            .WithFlexibleWidth()
            .WithAccessibilityIdentifier("native.vocabulary.filters.panel.content");
        content.Spacing = SourceUiLayout.FormGap;
        return Views.Scroll(content)
            .WithFixedWidth(FilterColumnWidth(width, style.TextScale))
            .WithAccessibilityIdentifier("native.vocabulary.filters.panel");
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
        var row = Views.HStack(chips)
            .WithAccessibilityIdentifier("native.vocabulary.chips.row");
        var grid = Views.HorizontalGrid(new double[chips.Length], row)
            .WithAccessibilityIdentifier("native.vocabulary.chips");
        grid.Spacing = SourceUiLayout.ItemGap;
        return grid;
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

    private static SwiftView SortField(
        string field, bool descending, SourceUiStyle style,
        Func<string, string> text, Action<string> changed)
    {
        var sort = Views.ButtonGroup([
                SortButton("added", "Vocabulary_SortAdded", field, descending, style,
                    text, changed, SourceButtonGroupPosition.First),
                SortButton("updated", "Vocabulary_SortUpdated", field, descending, style,
                    text, changed, SourceButtonGroupPosition.Last)
            ], selectedIndex: field == "updated" ? 1 : 0)
            .WithAccessibilityIdentifier("native.vocabulary.sort");
        return Views.VStack(
                style.Text(Views.Text(text("Vocabulary_SortLabel")),
                    SourceTextRole.Body2, SourceTextTone.Secondary),
                sort)
            .WithFlexibleWidth()
            .WithAccessibilityIdentifier("native.vocabulary.filter.field.sort");
    }

    private static SwiftView SortButton(
        string sort, string labelKey, string field, bool descending,
        SourceUiStyle style, Func<string, string> text, Action<string> changed,
        SourceButtonGroupPosition position) =>
        field == sort
            ? style.PrimaryButton(
                    $"{text(labelKey)} {(descending ? "↓" : "↑")}",
                    () => changed(sort), groupPosition: position)
                .WithAccessibilityIdentifier($"native.vocabulary.sort.{sort}")
            : style.OutlineButton(Views.Text(text(labelKey)),
                    () => changed(sort), groupPosition: position)
                .WithAccessibilityIdentifier($"native.vocabulary.sort.{sort}");

    private static List<SwiftView> FilterFields(
        VocabularyBrowserList state, SourceUiStyle style,
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
        VocabularyBrowserList state, SourceUiStyle style,
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
