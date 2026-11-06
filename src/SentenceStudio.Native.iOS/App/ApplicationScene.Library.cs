using Microsoft.SwiftUI;
using SentenceStudio.Native.iOS.Styling;
using Views = Microsoft.SwiftUI.SwiftUI;

namespace SentenceStudio.Native.iOS.App;

/// <summary>
/// The Library tab: a switcher (Resources, Vocabulary, Skills, Pairs) over one page at a
/// time, all on one NavigationStack. Each page's editor or second page is pushed one level
/// above the Library root, so system Back returns to the switcher.
/// </summary>
internal sealed partial class ApplicationScene
{
    private SwiftView? _libraryStack;
    private SwiftView? _libraryRoot;
    private SwiftView? _librarySwitcherSlot;
    private SwiftView? _libraryPageSlot;
    private LibrarySection? _renderedLibrarySection;
    private SwiftView? _pairsSlot;
    private SwiftView? _pairsToastLayer;
    private SwiftView? _pairsChildSlot;
    private SwiftView? _pairsChildToastLayer;
    private long _renderedPairsChildVersion = -1;
    private SourceUiStyle? _renderedPairsChildStyle;
    private SwiftView? _renderedPairsToastLayer;
    private long _renderedPairsToastId;
    private SourceUiStyle? _renderedPairsToastStyle;

    internal static readonly (LibrarySection Section, string TitleKey, string Id)[] LibrarySections =
    [
        (LibrarySection.Resources, "Library_Resources", "native.library.segment.resources"),
        (LibrarySection.Vocabulary, "Nav_Vocabulary", "native.library.segment.vocabulary"),
        (LibrarySection.Skills, "Nav_Skills", "native.library.segment.skills"),
        (LibrarySection.Pairs, "Library_Pairs", "native.library.segment.pairs"),
    ];

    private SwiftView BuildLibraryStack()
    {
        _librarySwitcherSlot = Views.VStack(LibrarySwitcher(_app.SelectedLibrarySection));
        _librarySwitcherSlot.Spacing = 0;
        _libraryPageSlot = Views.VStack(LibraryLoading()).WithFlexibleWidth();
        _libraryPageSlot.Spacing = 0;
        _renderedLibrarySection = null;
        _libraryRoot = _style.Page(Views.VStack(_librarySwitcherSlot, _libraryPageSlot)
                .WithFlexibleWidth())
            .WithNavigationTitle(_app.Localize("Nav_Library"))
            .WithInlineNavigationTitle()
            .WithAccessibilityIdentifier("native.library.root");
        _libraryRoot.Spacing = 0;
        _libraryStack = Views.NavigationStack(_libraryRoot,
            routes => OnLibraryRoutesChanged(routes.Count));
        return _libraryStack;
    }

    /// <summary>The switcher row, styled like Study's Today / Choose My Own switch.</summary>
    private SwiftView LibrarySwitcher(LibrarySection selected)
    {
        var buttons = new SwiftView[LibrarySections.Length];
        for (var index = 0; index < buttons.Length; index++)
        {
            var (section, key, id) = LibrarySections[index];
            var position = index == 0 ? SourceButtonGroupPosition.First
                : index == buttons.Length - 1 ? SourceButtonGroupPosition.Last
                : SourceButtonGroupPosition.Middle;
            var label = _app.Localize(key);
            buttons[index] = _style.SegmentButton(label, section == selected,
                    () => _app.SelectLibrarySection(section), position)
                .WithAccessibilityLabel(label)
                .WithAccessibilitySelected(section == selected)
                .WithAccessibilityIdentifier(id);
        }
        var group = Views.ButtonGroup(buttons, selectedIndex: (int)selected)
            .WithButtonGroupLayout(_style.Appearance.BootstrapBaseButtonBorderWidth,
                fillAvailableWidth: true)
            .WithFlexibleWidth()
            .WithAccessibilityIdentifier("native.library.switcher");
        var gutter = SourceUiLayout.PageGutter(_style.ViewportWidth);
        return Views.VStack(group).WithFlexibleWidth().WithPadding(8, gutter, 8, gutter);
    }

    private SwiftView LibraryLoading() =>
        Views.VStack(Views.IndeterminateProgress(_app.Localize("Common_Loading"))
                .WithFlexibleWidth(alignment: SwiftHorizontalAlignment.Center)
                .WithPadding(24))
            .WithFlexibleWidth()
            .WithAccessibilityIdentifier("native.library.loading");

    private void UpdateLibrary(bool appearanceChanged)
    {
        if (_libraryPageSlot is null || _librarySwitcherSlot is null)
            return;
        var section = _app.SelectedLibrarySection;
        if (_renderedLibrarySection != section || appearanceChanged)
            Replace(_librarySwitcherSlot, LibrarySwitcher(section));
        if (_renderedLibrarySection != section)
        {
            // The controller closed the previous page; its nodes leave with the slot.
            DropLibraryPageReferences();
            if (section == LibrarySection.Resources)
            {
                _resourcesSlot = Views.VStack(_app.RenderResources(_style)).WithFlexibleWidth();
                _resourcesSearch = Find(_resourcesSlot, "native.resources.search");
                Replace(_libraryPageSlot, _resourcesSlot);
            }
            else
                Replace(_libraryPageSlot, LibraryLoading());
            _renderedLibrarySection = section;
        }
        else if (section == LibrarySection.Resources && _resourcesSlot is not null
            && (_app.SelectedTab == NativeTabs.Library || appearanceChanged))
            UpdateResources(appearanceChanged);
        UpdateLibraryPairs(appearanceChanged);
    }

    /// <summary>A Library page's main view, mounted once its coordinator is open.</summary>
    private void MountLibraryPage(SwiftView page)
    {
        if (_libraryPageSlot is not null)
            Replace(_libraryPageSlot, page);
    }

    private void DropLibraryPageReferences()
    {
        _resourcesSlot = null;
        _resourcesSearch = null;
        DropVocabularyReferences();
        DropSkillsReferences();
        DropPairsReferences();
    }

    /// <summary>
    /// Pops the page above the Library root once its own Save, Cancel or Delete closed it.
    /// A pop refused while navigation is blocked keeps the references, so the next update
    /// tries again.
    /// </summary>
    /// <remarks>
    /// Known issue (iOS 26.5 simulator, 2026-11-05): after this programmatic pop to the
    /// root, the Liquid Glass bar can keep a Back chevron that does nothing (it is not in
    /// the accessibility tree) until the next push and Back. A system Back does not show it,
    /// and a plain SwiftUI probe with the same stack does not reproduce it.
    /// </remarks>
    private void PopLibraryPage(Action dropReferences)
    {
        var stack = _libraryStack;
        if (stack is null || stack.NavigationPath.Count == 0 || !stack.CanNavigateBack)
            return;
        // Clear first: the route callback must not report this pop as a person's Back.
        dropReferences();
        stack.Pop();
    }

    /// <summary>Only one page is ever pushed over the Library root.</summary>
    private void OnLibraryRoutesChanged(int count)
    {
        if (count != 0)
            return;
        if (_formSlot is not null || _app.IsResourcesFormOpen)
        {
            if (_app.IsResourcesFormOpen)
                _app.OnResourcesFormPopped();
            DropFormReferences();
            if (_app.IsResourcesFormOpen)
                UpdateResourceForm();
            return;
        }
        if (_vocabularyWordSlot is not null || _lookupsSlot is not null)
        {
            OnVocabularyRoutesChanged(count);
            return;
        }
        if (_skillFormSlot is not null)
        {
            OnSkillsRoutesChanged(count);
            return;
        }
        if (_pairsChildSlot is not null)
        {
            _pairsChildSlot = null;
            ForgetPairsChildToastLayer();
            _app.OnLibraryPairsChildPopped();
        }
    }

    // ---- Minimal Pairs ----

    private void UpdateLibraryPairs(bool appearanceChanged)
    {
        if (_libraryStack is null || _libraryPageSlot is null)
            return;
        if (!_app.IsLibraryPairsOpen || _app.SelectedLibrarySection != LibrarySection.Pairs)
        {
            if (_pairsSlot is not null)
                DropPairsReferences();
            return;
        }
        if (_pairsSlot is null)
        {
            _pairsSlot = Views.VStack(_app.RenderLibraryPairs(_style)).WithFlexibleWidth();
            _pairsToastLayer = Views.VStack().WithFlexibleWidth();
            _renderedPairsToastLayer = null;
            var page = Views.Overlay(_pairsSlot, _pairsToastLayer, SwiftOverlayEdge.Top);
            // A browse page: the list keeps the tab bar (its alert is wrapped around it once).
            MountLibraryPage(_app.PresentLibraryPairsOver(page).WithTabBarHidden(false));
        }
        else
            Replace(_pairsSlot, _app.RenderLibraryPairs(_style));

        if (_app.LibraryPairsHasChildPage)
        {
            if (_pairsChildSlot is null && _libraryStack.NavigationPath.Count == 0)
            {
                // The create page and the listening session: Save/Cancel or the session decide how you leave.
                _pairsChildSlot = Views.VStack(_app.RenderLibraryPairsChild(_style))
                    .WithNavigationTitle(_app.LibraryPairsChildTitle)
                    .WithInlineNavigationTitle()
                    .WithTabBarHidden();
                RecordPairsChild();
                _pairsChildToastLayer = Views.VStack().WithFlexibleWidth();
                _libraryStack.Push("pairs-child", WithSamToolbar(_libraryStack, "pairs-child",
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
            PopLibraryPage(() =>
            {
                _pairsChildSlot = null;
                ForgetPairsChildToastLayer();
            });
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
