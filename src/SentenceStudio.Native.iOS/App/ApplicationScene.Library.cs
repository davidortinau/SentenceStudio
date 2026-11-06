using System.Globalization;
using Microsoft.SwiftUI;
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
    private SwiftView? _libraryStack;
    private SwiftView? _libraryRoot;
    private SwiftView? _libraryListSlot;
    private string? _renderedLibraryList;
    private SwiftView? _pairsSlot;
    private SwiftView? _pairsToastLayer;
    private SwiftView? _pairsChildSlot;
    private SwiftView? _pairsChildToastLayer;
    private long _renderedPairsChildVersion = -1;
    private SourceUiStyle? _renderedPairsChildStyle;
    private SwiftView? _renderedPairsToastLayer;
    private long _renderedPairsToastId;
    private SourceUiStyle? _renderedPairsToastStyle;

    private SwiftView BuildLibraryStack()
    {
        _libraryListSlot = Views.VStack(LibraryList()).WithFlexibleWidth();
        _libraryListSlot.Spacing = 0;
        _renderedLibraryList = LibraryListKey();
        _libraryRoot = _libraryListSlot
            .WithNavigationTitle(_app.Localize("Nav_Library"))
            .WithInlineNavigationTitle();
        _libraryStack = Views.NavigationStack(_libraryRoot, OnLibraryRoutesChanged);
        return _libraryStack;
    }

    /// <summary>What the list shows: the counts that are already loaded, and the style.</summary>
    private string LibraryListKey() => string.Create(CultureInfo.InvariantCulture,
        $"{_app.LibraryResourceCount}|{_app.LibraryVocabularyCount}|{_style.GetHashCode()}");

    /// <summary>
    /// The symbols follow the source menu icons (bi-book, bi-card-text, bi-bullseye,
    /// bi-soundwave) and are about equally wide, so the titles and separators line up.
    /// </summary>
    private SwiftView LibraryList() =>
        _style.GroupedForm(Views.Form(
                LibraryRow(LibrarySection.Resources, "book.closed", "Library_Resources",
                    _app.LibraryResourceCount, "native.library.row.resources"),
                LibraryRow(LibrarySection.Vocabulary, "character.book.closed", "Nav_Vocabulary",
                    _app.LibraryVocabularyCount, "native.library.row.vocabulary"),
                LibraryRow(LibrarySection.Skills, "target", "Nav_Skills",
                    null, "native.library.row.skills"),
                LibraryRow(LibrarySection.Pairs, "waveform", "Nav_MinimalPairs",
                    null, "native.library.row.minimal-pairs")))
            .WithAccessibilityIdentifier("native.library.list");

    /// <summary>A list row: symbol, title, the count when it is already loaded, and a chevron.</summary>
    private SwiftView LibraryRow(LibrarySection page, string symbol, string titleKey,
        int? count, string id)
    {
        var appearance = _style.Appearance;
        var title = _app.Localize(titleKey);
        var items = new List<SwiftView>
        {
            Views.Image(symbol)
                .WithFixedSymbolSize(20)
                .WithForegroundColor(appearance.Primary.Light, appearance.Primary.Dark),
            _style.Text(Views.Text(title), SourceTextRole.Body1).WithFlexibleWidth(),
        };
        if (count is { } total)
            items.Add(_style.Text(Views.Text(total.ToString(CultureInfo.CurrentCulture)),
                SourceTextRole.Body1, SourceTextTone.Secondary));
        items.Add(Views.Image("chevron.right")
            .WithFixedSymbolSize(13)
            .WithForegroundColor(appearance.SecondaryText.Light, appearance.SecondaryText.Dark));
        // The list row itself keeps the 44-point minimum; no extra height, as in Settings.
        var row = Views.HStack(items.ToArray()).WithFlexibleWidth();
        row.Spacing = 12;
        return Views.Button(row, () => _app.OpenLibraryPage(page))
            .WithButtonStyle(SwiftButtonStyle.Plain)
            .WithFlexibleWidth()
            .WithAccessibilityLabel(count is { } shown
                ? string.Create(CultureInfo.CurrentCulture, $"{title}, {shown}") : title)
            .WithAccessibilityIdentifier(id);
    }

    private void UpdateLibrary(bool appearanceChanged)
    {
        if (_libraryListSlot is null || _libraryStack is null)
            return;
        var key = LibraryListKey();
        if (appearanceChanged || !string.Equals(key, _renderedLibraryList, StringComparison.Ordinal))
        {
            Replace(_libraryListSlot, LibraryList());
            _renderedLibraryList = key;
        }
        UpdateLibraryResources(appearanceChanged);
        UpdateLibraryPairs(appearanceChanged);
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
                .WithInlineNavigationTitle();
            _resourcesSearch = Find(_resourcesSlot, "native.resources.search");
            _libraryStack.Push(ApplicationFormRoutes.LibraryResources,
                WithSamToolbar(_libraryStack, ApplicationFormRoutes.LibraryResources, _resourcesSlot));
        }
        else if (_app.SelectedTab == NativeTabs.Library || appearanceChanged)
            UpdateResources(appearanceChanged);
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
            _resourcesSlot = null;
            _resourcesSearch = null;
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
    }

    // ---- Minimal Pairs ----

    private void UpdateLibraryPairs(bool appearanceChanged)
    {
        var stack = _libraryStack;
        if (stack is null)
            return;
        if (!_app.IsLibraryPairsOpen)
        {
            // Released without a Back (the list's Go Back): leave the page too.
            if (_pairsSlot is not null)
            {
                DropPairsReferences();
                while (stack.CanNavigateBack && stack.NavigationPath.Count > 0
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
            _pairsToastLayer = Views.VStack().WithFlexibleWidth();
            _renderedPairsToastLayer = null;
            var page = Views.Overlay(_pairsSlot, _pairsToastLayer, SwiftOverlayEdge.Top)
                .WithNavigationTitle(_app.LibraryPairsTitle)
                .WithInlineNavigationTitle();
            // A browse page: the list keeps the tab bar (its alert is wrapped around it once).
            stack.Push(ApplicationFormRoutes.MinimalPairs, WithSamToolbar(stack,
                ApplicationFormRoutes.MinimalPairs,
                _app.PresentLibraryPairsOver(page).WithTabBarHidden(false)));
        }
        else
            Replace(_pairsSlot, _app.RenderLibraryPairs(_style));

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
                    || !ReferenceEquals(_renderedPairsChildStyle, _style))
                {
                    Replace(_pairsChildSlot, _app.RenderLibraryPairsChild(_style));
                    RecordPairsChild();
                }
                _pairsChildSlot.WithNavigationTitle(_app.LibraryPairsChildTitle);
            }
        }
        else if (_pairsChildSlot is not null)
        {
            // Clear first: the route callback must not report this pop as a person's Back.
            _pairsChildSlot = null;
            ForgetPairsChildToastLayer();
            if (stack.CanNavigateBack && stack.NavigationPath.Count > 1)
                stack.Pop();
        }
        UpdatePairsToast();
    }

    private void RecordPairsChild()
    {
        _renderedPairsChildVersion = _app.LibraryPairsChildVersion;
        _renderedPairsChildStyle = _style;
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
        _renderedPairsChildVersion = -1;
        _renderedPairsChildStyle = null;
    }
}
