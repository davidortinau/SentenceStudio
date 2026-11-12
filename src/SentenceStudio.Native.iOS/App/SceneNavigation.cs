using Microsoft.SwiftUI;
using Views = Microsoft.SwiftUI.SwiftUI;

namespace SentenceStudio.Native.iOS.App;

/// <summary>
/// A navigation the scene pushes pages onto. The Library's split view
/// (<see cref="LibrarySplitNavigation"/>) keeps a stack's path and Back rules, so a page's code
/// does not know where it is shown.
/// </summary>
internal interface ISceneNavigation
{
    IReadOnlyList<string> NavigationPath { get; }
    bool CanNavigateBack { get; set; }
    void Push(string route, SwiftView page);
    void Pop();
}

/// <summary>
/// The Library tab as a three-column NavigationSplitView (Captain 2026-11-12: the Library's
/// sidebar lists only its places; the tabs stay in the tab bar). A place's page (Resources,
/// Vocabulary, Skills, Minimal Pairs) fills the content column, the item it opens (a word, a
/// resource, a skill) the detail column, and anything deeper is pushed on the detail column's
/// own stack. A page opened on its own (a new resource from quick add, an import) fills the
/// detail column directly, beside an empty content column.
/// <para>
/// The sidebar shows while nothing is open and tucks away once a page opens; its button brings
/// it back over the screen. In a compact width (a slab iPhone, the folded iPhone Duo) the split
/// view collapses to one column at a time: the path decides which, and the person going back
/// is reported like a Back on a stack.
/// </para>
/// </summary>
internal sealed class LibrarySplitNavigation : ISceneNavigation
{
    private readonly List<string> _path = [];
    private readonly IReadOnlySet<string> _placeRoutes;
    private readonly SwiftView _contentSlot;
    private readonly SwiftView _detailRoot;
    private readonly SwiftView _detailStack;
    private readonly Action<IReadOnlyList<string>> _routesChanged;
    private SwiftView _contentPlaceholder;
    private SwiftView _detailPlaceholder;
    private SwiftView? _contentPage;
    private SwiftView? _detailPage;
    private bool _canNavigateBack = true;
    private readonly string _enterFullScreenLabel;
    private readonly string _exitFullScreenLabel;
    private SwiftView? _fullScreenButton;
    private SwiftView? _fullScreenSymbol;

    /// <summary>True while the path's first page fills the detail column (not a place).</summary>
    private bool _startsInDetail;

    /// <summary>The open item fills the window (the person chose Full screen).</summary>
    private bool _fullScreen;

    /// <param name="sidebar">The sidebar column (the Library's list of places).</param>
    /// <param name="placeRoutes">The routes of the places, whose pages fill the content column.</param>
    /// <param name="contentPlaceholder">What the content column shows while no place is open (the places).</param>
    /// <param name="detailPlaceholder">What the detail column shows while no item is open.</param>
    /// <param name="routesChanged">The person's Back changed the path (as a stack reports it).</param>
    /// <param name="enterFullScreenLabel">The full-screen button's name while the columns show.</param>
    /// <param name="exitFullScreenLabel">Its name while the item fills the window.</param>
    public LibrarySplitNavigation(SwiftView sidebar, IReadOnlySet<string> placeRoutes,
        SwiftView contentPlaceholder, SwiftView detailPlaceholder,
        Action<IReadOnlyList<string>> routesChanged,
        string enterFullScreenLabel, string exitFullScreenLabel)
    {
        _placeRoutes = placeRoutes;
        _enterFullScreenLabel = enterFullScreenLabel;
        _exitFullScreenLabel = exitFullScreenLabel;
        _routesChanged = routesChanged;
        _contentPlaceholder = contentPlaceholder;
        _detailPlaceholder = detailPlaceholder;
        _contentSlot = Views.VStack(contentPlaceholder).WithFlexibleWidth()
            .WithSplitColumnWidth(380, min: 300, max: 560);
        _contentSlot.Spacing = 0;
        _detailRoot = Views.VStack(detailPlaceholder).WithFlexibleWidth();
        _detailRoot.Spacing = 0;
        _detailStack = Views.NavigationStack(_detailRoot, OnDetailRoutesChanged);
        Split = Views.NavigationSplitView(sidebar, _contentSlot, _detailStack,
            OnVisibilityChanged, OnCompactColumnChanged);
        // The sidebar is a drawer over the columns, never a fixed column beside them.
        Split.WithSplitViewStyle(SwiftSplitViewStyle.Automatic);
        ShowColumns();
    }

    /// <summary>The split view node the Library tab shows.</summary>
    public SwiftView Split { get; }

    public IReadOnlyList<string> NavigationPath => _path.AsReadOnly();

    public bool CanNavigateBack
    {
        get => _canNavigateBack;
        set
        {
            _canNavigateBack = value;
            _detailStack.CanNavigateBack = value;
        }
    }

    public void Push(string route, SwiftView page)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(route);
        ArgumentNullException.ThrowIfNull(page);
        if (_path.Contains(route, StringComparer.Ordinal))
            throw new ArgumentException("A route may appear only once in a navigation path.", nameof(route));
        if (_path.Count == 0)
            _startsInDetail = !_placeRoutes.Contains(route);
        switch (DetailLevel(_path.Count))
        {
            case < 0:
                _contentPage = Swap(_contentSlot, page);
                break;
            case 0:
                _detailPage = Swap(_detailRoot, _startsInDetail ? page : WithFullScreenButton(page));
                break;
            default:
                _detailStack.Push(route, page);
                break;
        }
        _path.Add(route);
        ShowColumns();
    }

    /// <summary>
    /// The item's toolbar gets a Full screen button (beside the list only: it is left out in a
    /// compact width), which hides the sidebar and the list so the item fills the window, and
    /// brings them back.
    /// </summary>
    private SwiftView WithFullScreenButton(SwiftView page)
    {
        _fullScreenSymbol = Views.Image(FullScreenSymbol);
        _fullScreenButton = Views.Button(_fullScreenSymbol, ToggleFullScreen)
            .WithAccessibilityLabel(FullScreenLabel)
            .WithHiddenInCompactWidth()
            .WithAccessibilityIdentifier("native.library.fullscreen");
        return Views.Toolbar(page, _fullScreenButton);
    }

    private string FullScreenSymbol => _fullScreen
        ? "arrow.down.right.and.arrow.up.left" : "arrow.up.left.and.arrow.down.right";

    private string FullScreenLabel => _fullScreen ? _exitFullScreenLabel : _enterFullScreenLabel;

    private void ToggleFullScreen()
    {
        if (_detailPage is null || _startsInDetail)
            return;
        _fullScreen = !_fullScreen;
        ShowColumns();
    }

    /// <summary>The full-screen button shows the state it would change.</summary>
    private void UpdateFullScreenButton()
    {
        if (_fullScreenSymbol is { } symbol && _fullScreenButton is { } button)
        {
            symbol.SystemImageName = FullScreenSymbol;
            button.WithAccessibilityLabel(FullScreenLabel);
        }
    }

    /// <summary>
    /// The person showed the sidebar (its button) or SwiftUI changed the columns: leaving the
    /// item-only view ends full screen.
    /// </summary>
    private void OnVisibilityChanged(SwiftSplitViewVisibility visibility)
    {
        if (_fullScreen && visibility != SwiftSplitViewVisibility.DetailOnly)
        {
            _fullScreen = false;
            UpdateFullScreenButton();
        }
    }

    public void Pop()
    {
        if (_path.Count == 0)
            throw new InvalidOperationException("The navigation is at its root.");
        if (!_canNavigateBack)
            throw new InvalidOperationException("Back navigation is disabled while this operation is in progress.");
        RemoveLast();
        ShowColumns();
    }

    /// <summary>
    /// Leaves every open page, as a person going back to the Library's list would, and reports
    /// it like that Back. False (nothing changes) while Back is blocked.
    /// </summary>
    public bool PopAllAsBack()
    {
        if (_path.Count == 0)
            return true;
        if (!_canNavigateBack)
            return false;
        while (_path.Count > 0)
            RemoveLast();
        ShowColumns();
        _routesChanged(NavigationPath);
        return true;
    }

    /// <summary>
    /// New placeholders (a theme, text size or language change): shown at once in a column
    /// that has no page open; the old ones are disposed.
    /// </summary>
    public void ReplacePlaceholders(SwiftView contentPlaceholder, SwiftView detailPlaceholder)
    {
        _contentPlaceholder = ReplacePlaceholder(_contentSlot, _contentPage, _contentPlaceholder,
            contentPlaceholder);
        _detailPlaceholder = ReplacePlaceholder(_detailRoot, _detailPage, _detailPlaceholder,
            detailPlaceholder);
    }

    private static SwiftView ReplacePlaceholder(SwiftView slot, SwiftView? page,
        SwiftView previous, SwiftView next)
    {
        if (page is null && slot.Children.Count == 1)
            slot.RemoveAt(0);
        previous.Dispose();
        if (page is null)
            slot.Add(next);
        return next;
    }

    /// <summary>
    /// Where a path index is shown: below 0 the content column, 0 the detail column's root, and
    /// above 0 pushed on the detail column's stack.
    /// </summary>
    private int DetailLevel(int pathIndex) => _startsInDetail ? pathIndex : pathIndex - 1;

    private void RemoveLast()
    {
        var index = _path.Count - 1;
        switch (DetailLevel(index))
        {
            case < 0:
                Restore(_contentSlot, _contentPlaceholder, _contentPage);
                _contentPage = null;
                break;
            case 0:
                Restore(_detailRoot, _detailPlaceholder, _detailPage);
                _detailPage = null;
                _fullScreenButton = null;
                _fullScreenSymbol = null;
                _fullScreen = false;
                break;
            default:
                _detailStack.Pop();
                break;
        }
        _path.RemoveAt(index);
        if (_path.Count == 0)
            _startsInDetail = false;
    }

    /// <summary>
    /// The list and detail columns show; the sidebar is a drawer behind its toolbar button.
    /// With nothing open the content column's placeholder is the list of places itself (Captain
    /// 2026-11-12: "I expected to see it where the list appears"), so choosing one needs no
    /// drawer. Full screen, or a page that fills the detail column on its own, shows only that
    /// column. In a compact width the deepest open level is the column on top.
    /// </summary>
    private void ShowColumns()
    {
        Split.SplitColumnVisibility = _startsInDetail || (_fullScreen && _detailPage is not null)
            ? SwiftSplitViewVisibility.DetailOnly
            : SwiftSplitViewVisibility.DoubleColumn;
        UpdateFullScreenButton();
        Split.PreferredCompactColumn = _path.Count == 0 ? SwiftSplitViewColumn.Sidebar
            : DetailLevel(_path.Count - 1) < 0 ? SwiftSplitViewColumn.Content
            : SwiftSplitViewColumn.Detail;
    }

    /// <summary>The person went back on the detail column's stack (above its root page).</summary>
    private void OnDetailRoutesChanged(IReadOnlyList<string> detailRoutes)
    {
        var detailRootIndex = _startsInDetail ? 0 : 1;
        var keep = Math.Min(_path.Count, detailRootIndex + 1 + detailRoutes.Count);
        if (keep >= _path.Count)
            return;
        _path.RemoveRange(keep, _path.Count - keep);
        ShowColumns();
        _routesChanged(NavigationPath);
    }

    /// <summary>
    /// In the collapsed stack the person went back to the content column or the sidebar: the
    /// levels above it close, as a stack's Back closes them (a page that started in the detail
    /// column has no content column page, so going back from it closes it). While Back is
    /// blocked the column stays where the path is.
    /// </summary>
    private void OnCompactColumnChanged(SwiftSplitViewColumn column)
    {
        var keep = column switch
        {
            SwiftSplitViewColumn.Sidebar => 0,
            SwiftSplitViewColumn.Content => _startsInDetail ? 0 : 1,
            _ => _path.Count,
        };
        if (keep >= _path.Count)
            return;
        if (!_canNavigateBack)
        {
            ShowColumns();
            return;
        }
        while (_path.Count > keep)
            RemoveLast();
        ShowColumns();
        _routesChanged(NavigationPath);
    }

    /// <summary>Shows <paramref name="page"/> in a column slot in place of its placeholder.</summary>
    private static SwiftView Swap(SwiftView slot, SwiftView page)
    {
        if (slot.Children.Count == 1)
            slot.RemoveAt(0);
        slot.Add(page);
        return page;
    }

    /// <summary>Puts the placeholder back and disposes the page that left.</summary>
    private static void Restore(SwiftView slot, SwiftView placeholder, SwiftView? page)
    {
        if (slot.Children.Count == 1)
            slot.RemoveAt(0);
        page?.Dispose();
        slot.Add(placeholder);
    }
}
