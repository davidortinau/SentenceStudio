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
        double? pageWidth = null,
        string? pendingDeleteTitle = null)
    {
        var width = BrowserPage.Width(pageWidth, style);
        var wide = BrowserPage.IsWide(width);
        var ready = IsReady(state);
        var page = wide && ready
            ? WideLayout(state, feedback, style, text, actions, width)
            : SingleColumn(state, feedback, style, text, actions, width);
        // Making a starter resource holds the page still until it is done.
        page.IsEnabled = !state.StarterBusy;
        page = DeleteConfirmation(page, pendingDeleteTitle, text, actions);
        return BrowserPage.WithToolbar(Prefix, page, style, actions.PageWidthChanged,
            ToolbarActions(state, text, actions, wide, ready));
    }

    /// <summary>
    /// A list row's Delete asks first (a native confirmation dialog naming the resource). The
    /// dialog is always there, shown while a delete waits, so the page updates in place.
    /// </summary>
    private static SwiftView DeleteConfirmation(SwiftView page, string? pendingTitle,
        Func<string, string> text, LearningResourcesActions actions)
    {
        var dialog = Views.ConfirmationDialog(page,
            string.Format(System.Globalization.CultureInfo.CurrentCulture,
                text("Library_DeleteResourceConfirm"), pendingTitle ?? string.Empty),
            () => actions.CancelListDelete?.Invoke(),
            Views.Button(text("Common_Delete"), () => actions.ConfirmListDelete?.Invoke())
                .WithButtonRole(SwiftButtonRole.Destructive)
                .WithAccessibilityIdentifier("native.resources.delete.confirm"),
            Views.Button(text("Common_Cancel"), () => actions.CancelListDelete?.Invoke())
                .WithButtonRole(SwiftButtonRole.Cancel)
                .WithAccessibilityIdentifier("native.resources.delete.cancel"))
            .WithAccessibilityIdentifier("native.resources.delete.dialog");
        dialog.IsPresented = pendingTitle is not null;
        return dialog;
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

    /// <summary>The wide page's search row (field and clear).</summary>
    private static SwiftView SearchRow(
        NativeResourceListState state, SourceUiStyle style,
        Func<string, string> text, LearningResourcesActions actions)
    {
        var search = style.TextInput(Views.TextField(
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
            AddResults(children, state, feedback, style, text, actions, width);
        }
        // A wide page shows this while it loads: its search stays at the top.
        if (BrowserPage.IsWide(width))
            return BrowserPage.SingleColumnWithTopSearch(Prefix, children,
                SearchRow(state, style, text, actions), width);
        // No search while the owner is unknown (the field used to be disabled then).
        if (state.Phase == NativeResourceListPhase.Blocked)
            return BrowserPage.SingleColumn(Prefix, children, width, search: null);
        return BrowserPage.SingleColumn(Prefix, children, width, new BrowserPage.PageSearch(
            text("Resources_SearchPlaceholder"), state.SearchText, actions.Search,
            SearchTokens(state),
            // A token deleted in the field turns its filter off.
            remaining => actions.KeepFilterTokens?.Invoke(remaining)),
            AsNativeList(state) ? style : null);
    }

    /// <summary>
    /// List mode is a native List in every state (the grid stays a grid): loading, an error and
    /// the empty page are rows (its buttons are plain-styled, so each takes its own tap), so the
    /// page keeps its outline while typing and the search field keeps its focus and Korean
    /// composition. The wide layout shows only a loaded list.
    /// </summary>
    private static bool AsNativeList(NativeResourceListState state) =>
        state.ViewMode == NativeResourceViewMode.List;

    /// <summary>The search field's tokens: the Type and Language filters that are on.</summary>
    private static List<SwiftSearchToken> SearchTokens(NativeResourceListState state)
    {
        var tokens = new List<SwiftSearchToken>();
        if (!string.IsNullOrWhiteSpace(state.MediaType))
            tokens.Add(new SwiftSearchToken("type", state.MediaType));
        if (!string.IsNullOrWhiteSpace(state.Language))
            tokens.Add(new SwiftSearchToken("language", state.Language));
        return tokens;
    }

    private static SwiftView WideLayout(
        NativeResourceListState state, string? feedback, SourceUiStyle style,
        Func<string, string> text, LearningResourcesActions actions, double width)
    {
        var results = new List<SwiftView>();
        AddResults(results, state, feedback, style, text, actions, width);
        return BrowserPage.Wide(Prefix, style,
            state.FilterPanelHidden ? null : FilterPanel(state, style, text, actions, width),
            SearchRow(state, style, text, actions),
            header: null, results, asList: AsNativeList(state));
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
    /// <summary>
    /// The count under the page title (Captain 2026-11-12: "Put the count with the title like
    /// Notes"); empty until resources are shown.
    /// </summary>
    internal static string CountText(NativeResourceListState state, Func<string, string> text) =>
        state.Phase != NativeResourceListPhase.Populated ? string.Empty
            : string.Format(CultureInfo.CurrentCulture, text("Resources_ResourcesCount"),
                Items(state).Length);

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
                children.AddRange(resources.Select(item => ListRow(item, text, actions, style, width)));
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

    /// <summary>
    /// A native List row (Captain 2026-11-12: native rows, icons kept): swipe left to delete
    /// (not a smart resource, which the system maintains), swipe right to study it.
    /// </summary>
    private static SwiftView ListRow(NativeResourceItem resource, Func<string, string> text,
        LearningResourcesActions actions, SourceUiStyle style, double width)
    {
        var row = LearningResourceRowView.Row(resource, text, actions.Open, style, list: true,
            width, nativeRow: true);
        var trailing = resource.IsSmartResource ? []
            : new[] {
                // Red, not destructive: a destructive swipe action removes its row at once, and
                // this one asks first.
                Views.Button(Views.Label(text("Common_Delete"), "trash"),
                        () => actions.RequestListDelete?.Invoke(resource.Id))
                    .WithTintColor(style.Appearance.Danger.Light, style.Appearance.Danger.Dark)
                    .WithAccessibilityIdentifier($"{NativeAccess.ResourceRow}.{resource.Id}.delete")
            };
        var leading = new[] {
            Views.Button(Views.Label(text("Library_StudyThis"), "graduationcap"),
                    () => actions.Study?.Invoke(resource.Id))
                .WithTintColor(style.Appearance.Primary.Light, style.Appearance.Primary.Dark)
                .WithAccessibilityIdentifier($"{NativeAccess.ResourceRow}.{resource.Id}.study")
        };
        return Views.SwipeRow(row, trailing, leading)
            .WithAccessibilityIdentifier($"{NativeAccess.ResourceRow}.{resource.Id}.swipe");
    }
}
