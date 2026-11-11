namespace SentenceStudio.Native;

/// <summary>
/// The Resources list's sort, filter column and phone filter sheet (Captain 2026-11-10: the
/// Resources page follows the Vocabulary list's layout and filter and sort options). Smart
/// resources stay first, as the source orders them; the rest follow the chosen sort.
/// </summary>
public sealed partial class NativeResourcePresenter
{
    private const string SortKey = "resources-sort";
    private const string FilterPanelKey = "resources-filter-panel";

    /// <summary>
    /// Sorts by <paramref name="field"/> (Added or Updated): the same field again flips the
    /// direction; another starts newest first. Remembered on this device.
    /// </summary>
    public NativeResourceListState SetSort(string field)
    {
        if (!TryCurrentOwner(out var owner) || _owner != owner)
            return Block();
        var (sortField, descending) = NextSort(_sortField, _sortDescending, field);
        _sortField = sortField;
        _sortDescending = descending;
        SaveSort();
        return CanProjectList() ? ProjectList() : _list = WithListOptions(_list);
    }

    /// <summary>The wide filter column shows or hides; remembered on this device.</summary>
    public NativeResourceListState ToggleFilterPanel()
    {
        if (!TryCurrentOwner(out var owner) || _owner != owner)
            return Block();
        _filterPanelHidden = !_filterPanelHidden;
        preferences.Set(FilterPanelKey, _filterPanelHidden ? "hidden" : "shown");
        return _list = WithListOptions(_list);
    }

    /// <summary>The phone sheet opens on the list's own type, language and sort.</summary>
    public NativeResourceListState OpenFilterSheet()
    {
        if (!TryCurrentOwner(out var owner) || _owner != owner)
            return Block();
        _sheet = new(_mediaType, _language, _sortField, _sortDescending);
        return _list = WithListOptions(_list);
    }

    /// <summary>A sheet choice (type or language; null is All), applied by Apply.</summary>
    public NativeResourceListState ChangeSheetFilter(string? mediaType, string? language)
    {
        if (!TryCurrentOwner(out var owner) || _owner != owner)
            return Block();
        if (_sheet is not { } sheet)
            return _list;
        _sheet = sheet with
        {
            MediaType = CatalogValue(TypeCatalog, mediaType, nameof(mediaType)),
            Language = CatalogValue(LanguageCatalog, language, nameof(language))
        };
        return _list = WithListOptions(_list);
    }

    /// <summary>The sheet's sort, applied by Apply (the same field again flips the direction).</summary>
    public NativeResourceListState SetSheetSort(string field)
    {
        if (!TryCurrentOwner(out var owner) || _owner != owner)
            return Block();
        if (_sheet is not { } sheet)
            return _list;
        var (sortField, descending) = NextSort(sheet.SortField, sheet.SortDescending, field);
        _sheet = sheet with { SortField = sortField, SortDescending = descending };
        return _list = WithListOptions(_list);
    }

    /// <summary>Apply: the sheet's type, language and sort become the list's.</summary>
    public NativeResourceListState ApplyFilterSheet()
    {
        if (!TryCurrentOwner(out var owner) || _owner != owner)
            return Block();
        if (_sheet is not { } sheet)
            return _list;
        _sheet = null;
        _mediaType = sheet.MediaType;
        _language = sheet.Language;
        if (sheet.SortField != _sortField || sheet.SortDescending != _sortDescending)
        {
            _sortField = sheet.SortField;
            _sortDescending = sheet.SortDescending;
            SaveSort();
        }
        return CanProjectList() ? ProjectList()
            : _list = WithListOptions(_list with { MediaType = _mediaType, Language = _language });
    }

    /// <summary>Cancel (or the sheet swiped away): its choices are dropped.</summary>
    public NativeResourceListState CancelFilterSheet()
    {
        if (_sheet is null)
            return _list;
        _sheet = null;
        return _list = WithListOptions(_list);
    }

    /// <summary>
    /// Clear All Filters: type and language back to All, at once (an open sheet's choices too,
    /// as the Vocabulary sheet clears); the sort and the search stay.
    /// </summary>
    public NativeResourceListState ClearFilters()
    {
        if (!TryCurrentOwner(out var owner) || _owner != owner)
            return Block();
        _mediaType = null;
        _language = null;
        if (_sheet is { } sheet)
            _sheet = sheet with { MediaType = null, Language = null };
        return CanProjectList() ? ProjectList()
            : _list = WithListOptions(_list with { MediaType = null, Language = null });
    }

    /// <summary>The search row's clear (as on Vocabulary): the search and the filters, at once.</summary>
    public NativeResourceListState ClearSearchAndFilters()
    {
        if (!TryCurrentOwner(out var owner) || _owner != owner)
            return Block();
        _searchText = string.Empty;
        _list = _list with { SearchText = string.Empty };
        return ClearFilters();
    }

    /// <summary>The list state with the sort, filter column and sheet as they are now.</summary>
    private NativeResourceListState WithListOptions(NativeResourceListState state) =>
        state with
        {
            SortField = _sortField,
            SortDescending = _sortDescending,
            FilterPanelHidden = _filterPanelHidden,
            Sheet = _sheet
        };

    /// <summary>
    /// Smart resources first (by title, as the source), then the rest by the chosen sort. The
    /// sort is stable over the store's source order, so equal dates keep it.
    /// </summary>
    private IEnumerable<NativeResourceItem> Ordered(IEnumerable<NativeResourceItem> filtered)
    {
        // The search and filters run once; smart and ordinary resources are split from that.
        var items = filtered.ToList();
        var smart = items.Where(item => item.IsSmartResource)
            .OrderBy(item => item.Title, StringComparer.CurrentCultureIgnoreCase);
        Func<NativeResourceItem, DateTime> key = _sortField == NativeResourceSort.Added
            ? item => item.CreatedAt : item => item.UpdatedAt;
        var others = items.Where(item => !item.IsSmartResource);
        var sorted = _sortDescending
            ? others.OrderByDescending(key).ThenByDescending(item => item.CreatedAt)
            : others.OrderBy(key).ThenBy(item => item.CreatedAt);
        return smart.Concat(sorted);
    }

    /// <summary>The device's remembered sort and filter column (an owner change reads them again).</summary>
    private void ReadListOptions()
    {
        var parts = preferences.Get(SortKey, $"{NativeResourceSort.Updated}:desc").Split(':', 2);
        _sortField = parts[0] == NativeResourceSort.Added ? NativeResourceSort.Added : NativeResourceSort.Updated;
        _sortDescending = parts.Length < 2 || parts[1] != "asc";
        _filterPanelHidden = preferences.Get(FilterPanelKey, "shown") == "hidden";
        _sheet = null;
    }

    private void SaveSort() =>
        preferences.Set(SortKey, $"{_sortField}:{(_sortDescending ? "desc" : "asc")}");

    private static (string Field, bool Descending) NextSort(string field, bool descending, string chosen)
    {
        var next = chosen == NativeResourceSort.Added ? NativeResourceSort.Added : NativeResourceSort.Updated;
        return next == field ? (field, !descending) : (next, true);
    }

    private static string? CatalogValue(System.Collections.Immutable.ImmutableArray<string> catalog,
        string? value, string name) =>
        value is null ? null
            : catalog.FirstOrDefault(option => FilterComparer.Equals(option, value))
                ?? throw new ArgumentException("The selected value is not in the catalog.", name);
}
