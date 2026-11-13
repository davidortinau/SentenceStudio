using System.Globalization;
using Microsoft.SwiftUI;
using SentenceStudio.Native.iOS.Features.Browse;
using SentenceStudio.Native.iOS.Platform;
using SentenceStudio.Native.iOS.Styling;
using Views = Microsoft.SwiftUI.SwiftUI;

namespace SentenceStudio.Native.iOS.App;

/// <summary>
/// The Library tab (the Apple Music Library pattern, Captain 2026-11-05): a short native
/// list (Resources, Vocabulary, Skills, Minimal Pairs). Each row pushes its page on the
/// Library stack with its own title and toolbar; the page's editor is pushed above it.
/// The tab bar stays on the list and the pages, and hides in the editors.
/// </summary>
internal sealed partial class ApplicationScene
{
    private ISceneNavigation? _libraryStack;
    private LibrarySplitNavigation? _librarySplit;
    private SwiftView? _libraryRoot;
    private SwiftView? _libraryListSlot;
    private string? _renderedLibraryList;
    private SourceUiStyle? _placeholderStyle;
    private SwiftView? _pairsSlot;
    private SwiftView? _pairsToastLayer;
    private SwiftView? _pairsChildSlot;
    private SwiftView? _pairsChildToastLayer;
    private long _renderedPairsVersion = -1;
    private SourceUiStyle? _renderedPairsStyle;
    private double _renderedPairsWidth;
    private long _renderedPairsChildVersion = -1;
    private SourceUiStyle? _renderedPairsChildStyle;
    private double _renderedPairsChildWidth;
    private long _renderedLibraryResetVersion;
    private SwiftView? _renderedPairsToastLayer;
    private long _renderedPairsToastId;
    private SourceUiStyle? _renderedPairsToastStyle;

    private SwiftView BuildLibraryStack()
    {
        _libraryListSlot = Views.VStack(LibraryList()).WithFlexibleWidth();
        _libraryListSlot.Spacing = 0;
        _renderedLibraryList = LibraryListKey();
        _placeholderStyle = _style;
        _renderedLibraryResetVersion = _app.LibraryResetVersion;
        _libraryRoot = _libraryListSlot
            .WithNavigationTitle(_app.Localize("Nav_Library"))
            .WithInlineNavigationTitle();
        // The Library as a split view (Captain 2026-11-12): its four places in the sidebar,
        // the open place's list in the middle and the open item on the right. A compact width
        // (a slab iPhone, the folded iPhone Duo) collapses it to one column at a time.
        _librarySplit = new LibrarySplitNavigation(_libraryRoot, LibraryPlaceRoutes,
            ContentPlaceholder(), DetailPlaceholder(), OnLibraryRoutesChanged,
            _app.Localize("Library_FullScreen"), _app.Localize("Library_ExitFullScreen"));
        _libraryStack = _librarySplit;
        return _librarySplit.Split.WithAccessibilityIdentifier("native.library.split");
    }

    /// <summary>The places' pages, which fill the split view's content column.</summary>
    private static readonly IReadOnlySet<string> LibraryPlaceRoutes = new HashSet<string>(
        [ApplicationFormRoutes.LibraryResources, ApplicationFormRoutes.Vocabulary,
            ApplicationFormRoutes.Skills, ApplicationFormRoutes.MinimalPairs],
        StringComparer.Ordinal);

    /// <summary>
    /// The content column while no place is open, beside the pinned sidebar that lists the
    /// places (the places are not repeated here).
    /// </summary>
    private SwiftView ContentPlaceholder() =>
        ColumnPlaceholder("Library_ChooseSection", "native.library.content.placeholder");

    private SwiftView DetailPlaceholder() =>
        ColumnPlaceholder("Library_ChooseItem", "native.library.detail.placeholder");

    /// <summary>What an empty split view column says (centered, secondary text).</summary>
    private SwiftView ColumnPlaceholder(string key, string id)
    {
        var appearance = _style.Appearance;
        var placeholder = Views.VStack(
                _style.Text(Views.Text(_app.Localize(key)), SourceTextRole.Body1, SourceTextTone.Secondary)
                    .WithFlexibleWidth(alignment: SwiftHorizontalAlignment.Center))
            .WithFlexibleWidth(alignment: SwiftHorizontalAlignment.Center)
            .WithFlexibleHeight()
            .WithBackgroundColor(appearance.BodyBackground.Light, appearance.BodyBackground.Dark)
            .WithAccessibilityIdentifier(id);
        return placeholder;
    }

    /// <summary>The place the Library has open, for the sidebar's selection.</summary>
    private LibrarySection? OpenLibrarySection =>
        _app.IsLibraryResourcesOpen ? LibrarySection.Resources
        : _app.IsVocabularyOpen ? LibrarySection.Vocabulary
        : _app.IsSkillsOpen ? LibrarySection.Skills
        : _app.IsLibraryPairsOpen ? LibrarySection.Pairs
        : null;

    /// <summary>
    /// A sidebar row: in the split view the sidebar stays in view, so choosing another place
    /// first leaves the open one (as Back would; nothing happens while Back is blocked).
    /// </summary>
    private void SelectLibrarySection(LibrarySection page)
    {
        if (OpenLibrarySection == page)
            return;
        if (_librarySplit is { } split && split.NavigationPath.Count > 0 && !split.PopAllAsBack())
            return;
        _app.OpenLibraryPage(page);
    }

    /// <summary>
    /// What the list shows: the count that is already loaded, a row's error, the style, and
    /// the quick-add grid's column count (its buttons are fixed).
    /// </summary>
    private string LibraryListKey() => string.Create(CultureInfo.InvariantCulture,
        $"{_app.LibraryResourceCount}|{_app.LibraryVocabularyCount}|{_app.LibraryErrorText}|{_style.GetHashCode()}|{QuickAddColumns(_app.LibraryQuickAddItems.Count)}|{OpenLibrarySection}");


    private const double QuickAddTileHeight = 84;

    /// <summary>Two columns: the Library list is the split view's sidebar, a narrow column (as on iPhone).</summary>
    private static int QuickAddColumns(int count) => 2;

    /// <summary>
    /// The symbols follow the source menu icons (bi-book, bi-card-text, bi-bullseye,
    /// bi-soundwave) and are about equally wide, so the titles and separators line up.
    /// Resources and Vocabulary show a count once their lists have been read (no extra query).
    /// </summary>
    private SwiftView LibraryList()
    {
        var rows = new List<SwiftView>
        {
            LibraryRow(LibrarySection.Resources, "book.closed", "Library_Resources",
                _app.LibraryResourceCount, "native.library.row.resources"),
            LibraryRow(LibrarySection.Vocabulary, "character.book.closed", "Nav_Vocabulary",
                _app.LibraryVocabularyCount, "native.library.row.vocabulary"),
            LibraryRow(LibrarySection.Skills, "target", "Nav_Skills",
                null, "native.library.row.skills"),
            LibraryRow(LibrarySection.Pairs, "waveform", "Nav_MinimalPairs",
                null, "native.library.row.minimal-pairs"),
        };
        if (_app.LibraryErrorText is { } error)
            rows.Add(_style.Text(Views.Text(error), SourceTextRole.Body2, SourceTextTone.Danger)
                .WithAccessibilityIdentifier("native.library.error"));
        if (QuickAddGrid() is { } quickAdd)
            rows.Add(quickAdd);
        return _style.GroupedForm(Views.Form(rows.ToArray()))
            .WithAccessibilityIdentifier("native.library.list");
    }


    /// <summary>
    /// The quick-add grid below the rows (Captain 2026-11-06): one icon-and-label button per
    /// create flow (<see cref="ApplicationController.LibraryQuickAddItems"/>).
    /// </summary>
    private SwiftView? QuickAddGrid()
    {
        var items = _app.LibraryQuickAddItems;
        if (items.Count == 0)
            return null;
        var appearance = _style.Appearance;
        var buttons = items.Select(item =>
        {
            var title = _app.Localize(item.TitleKey);
            var content = Views.VStack(
                    Views.Image(item.Symbol)
                        .WithFixedSymbolSize(22)
                        .WithForegroundColor(appearance.Primary.Light, appearance.Primary.Dark)
                        .WithFlexibleWidth(alignment: SwiftHorizontalAlignment.Center),
                    _style.Text(Views.Text(title), SourceTextRole.Caption1)
                        .WithFlexibleWidth(alignment: SwiftHorizontalAlignment.Center))
                .WithFlexibleWidth(alignment: SwiftHorizontalAlignment.Center)
                .WithPadding(10)
                // One tile height whatever the symbol, so the grid rows line up.
                .WithMinimumHeight(QuickAddTileHeight);
            content.Spacing = 6;
            // Plain: each button in the shared list row takes only its own taps.
            var button = Views.Button(content, () => OpenLibraryQuickAdd(item))
                .WithButtonStyle(SwiftButtonStyle.Plain)
                .WithAccessibilityLabel(title)
                .WithAccessibilityIdentifier($"native.library.quickadd.{item.Id}");
            return _style.BorderedCard(button, radius: appearance.BootstrapRadius);
        }).ToArray();
        var grid = Views.LazyVGrid(QuickAddColumns(items.Count), buttons)
            .WithAccessibilityIdentifier("native.library.quickadd");
        grid.Spacing = SourceUiLayout.ItemGap;
        return Views.Section(_app.Localize("Library_QuickAdd"), grid);
    }

    /// <summary>
    /// A quick-add tile: the sidebar can show beside an open page, so the open page closes
    /// first (as Back would; nothing happens while Back is blocked), then the create flow opens.
    /// </summary>
    private void OpenLibraryQuickAdd(ApplicationController.LibraryQuickAddItem item)
    {
        if (_librarySplit is { } split && split.NavigationPath.Count > 0 && !split.PopAllAsBack())
            return;
        item.Open();
    }

    /// <summary>
    /// A list row: symbol, title, the count when it is already loaded, and a chevron. The open
    /// place's row is marked in the sidebar: its title and symbol in the accent color, bold,
    /// and a checkmark in place of the chevron (not color alone), and selected for VoiceOver.
    /// </summary>
    private SwiftView LibraryRow(LibrarySection page, string symbol, string titleKey,
        int? count, string id)
    {
        var appearance = _style.Appearance;
        var title = _app.Localize(titleKey);
        var selected = OpenLibrarySection == page;
        var items = new List<SwiftView>
        {
            Views.Image(symbol)
                .WithFixedSymbolSize(20)
                .WithForegroundColor(appearance.Primary.Light, appearance.Primary.Dark),
            _style.Text(Views.Text(title), SourceTextRole.Body1,
                selected ? SourceTextTone.Primary : SourceTextTone.Body,
                strongElement: selected).WithFlexibleWidth(),
        };
        if (count is { } total)
            items.Add(_style.Text(Views.Text(total.ToString(CultureInfo.CurrentCulture)),
                SourceTextRole.Body1, SourceTextTone.Secondary));
        items.Add(selected
            ? Views.Image("checkmark")
                .WithFixedSymbolSize(15)
                .WithForegroundColor(appearance.Primary.Light, appearance.Primary.Dark)
            : Views.Image("chevron.right")
                .WithFixedSymbolSize(13)
                .WithForegroundColor(appearance.SecondaryText.Light, appearance.SecondaryText.Dark));
        // The list row itself keeps the 44-point minimum; no extra height, as in Settings.
        var row = Views.HStack(items.ToArray()).WithFlexibleWidth();
        row.Spacing = 12;
        return Views.Button(row, () => SelectLibrarySection(page))
            .WithButtonStyle(SwiftButtonStyle.Plain)
            .WithFlexibleWidth()
            .WithAccessibilitySelected(selected)
            .WithAccessibilityLabel(count is { } shown
                ? string.Create(CultureInfo.CurrentCulture, $"{title}, {shown}") : title)
            .WithAccessibilityIdentifier(id);
    }

    private void UpdateLibrary(bool appearanceChanged)
    {
        if (_libraryListSlot is null || _libraryStack is null)
            return;
        if (_renderedLibraryResetVersion != _app.LibraryResetVersion)
        {
            // A Study entry closed the Library's pages: back to the list. The controller
            // already released them, so no route callback reports these pops.
            if (_libraryStack.NavigationPath.Count > 0 && !_libraryStack.CanNavigateBack)
                return;
            while (_libraryStack.NavigationPath.Count > 0)
                _libraryStack.Pop();
            DropResourcesReferences();
            DropFormReferences();
            DropVocabularyReferences();
            DropSkillsReferences();
            DropPairsReferences();
            DropImportReferences();
            DropYouTubeReferences();
            _renderedLibraryResetVersion = _app.LibraryResetVersion;
        }
        var key = LibraryListKey();
        if (appearanceChanged || !string.Equals(key, _renderedLibraryList, StringComparison.Ordinal))
        {
            Replace(_libraryListSlot, LibraryList());
            _renderedLibraryList = key;
        }
        // The empty columns' texts follow the theme and the text size (a new style; a text size
        // change is not an appearance change here).
        if (appearanceChanged || !ReferenceEquals(_placeholderStyle, _style))
        {
            _librarySplit?.ReplacePlaceholders(ContentPlaceholder(), DetailPlaceholder());
            _placeholderStyle = _style;
        }
        UpdateLibraryResources(appearanceChanged);
        UpdateLibraryPairs(appearanceChanged);
        UpdateLibraryImport(appearanceChanged);
        UpdateLibraryYouTube(appearanceChanged);
    }

    /// <summary>The Resources page, pushed by its list row.</summary>
    private void UpdateLibraryResources(bool appearanceChanged)
    {
        if (_libraryStack is null || !_app.IsLibraryResourcesOpen)
            return;
        if (_resourcesSlot is null)
        {
            if (_libraryStack.NavigationPath.Count != 0)
                return;
            _resourcesSlot = Views.VStack(_app.RenderResources(_style))
                .WithNavigationTitle(_app.Localize("Resources_Title"))
                .WithNavigationSubtitle(_app.ResourcesSubtitle)
                .WithInlineNavigationTitle();
            _resourcesSearch = Find(_resourcesSlot, BrowserPageUpdate.Resources.SearchId);
            _renderedResourcesArrangement = _app.ResourcesArrangement;
            _renderedResourcesKey = _app.ResourcesRenderKey;
            // As on Vocabulary: a phone's filters and sort in a sheet over the page.
            _resourcesFilterSlot = Views.VStack(_app.RenderResourceFilters(_style));
            _renderedResourceSheetKey = _app.ResourceFilterSheetKey;
            _resourcesSheet = Views.Sheet(_resourcesSlot, _resourcesFilterSlot,
                    _app.CancelResourceFilters)
                .WithSheetDetentFraction(0.85)
                .WithAccessibilityIdentifier("native.resources.filters.host");
            _libraryStack.Push(ApplicationFormRoutes.LibraryResources, _resourcesSheet);
        }
        // Drawn again only when what it shows changed, so an unrelated publish leaves an open
        // filter menu (and the results' build) alone.
        else if (appearanceChanged || _app.SelectedTab == NativeTabs.Library
            && !Equals(_renderedResourcesKey, _app.ResourcesRenderKey))
            UpdateResources(appearanceChanged);
        UpdateResourceFilters(appearanceChanged);
    }

    /// <summary>System Back (or a swipe) changed the Library path: drop what left it.</summary>
    private void OnLibraryRoutesChanged(IReadOnlyList<string> routes)
    {
        if (_formSlot is not null && !routes.Contains(ApplicationFormRoutes.ResourceForm))
        {
            if (_app.IsResourcesFormOpen)
                _app.OnResourcesFormPopped();
            DropFormReferences();
            if (_app.IsResourcesFormOpen)
                UpdateResourceForm();
        }
        if (_resourcesSlot is not null && !routes.Contains(ApplicationFormRoutes.LibraryResources))
        {
            DropResourcesReferences();
            _app.OnLibraryResourcesPopped();
        }
        if (_vocabularySlot is not null || _lookupsSlot is not null)
            OnVocabularyRoutesChanged(routes.Count);
        if (_skillsSlot is not null)
            OnSkillsRoutesChanged(routes.Count);
        if (_pairsSlot is not null && !routes.Contains(ApplicationFormRoutes.MinimalPairs))
        {
            DropPairsReferences();
            _app.OnLibraryPairsPopped();
        }
        else if (_pairsChildSlot is not null
            && !routes.Contains(ApplicationFormRoutes.MinimalPairsChild))
        {
            _pairsChildSlot = null;
            ForgetPairsChildToastLayer();
            _app.OnLibraryPairsChildPopped();
        }
        OnImportRoutesChanged(routes);
        OnYouTubeRoutesChanged(routes);
    }

    // ---- Minimal Pairs ----

    private void UpdateLibraryPairs(bool appearanceChanged)
    {
        var stack = _libraryStack;
        if (stack is null)
            return;
        if (!_app.IsLibraryPairsOpen)
        {
            // Released without a Back (the list's Go Back): leave the page too. While Back
            // is blocked the references stay, so the next update pops the page.
            if (_pairsSlot is not null)
            {
                if (stack.NavigationPath.Contains(ApplicationFormRoutes.MinimalPairs)
                    && !stack.CanNavigateBack)
                    return;
                DropPairsReferences();
                while (stack.NavigationPath.Count > 0
                    && (stack.NavigationPath[^1] == ApplicationFormRoutes.MinimalPairsChild
                        || stack.NavigationPath[^1] == ApplicationFormRoutes.MinimalPairs))
                    stack.Pop();
            }
            return;
        }
        if (_pairsSlot is null)
        {
            if (stack.NavigationPath.Count != 0)
                return;
            _pairsSlot = Views.VStack(_app.RenderLibraryPairs(_style)).WithFlexibleWidth();
            RecordPairs();
            _pairsToastLayer = Views.VStack().WithFlexibleWidth();
            _renderedPairsToastLayer = null;
            var page = Views.Overlay(_pairsSlot, _pairsToastLayer, SwiftOverlayEdge.Top)
                .WithNavigationTitle(_app.LibraryPairsTitle)
                .WithInlineNavigationTitle();
            // A browse page: the list keeps the tab bar (its alert is wrapped around it once).
            stack.Push(ApplicationFormRoutes.MinimalPairs,
                _app.PresentLibraryPairsOver(page).WithTabBarHidden(false));
        }
        else
        {
            // Only a changed list re-renders the list page (not the create page or the
            // session above it); the same shape swaps its content in place, so the scroll
            // position stays. A new appearance, style or width re-renders it whole.
            var reshaped = appearanceChanged || !ReferenceEquals(_renderedPairsStyle, _style)
                || Math.Abs(_renderedPairsWidth - _style.ViewportWidth) >= 0.5;
            if (reshaped || _renderedPairsVersion != _app.LibraryPairsVersion)
            {
                var next = _app.RenderLibraryPairs(_style);
                if (reshaped || !TryReplaceScrollContent(_pairsSlot, next))
                    Replace(_pairsSlot, next);
                RecordPairs();
            }
        }

        if (_app.LibraryPairsHasChildPage)
        {
            if (_pairsChildSlot is null && stack.NavigationPath.Count == 1)
            {
                // The create page and the listening session: Save/Cancel or the session decide how you leave.
                _pairsChildSlot = Views.VStack(_app.RenderLibraryPairsChild(_style))
                    .WithNavigationTitle(_app.LibraryPairsChildTitle)
                    .WithInlineNavigationTitle()
                    .WithTabBarHidden();
                RecordPairsChild();
                _pairsChildToastLayer = Views.VStack().WithFlexibleWidth();
                stack.Push(ApplicationFormRoutes.MinimalPairsChild, WithSamToolbar(stack,
                    ApplicationFormRoutes.MinimalPairsChild,
                    Views.Overlay(_pairsChildSlot, _pairsChildToastLayer, SwiftOverlayEdge.Top)));
            }
            else if (_pairsChildSlot is not null)
            {
                if (appearanceChanged || _renderedPairsChildVersion != _app.LibraryPairsChildVersion
                    || !ReferenceEquals(_renderedPairsChildStyle, _style)
                    || Math.Abs(_renderedPairsChildWidth - _style.ViewportWidth) >= 0.5)
                {
                    Replace(_pairsChildSlot, _app.RenderLibraryPairsChild(_style));
                    RecordPairsChild();
                }
                _pairsChildSlot.WithNavigationTitle(_app.LibraryPairsChildTitle);
            }
        }
        else if (_pairsChildSlot is not null)
        {
            var onStack = stack.NavigationPath.Count > 1
                && stack.NavigationPath[^1] == ApplicationFormRoutes.MinimalPairsChild;
            // While Back is blocked the reference stays, so the next update pops the page.
            if (!onStack || stack.CanNavigateBack)
            {
                // Clear first: the route callback must not report this pop as a person's Back.
                _pairsChildSlot = null;
                ForgetPairsChildToastLayer();
                if (onStack)
                    stack.Pop();
            }
        }
        UpdatePairsToast();
    }

    private void RecordPairs()
    {
        _renderedPairsVersion = _app.LibraryPairsVersion;
        _renderedPairsStyle = _style;
        _renderedPairsWidth = _style.ViewportWidth;
    }

    private void RecordPairsChild()
    {
        _renderedPairsChildVersion = _app.LibraryPairsChildVersion;
        _renderedPairsChildStyle = _style;
        _renderedPairsChildWidth = _style.ViewportWidth;
    }

    /// <summary>
    /// Swaps a mounted page's scroll content for the next render's (same scroll and content
    /// ids), so the scroll position stays. False when the shapes differ.
    /// </summary>
    private static bool TryReplaceScrollContent(SwiftView slot, SwiftView next)
    {
        if (slot.Children.Count != 1 || slot.Children[0] is not { Kind: "scroll" } scroll
            || next.Kind != "scroll" || scroll.Children.Count != 1 || next.Children.Count != 1
            || scroll.AccessibilityIdentifier != next.AccessibilityIdentifier
            || scroll.Children[0].AccessibilityIdentifier is not { Length: > 0 } contentId
            || next.Children[0].AccessibilityIdentifier != contentId)
            return false;
        var content = next.Children[0];
        next.RemoveAt(0);
        next.Dispose();
        ReplaceContainerChildren(scroll.Children[0], content);
        return true;
    }

    /// <summary>The toast follows the page on top (as on the activity page), posted once per toast.</summary>
    private void UpdatePairsToast()
    {
        var layer = _pairsChildToastLayer ?? _pairsToastLayer;
        if (layer is null)
            return;
        var toast = _app.LibraryPairsToast;
        var id = toast?.Id ?? 0;
        if (ReferenceEquals(layer, _renderedPairsToastLayer) && id == _renderedPairsToastId
            && (toast is null || ReferenceEquals(_renderedPairsToastStyle, _style)))
            return;
        if (_renderedPairsToastLayer is { } previous && !ReferenceEquals(previous, layer))
            ClearToastLayer(previous);
        ClearToastLayer(layer);
        _renderedPairsToastLayer = layer;
        _renderedPairsToastId = id;
        _renderedPairsToastStyle = _style;
        if (toast is null)
            return;
        var gutter = SourceUiLayout.PageGutter(_style.ViewportWidth);
        layer.Add(Views.VStack(_style.MessageToast(toast, _app.LocalizedText, _app.DismissLibraryPairsToast))
            .WithFlexibleWidth()
            .WithPadding(8, gutter, 0, gutter));
    }

    private void ForgetPairsChildToastLayer()
    {
        if (ReferenceEquals(_renderedPairsToastLayer, _pairsChildToastLayer))
            _renderedPairsToastLayer = null;
        _pairsChildToastLayer = null;
    }

    private void DropPairsReferences()
    {
        _pairsSlot = null;
        _pairsToastLayer = null;
        _pairsChildSlot = null;
        _pairsChildToastLayer = null;
        _renderedPairsToastLayer = null;
        _renderedPairsToastId = 0;
        _renderedPairsVersion = -1;
        _renderedPairsStyle = null;
        _renderedPairsChildVersion = -1;
        _renderedPairsChildStyle = null;
    }
}
