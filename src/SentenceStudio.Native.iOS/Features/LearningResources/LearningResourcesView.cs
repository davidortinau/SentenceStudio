using System.Collections.Immutable;
using System.Globalization;
using Microsoft.SwiftUI;
using SentenceStudio.Native.iOS.Features.Browse;
using SentenceStudio.Native.iOS.Styling;
using Views = Microsoft.SwiftUI.SwiftUI;

namespace SentenceStudio.Native.iOS.Features.LearningResources;

/// <summary>
/// The Resources list on the shared browse layout (<see cref="BrowserPage"/>; Captain 2026-11-10:
/// the same layout and filter and sort options as Vocabulary). Phone: one scrolling column with
/// type, language and sort in a sheet. Wide: a fixed filter column beside the results, whose own
/// scroll starts with the count. The toolbar holds the grid/list switch, Filters and Add (+).
/// </summary>
/// <remarks>
/// The scene updates a mounted page in place (the search field keeps focus while typing), so a
/// toolbar button's action must not depend on the state it was built with.
/// </remarks>
internal static class LearningResourcesView
{
    private const string Prefix = "native.resources";

    /// <param name="pageWidth">
    /// The page's own width once it has reported one (an iPad sidebar takes part of the window);
    /// before that, the window's.
    /// </param>
    internal static SwiftView Page(
        NativeResourceListState state,
        string? feedback,
        SourceUiStyle style,
        Func<string, string> text,
        LearningResourcesActions actions,
        double? pageWidth = null)
    {
        var width = BrowserPage.Width(pageWidth, style);
        var wide = BrowserPage.IsWide(width);
        var ready = IsReady(state);
        var page = wide && ready
            ? WideLayout(state, feedback, style, text, actions, width)
            : SingleColumn(state, feedback, style, text, actions, width);
        // Making a starter resource holds the page still until it is done.
        page.IsEnabled = !state.StarterBusy;
        return BrowserPage.WithToolbar(Prefix, page, style, actions.PageWidthChanged,
            ToolbarActions(state, text, actions, wide, ready));
    }

    /// <summary>The list has loaded and can show its resources (or that there are none).</summary>
    internal static bool IsReady(NativeResourceListState state) =>
        state.Phase is NativeResourceListPhase.Populated or NativeResourceListPhase.Empty
            or NativeResourceListPhase.NoMatches;

    /// <summary>
    /// Card columns in grid mode: the source's columns for the window on a phone; wide, as many
    /// as the results column holds beside the filter column. A resource card carries its date
    /// beside the title, so it needs more room than a word card: one column under 600 points
    /// (larger text counting as narrower), two under 1000, then three.
    /// </summary>
    internal static int GridColumns(double width, double textScale, bool filterPanelShown)
    {
        if (!BrowserPage.IsWide(width))
            return SourceUiLayout.ResourceColumns(width);
        var results = width - 2 * SourceUiLayout.CardInset
            - (filterPanelShown ? BrowserPage.FilterColumnWidth(width, textScale) + 1 : 0);
        var effective = NativeAdaptiveLayout.EffectiveWidth(results, textScale);
        return effective < 600 ? 1 : effective < 1000 ? 2 : 3;
    }

    /// <summary>The phone filter sheet: sort, type and language, applied together.</summary>
    internal static SwiftView FilterSheet(
        NativeResourceListState state, SourceUiStyle style,
        Func<string, string> text, LearningResourceFilterActions actions)
    {
        var sheet = state.Sheet ?? new NativeResourceFilterDraft(state.MediaType, state.Language,
            state.SortField, state.SortDescending);
        var fields = new List<SwiftView> {
            BrowserPage.SortField(Prefix, sheet.SortField, sheet.SortDescending, style, text,
                actions.Sort)
        };
        fields.AddRange(FilterFields(state, sheet.MediaType, sheet.Language, style, text,
            actions.ChangeType, actions.ChangeLanguage));
        return BrowserPage.FilterSheet(Prefix, style, text, fields,
            actions.Clear, actions.Cancel, actions.Apply);
    }

    private static SwiftView[] ToolbarActions(
        NativeResourceListState state, Func<string, string> text,
        LearningResourcesActions actions, bool wide, bool ready)
    {
        var enabled = ready && !state.StarterBusy;
        // Wide: the filter column's show/hide. Otherwise (a phone, the Library split view's
        // list column) a native menu: sort, type and language, applied at once.
        var filters = wide
            ? BrowserPage.FiltersButton(Prefix, text("Vocabulary_Filters"),
                state.HasActiveFilters, wide, enabled, !state.FilterPanelHidden,
                actions.OpenFilters, actions.ToggleFilterPanel)
            : BrowserPage.FilterMenu(Prefix, text("Vocabulary_Filters"), state.HasActiveFilters,
                enabled: true, FilterMenuItems(state, text, actions));
        // The filter menu opens while the list loads, as on Vocabulary, but not while a
        // starter resource is made or the owner is unknown.
        if (state.StarterBusy || state.Phase == NativeResourceListPhase.Blocked)
            filters.IsEnabled = false;
        return [
            // The phone list differs from its one-column grid, so the switch shows at every width.
            BrowserPage.ViewToggle(Prefix, state.ViewMode == NativeResourceViewMode.List,
                actions.ToggleViewMode, text("Resources_GridView"), text("Resources_ListView"),
                enabled),
            filters,
            BrowserPage.AddButton(Prefix, text("Resources_AddResource"), actions.Add,
                state.Phase != NativeResourceListPhase.Blocked && !state.StarterBusy)
        ];
    }

    /// <summary>The filter menu's items: Sort, Type, Language, and Clear All Filters while one is on.</summary>
    private static IEnumerable<SwiftView> FilterMenuItems(
        NativeResourceListState state, Func<string, string> text, LearningResourcesActions actions)
    {
        var types = state.Types.IsDefault ? [] : state.Types;
        var languages = state.Languages.IsDefault ? [] : state.Languages;
        var items = new List<SwiftView> {
            BrowserPage.SortMenuSection(Prefix, state.SortField, state.SortDescending, text,
                actions.Sort),
            BrowserPage.FilterSubmenu(Prefix, "type", text("NativeResources_TypeFilter"),
                text("Resources_AllTypes"), types, state.MediaType, actions.FilterType),
            BrowserPage.FilterSubmenu(Prefix, "language", text("NativeResources_LanguageFilter"),
                text("Resources_AllLanguages"), languages, state.Language, actions.FilterLanguage),
        };
        items.AddRange(BrowserPage.ClearAllMenuItems(Prefix, text, actions.ClearFilters,
            state.HasActiveFilters));
        return items;
    }

    /// <param name="bottom">The bottom search bar (its capsule draws the field's surface).</param>
    private static SwiftView SearchRow(
        NativeResourceListState state, SourceUiStyle style,
        Func<string, string> text, LearningResourcesActions actions, bool bottom = false)
    {
        var search = bottom
            ? BrowserPage.SearchField(style, text("Resources_SearchPlaceholder"), state.SearchText,
                actions.Search, "native.resources.search")
            : style.TextInput(Views.TextField(
                    text("Resources_SearchPlaceholder"), state.SearchText, actions.Search))
                .WithFlexibleWidth()
                .WithAccessibilityIdentifier("native.resources.search");
        search.IsEnabled = state.Phase != NativeResourceListPhase.Blocked
            && !state.StarterBusy;
        // As on Vocabulary: one tap clears the search and the filters.
        var clear = state.HasActiveFilters || !string.IsNullOrEmpty(state.SearchText)
            ? Views.Button(Views.Image("xmark"), actions.ClearSearchAndFilters)
                .WithAccessibilityLabel(text("Vocabulary_ClearFilters"))
                .WithAccessibilityIdentifier("native.resources.search.clear")
            : null;
        return BrowserPage.SearchRow(Prefix, search, clear);
    }

    /// <summary>The phone page, and the wide page while it loads or cannot show resources.</summary>
    private static SwiftView SingleColumn(
        NativeResourceListState state, string? feedback, SourceUiStyle style,
        Func<string, string> text, LearningResourcesActions actions, double width)
    {
        var children = new List<SwiftView>();
        if (state.Phase is NativeResourceListPhase.Loading or NativeResourceListPhase.Idle)
            children.Add(Views.IndeterminateProgress(text("Common_Loading"))
                .WithFlexibleWidth(alignment: SwiftHorizontalAlignment.Center)
                .WithAccessibilityIdentifier("native.resources.status"));
        else
        {
            if (Count(state, style, text) is { } count)
                children.Add(count);
            AddResults(children, state, feedback, style, text, actions, width);
        }
        // A wide page shows this while it loads: its search stays at the top.
        var bottom = !BrowserPage.IsWide(width);
        return BrowserPage.SingleColumn(Prefix, style, children,
            SearchRow(state, style, text, actions, bottom), width, bottom,
            bottom ? SearchChips(state, style, text, actions) : null);
    }

    /// <summary>The bottom search bar's chips: the Type and Language filters that are on.</summary>
    private static List<SwiftView> SearchChips(
        NativeResourceListState state, SourceUiStyle style,
        Func<string, string> text, LearningResourcesActions actions)
    {
        var chips = new List<SwiftView>();
        var remove = text("Library_RemoveFilter");
        if (!string.IsNullOrWhiteSpace(state.MediaType))
            chips.Add(BrowserPage.SearchChip(Prefix, "type", text("NativeResources_TypeFilter"),
                state.MediaType, remove, () => actions.FilterType(null), style));
        if (!string.IsNullOrWhiteSpace(state.Language))
            chips.Add(BrowserPage.SearchChip(Prefix, "language",
                text("NativeResources_LanguageFilter"), state.Language, remove,
                () => actions.FilterLanguage(null), style));
        return chips;
    }

    private static SwiftView WideLayout(
        NativeResourceListState state, string? feedback, SourceUiStyle style,
        Func<string, string> text, LearningResourcesActions actions, double width)
    {
        var header = new List<SwiftView>();
        if (Count(state, style, text) is { } count)
            header.Add(count);
        var results = new List<SwiftView>();
        AddResults(results, state, feedback, style, text, actions, width);
        return BrowserPage.Wide(Prefix, style,
            state.FilterPanelHidden ? null : FilterPanel(state, style, text, actions, width),
            SearchRow(state, style, text, actions),
            BrowserPage.ResultsHeader(Prefix, header), results);
    }

    /// <summary>The wide filter column: sort, type and language (applied at once), Clear All Filters.</summary>
    private static SwiftView FilterPanel(
        NativeResourceListState state, SourceUiStyle style,
        Func<string, string> text, LearningResourcesActions actions, double width)
    {
        var fields = new List<SwiftView> {
            BrowserPage.SortField(Prefix, state.SortField, state.SortDescending, style, text,
                actions.Sort)
        };
        fields.AddRange(FilterFields(state, state.MediaType, state.Language, style, text,
            actions.FilterType, actions.FilterLanguage));
        if (state.HasActiveFilters)
            fields.Add(BrowserPage.ClearAllButton("native.resources.filters.panel.clear",
                style, text, actions.ClearFilters));
        return BrowserPage.FilterPanel(Prefix, style, width, fields);
    }

    private static IEnumerable<SwiftView> FilterFields(
        NativeResourceListState state, string? mediaType, string? language,
        SourceUiStyle style, Func<string, string> text,
        Action<string?> changeType, Action<string?> changeLanguage)
    {
        var types = state.Types.IsDefault ? [] : state.Types;
        var languages = state.Languages.IsDefault ? [] : state.Languages;
        yield return BrowserPage.FilterMenu(Prefix, "type", text("NativeResources_TypeFilter"),
            text("Resources_AllTypes"), types, mediaType, style, changeType);
        yield return BrowserPage.FilterMenu(Prefix, "language",
            text("NativeResources_LanguageFilter"), text("Resources_AllLanguages"), languages,
            language, style, changeLanguage);
    }

    /// <summary>Source: the count above the resources, shown while there are some.</summary>
    private static SwiftView? Count(
        NativeResourceListState state, SourceUiStyle style, Func<string, string> text) =>
        state.Phase != NativeResourceListPhase.Populated ? null
            : style.Text(Views.Text(string.Format(CultureInfo.CurrentCulture,
                    text("Resources_ResourcesCount"), Items(state).Length)),
                    SourceTextRole.Small, SourceTextTone.Secondary)
                .WithAccessibilityIdentifier("native.resources.count");

    private static ImmutableArray<NativeResourceItem> Items(NativeResourceListState state) =>
        state.Items.IsDefault ? ImmutableArray<NativeResourceItem>.Empty : state.Items;

    /// <summary>Sync status, feedback, why the list cannot show, the empty page or the resources.</summary>
    private static void AddResults(
        List<SwiftView> children, NativeResourceListState state, string? feedback,
        SourceUiStyle style, Func<string, string> text, LearningResourcesActions actions,
        double width)
    {
        var syncStatus = NativeResourceSyncStatus.Describe(
            state.Sync, state.SyncErrorKey, text);
        if (!string.IsNullOrEmpty(syncStatus))
            children.Add(style.Text(Views.Text(syncStatus),
                    SourceTextRole.Caption1, SourceTextTone.Secondary)
                .WithAccessibilityIdentifier("native.resources.sync.status"));
        if (!string.IsNullOrWhiteSpace(feedback))
            children.Add(style.Text(Views.Text(feedback),
                    SourceTextRole.Body2, SourceTextTone.Secondary)
                .WithAccessibilityIdentifier("native.resources.feedback"));
        if (state.ErrorKey == "NativeResources_LoadError"
            || state.Phase is NativeResourceListPhase.LoadError
                or NativeResourceListPhase.Blocked)
        {
            children.Add(style.Text(Views.Text(
                    text(state.ErrorKey ?? "NativeResources_LoadError")),
                    SourceTextRole.Body1, SourceTextTone.Danger)
                .WithAccessibilityIdentifier("native.resources.status"));
            if (state.Phase != NativeResourceListPhase.Blocked)
                children.Add(Views.Button(text(NativeText.Retry), actions.Reload)
                    .WithAccessibilityIdentifier("native.resources.retry"));
        }
        if (state.Phase is NativeResourceListPhase.Empty or NativeResourceListPhase.NoMatches)
            children.Add(Empty(state, style, text, actions));
        else if (state.Phase == NativeResourceListPhase.Populated)
        {
            var resources = Items(state);
            if (state.ViewMode == NativeResourceViewMode.Grid)
                children.Add(Views.LazyVGrid(
                        GridColumns(width, style.TextScale, !state.FilterPanelHidden),
                        resources.Select(item => LearningResourceRowView.Row(item, text,
                            actions.Open, style, list: false)).ToArray())
                    .WithAccessibilityIdentifier("native.resources.grid"));
            else
                children.Add(ResourceListGroup(resources, text, actions.Open, style, width));
        }
        if (state.StarterBusy)
            children.Add(style.Text(Views.Text(text("Resources_CreatingStarter")),
                    SourceTextRole.Body2, SourceTextTone.Secondary)
                .WithAccessibilityIdentifier("native.resources.starter.progress"));
    }

    /// <summary>Source: No resources found, Add your first resource and Create a starter resource.</summary>
    private static SwiftView Empty(
        NativeResourceListState state, SourceUiStyle style,
        Func<string, string> text, LearningResourcesActions actions)
    {
        var message = style.Text(Views.Text(text("Resources_NoResourcesFound")),
                SourceTextRole.Body1, SourceTextTone.Secondary)
            .WithFlexibleWidth(alignment: SwiftHorizontalAlignment.Center)
            .WithAccessibilityIdentifier("native.resources.status");
        var starter = style.SecondaryButton(
                text("Resources_CreateStarter"), actions.CreateStarter)
            .WithAccessibilityIdentifier("native.resources.empty.starter");
        SourceUiStyle.SetCustomButtonAvailability(starter, style.Appearance,
            SourceButtonRole.FilledSecondary, !state.StarterBusy);
        var emptyActions = Views.HStack(
                style.PrimaryButton(text("Resources_AddFirstResource"), actions.Add)
                    .WithAccessibilityIdentifier("native.resources.empty.add"),
                starter)
            .WithFlexibleWidth(alignment: SwiftHorizontalAlignment.Center)
            .WithAccessibilityIdentifier("native.resources.empty.actions");
        emptyActions.Spacing = SourceUiLayout.ItemGap;
        var empty = Views.VStack(message, emptyActions)
            .WithPadding(48)
            .WithFlexibleWidth()
            .WithAccessibilityIdentifier("native.resources.empty");
        empty.Spacing = SourceUiLayout.LargeCardInset;
        return empty;
    }

    private static SwiftView ResourceListGroup(
        ImmutableArray<NativeResourceItem> resources, Func<string, string> text,
        Action<string> open, SourceUiStyle style, double width)
    {
        var rows = new List<SwiftView>();
        foreach (var resource in resources)
        {
            if (rows.Count != 0)
                rows.Add(Views.Divider());
            rows.Add(LearningResourceRowView.Row(resource, text, open, style, list: true, width));
        }
        var group = Views.LazyVGrid(1, rows.ToArray())
            .WithAccessibilityIdentifier("native.resources.list");
        group.Spacing = 0;
        return style.BorderedCard(group, radius: style.Appearance.BootstrapRadius);
    }
}
