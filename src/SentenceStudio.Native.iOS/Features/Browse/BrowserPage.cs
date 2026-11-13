using Microsoft.SwiftUI;
using SentenceStudio.Native.iOS.Styling;
using Views = Microsoft.SwiftUI.SwiftUI;

namespace SentenceStudio.Native.iOS.Features.Browse;

/// <summary>
/// The layout the Library's browse pages share (Captain 2026-11-09 for Vocabulary, 2026-11-10 for
/// Resources: "make those consistent"). Phone: one scrolling column; filters and sort in a sheet.
/// Wide: a fixed filter column (sort first, then the filters, Clear All Filters while one is on)
/// beside the results: the search row stays put above their own scroll. The toolbar holds plain
/// icons: the page's own, the grid/list switch, Filters (the sheet on a phone, the column's
/// show/hide when wide) and Add (+) last.
/// </summary>
/// <remarks>
/// Every part's ID starts with the page's prefix (<c>native.vocabulary</c>,
/// <c>native.resources</c>), so <see cref="BrowserPageUpdate"/> can update a mounted page in place.
/// </remarks>
internal static class BrowserPage
{
    /// <summary>From the source md breakpoint the filter column sits beside the results.</summary>
    internal static bool IsWide(double width) => width >= SourceUiLayout.DesktopWidth;

    /// <summary>The page's own width when it has reported one, else the window's.</summary>
    internal static double Width(double? pageWidth, SourceUiStyle style) =>
        pageWidth is > 0 and var measured && double.IsFinite(measured)
            ? measured : style.ViewportWidth;

    /// <summary>
    /// The wide filter column: about a third of the window (Captain 2026-11-09), at least 280
    /// points and at most 400 so the results keep the rest. Larger text widens it in proportion
    /// (up to half the window) so its sort buttons and menus keep their words.
    /// </summary>
    internal static double FilterColumnWidth(double width, double textScale = 1)
    {
        var scale = double.IsFinite(textScale) && textScale > 1 ? textScale : 1;
        return Math.Clamp(Math.Round(width / 3 * scale), 280,
            Math.Max(280, Math.Min(width / 2, 400 * scale)));
    }

    /// <summary>
    /// Card columns for a page <paramref name="width"/> points wide. Phone: the source's one
    /// column. Wide: one in list mode; in grid mode as many as the results column holds at this
    /// text size (one under 440 points, two under 760, then three), larger text counting as a
    /// narrower column.
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

    /// <summary>
    /// The page in its toolbar. The content reports its own width as it appears and changes
    /// (an iPad sidebar takes part of the window), for the layout to follow.
    /// </summary>
    internal static SwiftView WithToolbar(string prefix, SwiftView page, SourceUiStyle style,
        Action<double>? pageWidthChanged, IReadOnlyList<SwiftView> actions)
    {
        var content = style.Page(page);
        if (pageWidthChanged is { } changed)
            content.OnViewportChanged((measuredWidth, _) => changed(measuredWidth));
        return Views.Toolbar(content, actions.ToArray())
            .WithAccessibilityIdentifier($"{prefix}.page");
    }

    // ------------------------------------------------------------------ toolbar icons

    /// <summary>
    /// Add (+): the same plain toolbar icon on every browse page (Captain 2026-11-10), last in the
    /// row. Its action must not depend on the state it was built with (the page updates in place).
    /// </summary>
    internal static SwiftView AddButton(string prefix, string label, Action add, bool enabled)
    {
        var button = Views.Button(Views.Image("plus"), add)
            .WithAccessibilityLabel(label)
            .WithAccessibilityIdentifier($"{prefix}.add");
        button.IsEnabled = enabled;
        return button;
    }

    /// <summary>
    /// The grid/list switch, showing the layout a tap switches to (Captain 2026-11-09). Its
    /// action reads the mode when tapped.
    /// </summary>
    internal static SwiftView ViewToggle(string prefix, bool listMode, Action toggle,
        string gridLabel, string listLabel, bool enabled)
    {
        var button = Views.Button(Views.Image(listMode ? "square.grid.3x3" : "list.bullet"), toggle)
            .WithAccessibilityLabel(listMode ? gridLabel : listLabel)
            .WithAccessibilityIdentifier($"{prefix}.view.toggle");
        button.IsEnabled = enabled;
        return button;
    }

    /// <summary>
    /// Filters: the sheet on a phone; the filter column's show/hide when wide (selected while
    /// the column shows). Filled while a filter is on.
    /// </summary>
    internal static SwiftView FiltersButton(string prefix, string label, bool active, bool wide,
        bool ready, bool panelShown, Action openSheet, Action togglePanel)
    {
        var button = Views.Button(Views.Image(active
                    ? "line.3.horizontal.decrease.circle.fill"
                    : "line.3.horizontal.decrease.circle"),
                wide ? togglePanel : openSheet)
            .WithAccessibilityLabel(label)
            .WithAccessibilitySelected(wide && ready && panelShown)
            .WithAccessibilityIdentifier($"{prefix}.filters.open");
        button.IsEnabled = !wide || ready;
        return button;
    }

    /// <summary>
    /// Filters as a native menu from the toolbar (Captain 2026-11-12: "a native menu popover
    /// like the native apps"): Sort, then one submenu per filter with a checkmark on its choice,
    /// applied at once, and Clear All Filters while one is on. The symbol is filled while a
    /// filter is on.
    /// </summary>
    internal static SwiftView FilterMenu(string prefix, string label, bool active, bool enabled,
        IEnumerable<SwiftView> items)
    {
        var menu = Views.Menu(Views.Image(active
                    ? "line.3.horizontal.decrease.circle.fill"
                    : "line.3.horizontal.decrease.circle"),
                items.ToArray())
            .WithAccessibilityLabel(label)
            .WithAccessibilityIdentifier($"{prefix}.filters.open");
        menu.IsEnabled = enabled;
        return menu;
    }

    /// <summary>The filter menu's Sort section: Added and Updated, the chosen one with its direction.</summary>
    internal static SwiftView SortMenuSection(string prefix, string field, bool descending,
        Func<string, string> text, Action<string> changed)
    {
        SwiftView Item(string sort, string labelKey) =>
            Views.Button(field == sort
                    ? $"{text(labelKey)} {(descending ? "↓" : "↑")}" : text(labelKey),
                    () => changed(sort))
                .WithAccessibilitySelected(field == sort)
                .WithAccessibilityIdentifier($"{prefix}.sort.{sort}");
        return Views.Section(text("Vocabulary_SortLabel"),
                Item("added", "Vocabulary_SortAdded"), Item("updated", "Vocabulary_SortUpdated"))
            .WithAccessibilityIdentifier($"{prefix}.sort");
    }

    /// <summary>
    /// One filter as a submenu of the filter menu: "All" (<paramref name="allLabel"/>) then the
    /// <paramref name="values"/>, a checkmark on the chosen one; a <paramref name="selected"/>
    /// value the list no longer has stays choosable.
    /// </summary>
    internal static SwiftView FilterSubmenu(string prefix, string type, string label,
        string allLabel, IReadOnlyList<string> values, string? selected,
        Action<string?> changed, Func<string, string>? valueLabel = null)
    {
        var known = values.Count;
        if (!string.IsNullOrWhiteSpace(selected)
            && !values.Any(value => value.Equals(selected, StringComparison.OrdinalIgnoreCase)))
            values = [.. values, selected];
        var options = new[] { allLabel }
            .Concat(values.Select((value, index) =>
                index < known ? valueLabel?.Invoke(value) ?? value : value))
            .ToArray();
        var index = values
            .Select((value, position) => (value, position))
            .FirstOrDefault(item => item.Item1.Equals(selected, StringComparison.OrdinalIgnoreCase),
                (string.Empty, -1)).Item2 + 1;
        SwiftView? picker = null;
        picker = Views.Picker(label, options, index, selection =>
            {
                if (selection <= 0)
                {
                    changed(null);
                    return;
                }
                // The menu updates in place and keeps this handler, so a catalog list (whose
                // option titles are its values) reads the mounted options, not this build's
                // list. A labelled list (association, status...) has fixed values.
                if (valueLabel is null && picker is { } mounted
                    && selection < mounted.Children.Count)
                    changed(mounted.Children[selection].Text);
                else
                    changed(selection <= values.Count ? values[selection - 1] : null);
            })
            .WithPickerStyle(SwiftPickerStyle.Menu)
            .WithAccessibilityIdentifier($"{prefix}.filter.{type}");
        return picker;
    }

    /// <summary>
    /// The filter menu's Clear All Filters, after a divider: always there (unavailable while no
    /// filter is on), so the menu keeps its shape and the page updates in place as the person
    /// types.
    /// </summary>
    internal static IEnumerable<SwiftView> ClearAllMenuItems(string prefix,
        Func<string, string> text, Action clear, bool enabled)
    {
        var button = Views.Button(text("Vocabulary_ClearFilters"), clear)
            .WithAccessibilityIdentifier($"{prefix}.filters.clear");
        button.IsEnabled = enabled;
        return [Views.Divider(), button];
    }

    // ------------------------------------------------------------------ filter fields

    /// <summary>Added and Updated, the chosen one marked with its direction (Captain 2026-11-09).</summary>
    internal static SwiftView SortField(string prefix, string field, bool descending,
        SourceUiStyle style, Func<string, string> text, Action<string> changed)
    {
        var sort = Views.ButtonGroup([
                SortButton(prefix, "added", "Vocabulary_SortAdded", field, descending, style,
                    text, changed, SourceButtonGroupPosition.First),
                SortButton(prefix, "updated", "Vocabulary_SortUpdated", field, descending, style,
                    text, changed, SourceButtonGroupPosition.Last)
            ], selectedIndex: field == "updated" ? 1 : 0)
            .WithAccessibilityIdentifier($"{prefix}.sort");
        return Views.VStack(
                style.Text(Views.Text(text("Vocabulary_SortLabel")),
                    SourceTextRole.Body2, SourceTextTone.Secondary),
                sort)
            .WithFlexibleWidth()
            .WithAccessibilityIdentifier($"{prefix}.filter.field.sort");
    }

    private static SwiftView SortButton(string prefix,
        string sort, string labelKey, string field, bool descending,
        SourceUiStyle style, Func<string, string> text, Action<string> changed,
        SourceButtonGroupPosition position) =>
        field == sort
            ? style.PrimaryButton(
                    $"{text(labelKey)} {(descending ? "↓" : "↑")}",
                    () => changed(sort), groupPosition: position)
                .WithAccessibilityIdentifier($"{prefix}.sort.{sort}")
            : style.OutlineButton(Views.Text(text(labelKey)),
                    () => changed(sort), groupPosition: position)
                .WithAccessibilityIdentifier($"{prefix}.sort.{sort}");

    /// <summary>
    /// A labelled menu of <paramref name="values"/> after "All" (<paramref name="allLabel"/>); a
    /// <paramref name="selected"/> value the list no longer has stays choosable.
    /// </summary>
    internal static SwiftView FilterMenu(string prefix, string type, string label, string allLabel,
        IReadOnlyList<string> values, string? selected, SourceUiStyle style,
        Action<string?> changed, Func<string, string>? valueLabel = null)
    {
        var known = values.Count;
        if (!string.IsNullOrWhiteSpace(selected)
            && !values.Any(value => value.Equals(selected, StringComparison.OrdinalIgnoreCase)))
            values = [.. values, selected];
        var options = new[] { allLabel }
            .Concat(values.Select((value, index) =>
                index < known ? valueLabel?.Invoke(value) ?? value : value))
            .ToArray();
        var index = values
            .Select((value, position) => (value, position))
            .FirstOrDefault(item => item.Item1.Equals(selected, StringComparison.OrdinalIgnoreCase),
                (string.Empty, -1)).Item2 + 1;
        var picker = style.Input(Views.Picker(
                label, options, index,
                selection => changed(selection is > 0 && selection <= values.Count
                    ? values[selection - 1] : null))
            .WithPickerStyle(SwiftPickerStyle.Menu)
            .WithFlexibleWidth())
            .WithAccessibilityLabel(label)
            .WithAccessibilityIdentifier($"{prefix}.filter.{type}");
        return Views.VStack(
                style.Text(Views.Text(label), SourceTextRole.Body2, SourceTextTone.Secondary),
                picker)
            .WithFlexibleWidth()
            .WithAccessibilityIdentifier($"{prefix}.filter.field.{type}");
    }

    // ------------------------------------------------------------------ layout

    /// <summary>The search field, with a clear-filters button beside it while a filter is on.</summary>
    internal static SwiftView SearchRow(string prefix, SwiftView search, SwiftView? clear)
    {
        var row = (clear is null ? Views.HStack(search) : Views.HStack(search, clear))
            .WithFlexibleWidth()
            .WithAccessibilityIdentifier($"{prefix}.search.row");
        row.Spacing = SourceUiLayout.ItemGap;
        return row;
    }

    /// <summary>
    /// What a narrow page's system search field shows and reports: the words, and the active
    /// filters as search tokens inside the field (Captain 2026-11-12: "I want the native way").
    /// Its actions must not depend on the state they were built with (the page updates in place).
    /// </summary>
    internal sealed record PageSearch(string Placeholder, string Text, Action<string> Changed,
        IReadOnlyList<SwiftSearchToken> Tokens, Action<IReadOnlyList<string>> TokensChanged);

    /// <summary>
    /// The phone page (and the list column of the Library split view): one scrolling column
    /// under the system search field (SwiftUI <c>searchable</c>; the system places it, at the
    /// bottom on a phone), its filters as tokens in the field.
    /// </summary>
    /// <param name="width">The page's own width (a split view's list column is narrow).</param>
    /// <param name="search">Null leaves the page without search (its list cannot be searched now).</param>
    /// <param name="list">
    /// The items as a native List (rows, swipe actions) in place of the scrolling column
    /// (Captain 2026-11-12: native lists; the grid stays a lazy grid in the scroll).
    /// </param>
    internal static SwiftView SingleColumn(string prefix, IEnumerable<SwiftView> children,
        double width, PageSearch? search, SourceUiStyle? list = null)
    {
        var content = list is null ? Scroll(prefix, children, width)
            : NativeList($"{prefix}.scroll.content", children, list);
        return search is null ? content
            : Views.Searchable(content, search.Placeholder, search.Text,
                    search.Changed, search.Tokens, search.TokensChanged)
                // In the bottom toolbar, as in Notes; it rises above the keyboard while typing.
                .WithSearchPlacement(SwiftSearchPlacement.BottomBar)
                // Filters, grid/list and Add stay usable while searching (Captain 2026-11-12:
                // "once I tap into the search field I can no longer use the filters").
                .WithSearchKeepingToolbar()
                .WithAccessibilityIdentifier($"{prefix}.search");
    }

    /// <summary>
    /// A native List in the app's colours: the page canvas behind it and list row surfaces.
    /// Its children are its rows (the scene rebuilds them on each update).
    /// </summary>
    internal static SwiftView NativeList(string id, IEnumerable<SwiftView> rows, SourceUiStyle style)
    {
        var appearance = style.Appearance;
        return Views.List(rows.ToArray())
            .WithNativeListColors(appearance.BodyBackground.Light, appearance.BodyBackground.Dark,
                appearance.ListRowSurface.Light, appearance.ListRowSurface.Dark)
            .WithAccessibilityIdentifier(id);
    }

    /// <summary>
    /// The wide page while it loads or cannot show its items: its search row stays at the top,
    /// where the loaded wide page has it.
    /// </summary>
    internal static SwiftView SingleColumnWithTopSearch(string prefix,
        IEnumerable<SwiftView> children, SwiftView searchRow, double width)
    {
        var gutter = SourceUiLayout.PageGutter(width);
        var top = Views.VStack(searchRow.WithPadding(gutter, gutter, 0, gutter),
                Scroll(prefix, children, width))
            .WithFlexibleWidth();
        top.Spacing = 0;
        return top;
    }

    /// <summary>
    /// The column's one scroll; its content owns the gutter. Dragging it puts the keyboard
    /// away, as in Messages and Notes.
    /// </summary>
    private static SwiftView Scroll(string prefix, IEnumerable<SwiftView> children, double width)
    {
        var content = Views.VStack(children.ToArray())
            .WithPadding(SourceUiLayout.PageGutter(width))
            .WithFlexibleWidth()
            .WithAccessibilityIdentifier($"{prefix}.scroll.content");
        content.Spacing = SourceUiLayout.PageGap;
        return Views.Scroll(content)
            .WithScrollDismissesKeyboard(SwiftScrollDismissesKeyboard.Interactively)
            .WithAccessibilityIdentifier($"{prefix}.scroll");
    }

    /// <summary>
    /// Chips in one row that scrolls sideways when they do not fit (the wide page's filter
    /// chips). The row is <paramref name="id"/>.row.
    /// </summary>
    internal static SwiftView ChipStrip(string id, IReadOnlyList<SwiftView> chips, double spacing)
    {
        var row = Views.HStack(chips.ToArray())
            .WithAccessibilityIdentifier($"{id}.row");
        var strip = Views.HorizontalGrid(new double[chips.Count], row)
            .WithAccessibilityIdentifier(id);
        strip.Spacing = spacing;
        return strip;
    }

    /// <summary>
    /// The wide page: the filter column (when shown) and a divider, then the results: the search
    /// row stays put above their own scroll, which starts with <paramref name="header"/>.
    /// </summary>
    /// <param name="asList">The results as a native List (the header is its first row).</param>
    internal static SwiftView Wide(string prefix, SourceUiStyle style, SwiftView? filterPanel,
        SwiftView searchRow, SwiftView? header, IEnumerable<SwiftView> results, bool asList = false)
    {
        // No header (nothing above the results): the list or column starts with the results.
        SwiftView[] rows = header is null ? [.. results] : [header, .. results];
        var gutter = SourceUiLayout.PageGutter(style.ViewportWidth);
        var columns = new List<SwiftView>();
        if (filterPanel is not null)
        {
            columns.Add(filterPanel);
            columns.Add(Views.Divider().WithAccessibilityIdentifier($"{prefix}.filters.panel.divider"));
        }
        SwiftView scroll;
        if (asList)
            scroll = NativeList($"{prefix}.results.content", rows, style);
        else
        {
            var content = Views.VStack(rows)
                .WithPadding(0, gutter, gutter, gutter)
                .WithFlexibleWidth()
                .WithAccessibilityIdentifier($"{prefix}.results.content");
            content.Spacing = SourceUiLayout.PageGap;
            scroll = Views.Scroll(content).WithAccessibilityIdentifier($"{prefix}.results.scroll");
        }
        var column = Views.VStack(
                searchRow.WithPadding(gutter, gutter, 0, gutter),
                scroll)
            .WithFlexibleWidth()
            .WithAccessibilityIdentifier($"{prefix}.results");
        column.Spacing = SourceUiLayout.PageGap;
        columns.Add(column);
        var layout = Views.HStack(columns.ToArray())
            .WithFlexibleWidth()
            .WithAccessibilityIdentifier($"{prefix}.wide.layout");
        layout.Spacing = 0;
        return layout;
    }

    /// <summary>The wide results' header: the counts and anything else that scrolls with the results.</summary>
    internal static SwiftView ResultsHeader(string prefix, IEnumerable<SwiftView> rows)
    {
        var header = Views.VStack(rows.ToArray())
            .WithFlexibleWidth()
            .WithAccessibilityIdentifier($"{prefix}.results.header");
        header.Spacing = SourceUiLayout.ItemGap;
        return header;
    }

    /// <summary>
    /// The wide filter column: its fields (sort first) and Clear All Filters while one is on.
    /// It scrolls on its own and keeps a fixed width beside the results.
    /// </summary>
    internal static SwiftView FilterPanel(string prefix, SourceUiStyle style, double width,
        IEnumerable<SwiftView> fields)
    {
        var content = Views.VStack(fields.ToArray())
            .WithPadding(SourceUiLayout.PageGutter(style.ViewportWidth))
            .WithFlexibleWidth()
            .WithAccessibilityIdentifier($"{prefix}.filters.panel.content");
        content.Spacing = SourceUiLayout.FormGap;
        return Views.Scroll(content)
            .WithFixedWidth(FilterColumnWidth(width, style.TextScale))
            .WithAccessibilityIdentifier($"{prefix}.filters.panel");
    }

    /// <summary>Clear All Filters (the column's last field, and the sheet's).</summary>
    internal static SwiftView ClearAllButton(string id, SourceUiStyle style,
        Func<string, string> text, Action clear) =>
        style.SecondaryButton(text("Vocabulary_ClearAllFilters"), clear)
            .WithAccessibilityIdentifier(id);

    /// <summary>
    /// The phone filter sheet: Filters and Close, the fields (sort first), Clear All Filters,
    /// then Cancel and Apply (the choices wait for Apply).
    /// </summary>
    internal static SwiftView FilterSheet(string prefix, SourceUiStyle style,
        Func<string, string> text, IEnumerable<SwiftView> fields,
        Action clear, Action cancel, Action apply)
    {
        var header = Views.HStack(
                style.Text(Views.Text(text("Vocabulary_Filters")),
                        SourceTextRole.Title3, sourceHeading: true)
                    .WithFlexibleWidth(),
                Views.Button(Views.Image("xmark"), cancel)
                    .WithAccessibilityLabel(text("Common_Close"))
                    .WithAccessibilityIdentifier($"{prefix}.filters.close"))
            .WithAccessibilityIdentifier($"{prefix}.filters.heading");
        var buttons = Views.HStack(
                style.OutlineButton(Views.Text(text("Common_Cancel")), cancel)
                    .WithAccessibilityIdentifier($"{prefix}.filters.cancel"),
                style.PrimaryButton(text("Vocabulary_BulkApply"), apply)
                    .WithAccessibilityIdentifier($"{prefix}.filters.apply"))
            .WithFlexibleWidth()
            .WithAccessibilityIdentifier($"{prefix}.filters.actions");
        buttons.Spacing = SourceUiLayout.ItemGap;
        var panel = Views.VStack([header, .. fields,
                ClearAllButton($"{prefix}.filters.clear", style, text, clear), buttons])
            .WithPadding(SourceUiLayout.CardInset)
            .WithFlexibleWidth()
            .WithAccessibilityIdentifier($"{prefix}.filters.sheet.content");
        panel.Spacing = SourceUiLayout.FormGap;
        return style.Page(Views.Scroll(panel))
            .WithAccessibilityIdentifier($"{prefix}.filters.sheet");
    }
}
