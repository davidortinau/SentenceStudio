using Microsoft.SwiftUI;
using SentenceStudio.Native.iOS.Features.Import;
using SentenceStudio.Native.iOS.Styling;
using Views = Microsoft.SwiftUI.SwiftUI;

namespace SentenceStudio.Native.iOS.App;

/// <summary>
/// The Library's import pages, pushed on the Library stack above the list (browse pages:
/// the tab bar stays). A redraw swaps the page's scroll content in place, so the scroll
/// position stays while the wizard grows below; the messages are pinned over the page.
/// </summary>
internal sealed partial class ApplicationScene
{
    private SwiftView? _importContentSlot;
    private SwiftView? _importContentOverlay;
    private long _renderedImportContentVersion = -1;
    private SourceUiStyle? _renderedImportContentStyle;
    private double _renderedImportContentWidth;
    private string? _renderedImportOverlayKey;
    private SourceUiStyle? _renderedImportOverlayStyle;
    private SwiftView? _importWordSlot;
    private SwiftView? _importWordContent;
    private SwiftView? _importWordDeleteConfirmation;
    private string? _importWordDeleteMessage;
    private long _renderedImportWordVersion = -1;

    private void UpdateLibraryImport(bool appearanceChanged)
    {
        var stack = _libraryStack;
        if (stack is null)
            return;
        const string route = ApplicationImportRoutes.ImportContent;
        if (!_app.IsImportContentOpen)
        {
            // Released without a Back (sign-out, a Study entry, a refused open): leave the page.
            if (_importContentSlot is not null)
            {
                if (stack.NavigationPath.Contains(route) && !stack.CanNavigateBack)
                    return;
                DropImportReferences();
                while (stack.NavigationPath.Count > 0 && stack.NavigationPath.Contains(route))
                    stack.Pop();
            }
            return;
        }
        if (_importContentSlot is null)
        {
            if (stack.NavigationPath.Count != 0)
                return;
            _importContentSlot = Views.VStack(_app.RenderImportContent(_style)).WithFlexibleWidth();
            RecordImportContent();
            _importContentOverlay = Views.VStack().WithFlexibleWidth();
            _renderedImportOverlayKey = null;
            var page = Views.Overlay(_importContentSlot, _importContentOverlay, SwiftOverlayEdge.Top)
                .WithNavigationTitle(_app.ImportContentTitle)
                .WithInlineNavigationTitle();
            // A browse page with the tab bar: Sam's tab slot is its Sam entry (no toolbar button).
            stack.Push(ApplicationImportRoutes.ImportContent, page.WithTabBarHidden(false));
        }
        else
        {
            var reshaped = appearanceChanged || !ReferenceEquals(_renderedImportContentStyle, _style)
                || Math.Abs(_renderedImportContentWidth - _style.ViewportWidth) >= 0.5;
            if (reshaped || _renderedImportContentVersion != _app.ImportContentVersion)
            {
                var next = _app.RenderImportContent(_style);
                if (reshaped || !TrySwapImportScroll(_importContentSlot, next))
                    Replace(_importContentSlot, next);
                RecordImportContent();
            }
        }
        UpdateImportOverlay();
        UpdateImportWord(stack, appearanceChanged);
    }

    /// <summary>
    /// A result row's word editor, pushed above the results (an editor: the tab bar hides;
    /// Save, Cancel or Back closes it and the results show again).
    /// </summary>
    private void UpdateImportWord(SwiftView stack, bool appearanceChanged)
    {
        const string route = ApplicationImportRoutes.ImportWord;
        if (_app.IsImportWordOpen)
        {
            if (_importWordSlot is null)
            {
                if (stack.NavigationPath.Count != 1
                    || stack.NavigationPath[0] != ApplicationImportRoutes.ImportContent)
                    return;
                _importWordContent = Views.VStack(_app.RenderImportWord(_style));
                _importWordDeleteMessage = _app.ImportWordDeleteMessage;
                _importWordDeleteConfirmation = ImportWordDeleteConfirmation(
                    _importWordContent, _importWordDeleteMessage);
                _importWordSlot = Views.VStack(_importWordDeleteConfirmation ?? _importWordContent)
                    .WithNavigationTitle(_app.ImportWordTitle)
                    .WithInlineNavigationTitle()
                    .WithTabBarHidden();
                _renderedImportWordVersion = _app.ImportWordVersion;
                stack.Push(route, WithSamToolbar(stack, route, _importWordSlot));
            }
            else if (appearanceChanged || _renderedImportWordVersion != _app.ImportWordVersion)
            {
                var message = _app.ImportWordDeleteMessage;
                if (message is not null && message != _importWordDeleteMessage)
                {
                    // The word loaded: the confirmation is built around a new page, once.
                    var content = Views.VStack(_app.RenderImportWord(_style));
                    var confirmation = ImportWordDeleteConfirmation(content, message)!;
                    Replace(_importWordSlot, confirmation);
                    _importWordContent = content;
                    _importWordDeleteConfirmation = confirmation;
                    _importWordDeleteMessage = message;
                }
                else if (_importWordContent is not null)
                    Replace(_importWordContent, _app.RenderImportWord(_style));
                _renderedImportWordVersion = _app.ImportWordVersion;
            }
            if (_importWordDeleteConfirmation is not null)
            {
                _importWordDeleteConfirmation.IsPresented = _app.IsImportWordDeletePresented;
                _importWordDeleteConfirmation.DeletePending = _app.IsImportWordDeletePending;
            }
        }
        else if (_importWordSlot is not null)
        {
            var onTop = stack.NavigationPath.Count > 0 && stack.NavigationPath[^1] == route;
            // While Back is blocked the reference stays, so the next update pops the page.
            if (!onTop || stack.CanNavigateBack)
            {
                // Cleared first: the route callback must not report this pop as a Back.
                DropImportWordReferences();
                if (onTop)
                    stack.Pop();
            }
        }
    }

    private SwiftView? ImportWordDeleteConfirmation(SwiftView content, string? message) =>
        message is null ? null
            : Views.ResourceDeleteConfirmation(content,
                    _app.Localize("VocabWordEdit_ConfirmDelete"), message,
                    _app.Localize("Common_Close"),
                    _app.Localize("Common_Cancel"),
                    _app.Localize("Common_Delete"),
                    _app.CancelImportWordDelete,
                    _app.ConfirmImportWordDelete)
                .WithTransparentDeleteBackdrop()
                .WithAccessibilityIdentifier("native.import.content.word.delete");

    private void DropImportWordReferences()
    {
        _importWordSlot = null;
        _importWordContent = null;
        _importWordDeleteConfirmation = null;
        _importWordDeleteMessage = null;
        _renderedImportWordVersion = -1;
    }

    private void RecordImportContent()
    {
        _renderedImportContentVersion = _app.ImportContentVersion;
        _renderedImportContentStyle = _style;
        _renderedImportContentWidth = _style.ViewportWidth;
    }

    private void UpdateImportOverlay()
    {
        if (_importContentOverlay is not { } layer)
            return;
        var key = _app.ImportContentOverlayKey;
        if (string.Equals(key, _renderedImportOverlayKey, StringComparison.Ordinal)
            && ReferenceEquals(_renderedImportOverlayStyle, _style))
            return;
        _renderedImportOverlayKey = key;
        _renderedImportOverlayStyle = _style;
        ClearToastLayer(layer);
        if (_app.RenderImportContentOverlay(_style) is not { } overlay)
            return;
        var gutter = SourceUiLayout.PageGutter(_style.ViewportWidth);
        layer.Add(Views.VStack(overlay).WithFlexibleWidth().WithPadding(8, gutter, 0, gutter));
    }

    /// <summary>System Back (or a swipe) left the import page.</summary>
    private void OnImportRoutesChanged(IReadOnlyList<string> routes)
    {
        if (_importWordSlot is not null && !routes.Contains(ApplicationImportRoutes.ImportWord))
        {
            DropImportWordReferences();
            _app.OnImportWordPopped();
        }
        if (_importContentSlot is not null && !routes.Contains(ApplicationImportRoutes.ImportContent))
        {
            DropImportReferences();
            _app.OnImportContentPopped();
        }
    }

    /// <summary>
    /// Swaps the page's scroll content for the next render's when both have the same shape
    /// (the scroll alone, or the scroll under a toolbar with the same actions). False when
    /// the shapes differ (the Import action appeared or left), which redraws the page whole.
    /// </summary>
    private static bool TrySwapImportScroll(SwiftView slot, SwiftView next)
    {
        if (slot.Children.Count != 1)
            return false;
        var current = slot.Children[0];
        if (current.Kind != next.Kind || current.Children.Count != next.Children.Count
            || ImportScroll(current) is not { } scroll || ImportScroll(next) is not { } nextScroll
            || scroll.Children.Count != 1 || nextScroll.Children.Count != 1)
            return false;
        for (var index = 1; index < current.Children.Count; index++)
        {
            if (current.Children[index].Kind != next.Children[index].Kind
                || current.Children[index].Text != next.Children[index].Text)
                return false;
            if (current.Children[index].IsEnabled != next.Children[index].IsEnabled)
                current.Children[index].IsEnabled = next.Children[index].IsEnabled;
        }
        var content = nextScroll.Children[0];
        nextScroll.RemoveAt(0);
        next.Dispose();
        ReplaceContainerChildren(scroll.Children[0], content);
        return true;
    }

    private static SwiftView? ImportScroll(SwiftView page)
    {
        var scroll = page.Kind == "toolbar" && page.Children.Count > 0 ? page.Children[0] : page;
        return scroll is { Kind: "scroll", AccessibilityIdentifier: ImportContentView.ScrollId }
            && scroll.Children.Count == 1
            && scroll.Children[0].AccessibilityIdentifier == ImportContentView.ContentId
            ? scroll : null;
    }

    private void DropImportReferences()
    {
        DropImportWordReferences();
        _importContentSlot = null;
        _importContentOverlay = null;
        _renderedImportContentVersion = -1;
        _renderedImportContentStyle = null;
        _renderedImportOverlayKey = null;
        _renderedImportOverlayStyle = null;
    }
}
