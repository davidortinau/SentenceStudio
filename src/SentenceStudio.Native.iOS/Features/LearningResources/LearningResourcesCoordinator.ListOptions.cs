using Microsoft.SwiftUI;
using SentenceStudio.Native.iOS.Features.Browse;
using SentenceStudio.Native.iOS.Styling;

namespace SentenceStudio.Native.iOS.Features.LearningResources;

/// <summary>
/// The Resources list's sort, grid/list switch, filter column, phone filter sheet and page
/// width (Captain 2026-11-10: the Resources page follows the Vocabulary list's layout and filter
/// and sort options). The rules are the shared presenter's; this adds the redraws.
/// </summary>
internal sealed partial class LearningResourcesCoordinator
{
    /// <summary>The width the page was last drawn for (its own, or the window's before it reported).</summary>
    private double? _drawnPageWidth;

    /// <summary>The Resources page's own width, once it has reported one.</summary>
    internal double? PageWidth { get; private set; }

    /// <summary>The phone filter sheet is open (its choices wait for Apply).</summary>
    internal bool IsFilterSheetPresented => _presenter.List.Sheet is not null;

    /// <summary>
    /// The sort and grid/list choice the page shows: another one mounts a new page, so the
    /// results start at the top instead of keeping an offset into a different order.
    /// </summary>
    internal string Arrangement
    {
        get
        {
            var list = _presenter.List;
            return $"{list.SortField}:{list.SortDescending}:{list.ViewMode}";
        }
    }

    /// <summary>What the open sheet shows: a change draws it again.</summary>
    internal string FilterSheetKey
    {
        get
        {
            var list = _presenter.List;
            return $"{list.Sheet}|{Join(list.Types)}|{Join(list.Languages)}";

            static string Join(System.Collections.Immutable.ImmutableArray<string> values) =>
                values.IsDefault ? string.Empty : string.Join('\u001f', values);
        }
    }

    /// <summary>
    /// What the page shows: the scene draws it again only when this changes (the list state is
    /// replaced on every change), so an unrelated publish leaves an open filter menu alone.
    /// </summary>
    internal object RenderKey
    {
        get
        {
            var starterPreview = false;
#if DEBUG
            starterPreview = _controlledStarterLoading;
#endif
            return (_presenter.List, _listFeedback, PageWidth, starterPreview);
        }
    }

    /// <summary>The window changed size: the page is drawn for it until it reports again.</summary>
    internal void ForgetPageWidth() => PageWidth = null;

    internal SwiftView RenderFilterSheet(SourceUiStyle style) =>
        LearningResourcesView.FilterSheet(_presenter.List, style, _text,
            new LearningResourceFilterActions(
                ChangeType: type => ChangeSheet(type, _presenter.List.Sheet?.Language),
                ChangeLanguage: language => ChangeSheet(_presenter.List.Sheet?.MediaType, language),
                Sort: field => ListChanged(_presenter.SetSheetSort(field)),
                Apply: () => ListChanged(_presenter.ApplyFilterSheet()),
                Cancel: CancelFilterSheet,
                Clear: ClearFilters));

    /// <summary>The sheet swiped away, Cancel, or the page became wide: its choices are dropped.</summary>
    internal void CancelFilterSheet()
    {
        if (_disposed || _presenter.List.Sheet is null)
            return;
        ListChanged(_presenter.CancelFilterSheet());
    }

    private void SetSort(string field) => ListChanged(_presenter.SetSort(field));

    /// <summary>Reads the mode at tap time: the scene keeps a mounted toolbar button.</summary>
    private void ToggleViewMode() => ListChanged(_presenter.SetViewMode(
        _presenter.List.ViewMode == NativeResourceViewMode.Grid
            ? NativeResourceViewMode.List : NativeResourceViewMode.Grid));

    private void ToggleFilterPanel() => ListChanged(_presenter.ToggleFilterPanel());

    private void OpenFilterSheet()
    {
        if (_disposed || _presenter.List.StarterBusy)
            return;
        ListChanged(_presenter.OpenFilterSheet());
    }

    private void ClearFilters() => ListChanged(_presenter.ClearFilters());

    /// <summary>The search row's clear: the search and the filters, as on Vocabulary.</summary>
    private void ClearSearchAndFilters()
    {
        if (!_disposed)
            ListChanged(_presenter.ClearSearchAndFilters());
    }

    /// <summary>A wide filter change applies at once; a value no longer listed changes nothing.</summary>
    private void FilterNow(string? mediaType, string? language)
    {
        if (_disposed || !Listed(mediaType, language))
            return;
        ListChanged(_presenter.Filter(mediaType, language));
    }

    private void ChangeSheet(string? mediaType, string? language)
    {
        if (_disposed || !Listed(mediaType, language))
            return;
        ListChanged(_presenter.ChangeSheetFilter(mediaType, language));
    }

    /// <summary>
    /// A menu keeps offering a chosen value the list no longer has; the presenter refuses one
    /// that is not in its catalog, so such a choice is ignored here.
    /// </summary>
    private bool Listed(string? mediaType, string? language)
    {
        var list = _presenter.List;
        return (mediaType is null || Contains(list.Types, mediaType))
            && (language is null || Contains(list.Languages, language));

        static bool Contains(System.Collections.Immutable.ImmutableArray<string> values, string value) =>
            !values.IsDefault && values.Contains(value, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Tokens were deleted in the search field (whose ids are "type" and "language"): their
    /// filters turn off in one change. A filter turning off is always allowed (no catalog check),
    /// and the page always redraws, so the field's tokens match the filters again.
    /// </summary>
    private void KeepFilterTokens(IReadOnlyList<string> remaining)
    {
        if (_disposed)
            return;
        var list = _presenter.List;
        ListChanged(_presenter.Filter(
            remaining.Contains("type") ? list.MediaType : null,
            remaining.Contains("language") ? list.Language : null));
    }

    private void ListChanged(NativeResourceListState _)
    {
        if (!_disposed)
            Publish(formChanged: false);
    }

    /// <summary>
    /// The page reported its width (when it appears and as it changes). The phone or wide
    /// layout, the filter column and the card columns follow it, so the list redraws when the
    /// layout flips or the width moved 8 points or more from the one drawn. Becoming wide closes
    /// the phone filter sheet, as crossing the window breakpoint does.
    /// </summary>
    private void OnPageWidthChanged(double width)
    {
        if (_disposed || !double.IsFinite(width) || width <= 0)
            return;
        PageWidth = width;
        var wide = BrowserPage.IsWide(width);
        if (_drawnPageWidth is { } drawn && Math.Abs(drawn - width) < 8
            && BrowserPage.IsWide(drawn) == wide)
            return;
        if (wide && IsFilterSheetPresented)
            _presenter.CancelFilterSheet();
        Publish(formChanged: false);
    }
}
