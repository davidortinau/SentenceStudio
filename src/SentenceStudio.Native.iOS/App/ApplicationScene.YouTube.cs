using Microsoft.SwiftUI;
using SentenceStudio.Native.iOS.Features.Import;
using SentenceStudio.Native.iOS.Styling;
using Views = Microsoft.SwiftUI.SwiftUI;

namespace SentenceStudio.Native.iOS.App;

/// <summary>
/// The Library's YouTube import page, pushed on the Library stack above the list (a browse
/// page: the tab bar stays). A redraw swaps the page's scroll content in place, so the scroll
/// position stays; the messages are pinned over the page.
/// </summary>
internal sealed partial class ApplicationScene
{
    private SwiftView? _youTubeSlot;
    private SwiftView? _youTubeOverlay;
    private long _renderedYouTubeVersion = -1;
    private SourceUiStyle? _renderedYouTubeStyle;
    private double _renderedYouTubeWidth;
    private string? _renderedYouTubeOverlayKey;
    private SourceUiStyle? _renderedYouTubeOverlayStyle;
    private SwiftView? _youTubeChannelSlot;
    private SwiftView? _youTubeChannelContent;
    private SwiftView? _youTubeChannelOverlay;
    private long _renderedYouTubeChannelVersion = -1;
    private long _renderedYouTubeChannelGeneration = -1;
    private SourceUiStyle? _renderedYouTubeChannelStyle;
    private string? _renderedYouTubeChannelOverlayKey;
    private SourceUiStyle? _renderedYouTubeChannelOverlayStyle;

    private void UpdateLibraryYouTube(bool appearanceChanged)
    {
        var stack = _libraryStack;
        if (stack is null)
            return;
        const string route = ApplicationYouTubeRoutes.YouTubeImport;
        if (!_app.IsYouTubeImportOpen)
        {
            // Released without a Back (sign-out, a Study entry, a refused open): leave the page.
            if (_youTubeSlot is not null)
            {
                if (stack.NavigationPath.Contains(route) && !stack.CanNavigateBack)
                    return;
                DropYouTubeReferences();
                while (stack.NavigationPath.Count > 0 && stack.NavigationPath.Contains(route))
                    stack.Pop();
            }
            return;
        }
        if (_youTubeSlot is null)
        {
            if (stack.NavigationPath.Count != 0)
                return;
            _youTubeSlot = Views.VStack(_app.RenderYouTubeImport(_style)).WithFlexibleWidth();
            RecordYouTube();
            _youTubeOverlay = Views.VStack().WithFlexibleWidth();
            _renderedYouTubeOverlayKey = null;
            var page = Views.Overlay(_youTubeSlot, _youTubeOverlay, SwiftOverlayEdge.Top)
                .WithNavigationTitle(_app.YouTubeImportTitle)
                .WithInlineNavigationTitle();
            // A browse page with the tab bar: Sam's tab slot is its Sam entry (no toolbar button).
            stack.Push(ApplicationYouTubeRoutes.YouTubeImport, page.WithTabBarHidden(false));
        }
        else
        {
            var reshaped = appearanceChanged || !ReferenceEquals(_renderedYouTubeStyle, _style)
                || Math.Abs(_renderedYouTubeWidth - _style.ViewportWidth) >= 0.5;
            if (reshaped || _renderedYouTubeVersion != _app.YouTubeImportVersion)
            {
                var next = _app.RenderYouTubeImport(_style);
                if (reshaped || !TrySwapYouTubeScroll(_youTubeSlot, next))
                    Replace(_youTubeSlot, next);
                RecordYouTube();
            }
        }
        UpdateYouTubeOverlay();
        UpdateYouTubeChannel(stack, appearanceChanged);
    }

    /// <summary>
    /// The channel page (source /media-import/channel/{Id?}), pushed above the YouTube import page.
    /// A form with Save and Cancel: the tab bar hides, as for the other Library editors.
    /// </summary>
    private void UpdateYouTubeChannel(ISceneNavigation stack, bool appearanceChanged)
    {
        const string route = ApplicationYouTubeRoutes.YouTubeChannel;
        if (_app.IsYouTubeChannelOpen)
        {
            if (_youTubeChannelSlot is null)
            {
                if (stack.NavigationPath.Count != 1
                    || stack.NavigationPath[0] != ApplicationYouTubeRoutes.YouTubeImport)
                    return;
                _youTubeChannelContent = Views.VStack(_app.RenderYouTubeChannel(_style)).WithFlexibleWidth();
                _youTubeChannelOverlay = Views.VStack().WithFlexibleWidth();
                _renderedYouTubeChannelOverlayKey = null;
                _youTubeChannelSlot = Views.Overlay(_youTubeChannelContent, _youTubeChannelOverlay,
                        SwiftOverlayEdge.Top)
                    .WithNavigationTitle(_app.YouTubeChannelTitle)
                    .WithInlineNavigationTitle()
                    .WithTabBarHidden();
                RecordYouTubeChannel();
                stack.Push(ApplicationYouTubeRoutes.YouTubeChannel,
                    WithSamToolbar(stack, route, _youTubeChannelSlot));
            }
            else if (appearanceChanged || !ReferenceEquals(_renderedYouTubeChannelStyle, _style)
                || _renderedYouTubeChannelVersion != _app.YouTubeChannelVersion
                || _renderedYouTubeChannelGeneration != _app.YouTubeChannelGeneration)
            {
                if (_renderedYouTubeChannelGeneration != _app.YouTubeChannelGeneration)
                    _youTubeChannelSlot.WithNavigationTitle(_app.YouTubeChannelTitle);
                // Redrawn whole: the link field's typing updates this draw's toolbar Save.
                if (_youTubeChannelContent is not null)
                    Replace(_youTubeChannelContent, _app.RenderYouTubeChannel(_style));
                RecordYouTubeChannel();
            }
            UpdateYouTubeChannelOverlay();
        }
        else if (_youTubeChannelSlot is not null)
        {
            var onTop = stack.NavigationPath.Count > 0 && stack.NavigationPath[^1] == route;
            // While Back is blocked the references stay, so the next update pops the page.
            if (!onTop || stack.CanNavigateBack)
            {
                // Cleared first: the route callback must not report this pop as a Back.
                DropYouTubeChannelReferences();
                if (onTop)
                    stack.Pop();
            }
        }
    }

    private void RecordYouTubeChannel()
    {
        _renderedYouTubeChannelVersion = _app.YouTubeChannelVersion;
        _renderedYouTubeChannelGeneration = _app.YouTubeChannelGeneration;
        _renderedYouTubeChannelStyle = _style;
    }

    private void UpdateYouTubeChannelOverlay()
    {
        if (_youTubeChannelOverlay is not { } layer)
            return;
        var key = _app.YouTubeChannelOverlayKey;
        if (string.Equals(key, _renderedYouTubeChannelOverlayKey, StringComparison.Ordinal)
            && ReferenceEquals(_renderedYouTubeChannelOverlayStyle, _style))
            return;
        _renderedYouTubeChannelOverlayKey = key;
        _renderedYouTubeChannelOverlayStyle = _style;
        ClearToastLayer(layer);
        if (_app.RenderYouTubeChannelOverlay(_style) is not { } overlay)
            return;
        var gutter = SourceUiLayout.PageGutter(_style.ViewportWidth);
        layer.Add(Views.VStack(overlay).WithFlexibleWidth().WithPadding(8, gutter, 0, gutter));
    }

    private void DropYouTubeChannelReferences()
    {
        _youTubeChannelSlot = null;
        _youTubeChannelContent = null;
        _youTubeChannelOverlay = null;
        _renderedYouTubeChannelVersion = -1;
        _renderedYouTubeChannelGeneration = -1;
        _renderedYouTubeChannelStyle = null;
        _renderedYouTubeChannelOverlayKey = null;
        _renderedYouTubeChannelOverlayStyle = null;
    }

    private void RecordYouTube()
    {
        _renderedYouTubeVersion = _app.YouTubeImportVersion;
        _renderedYouTubeStyle = _style;
        _renderedYouTubeWidth = _style.ViewportWidth;
    }

    private void UpdateYouTubeOverlay()
    {
        if (_youTubeOverlay is not { } layer)
            return;
        var key = _app.YouTubeImportOverlayKey;
        if (string.Equals(key, _renderedYouTubeOverlayKey, StringComparison.Ordinal)
            && ReferenceEquals(_renderedYouTubeOverlayStyle, _style))
            return;
        _renderedYouTubeOverlayKey = key;
        _renderedYouTubeOverlayStyle = _style;
        ClearToastLayer(layer);
        if (_app.RenderYouTubeImportOverlay(_style) is not { } overlay)
            return;
        var gutter = SourceUiLayout.PageGutter(_style.ViewportWidth);
        layer.Add(Views.VStack(overlay).WithFlexibleWidth().WithPadding(8, gutter, 0, gutter));
    }

    /// <summary>
    /// System Back (or a swipe) left the YouTube pages. On a phone one Back can close both (the
    /// collapsed split view goes back to the list column): every reference is dropped first,
    /// then the app is told, because telling it redraws at once and must not meet a disposed
    /// page.
    /// </summary>
    private void OnYouTubeRoutesChanged(IReadOnlyList<string> routes)
    {
        var channelLeft = _youTubeChannelSlot is not null
            && !routes.Contains(ApplicationYouTubeRoutes.YouTubeChannel);
        var importLeft = _youTubeSlot is not null
            && !routes.Contains(ApplicationYouTubeRoutes.YouTubeImport);
        if (channelLeft)
            DropYouTubeChannelReferences();
        if (importLeft)
            DropYouTubeReferences();
        // Releasing the import releases its channel page too; telling the app about the
        // channel first would redraw with the import still open and push it again.
        if (importLeft)
            _app.OnYouTubeImportPopped();
        else if (channelLeft)
            _app.OnYouTubeChannelPopped();
    }

    /// <summary>
    /// Swaps the page's Form Sections for the next render's when both have the same toolbar
    /// shape (the same actions); false when the shapes differ, which redraws the page whole.
    /// </summary>
    private bool TrySwapYouTubeScroll(SwiftView slot, SwiftView next) =>
        TrySwapFormSections(slot, next, YouTubeImportView.ScrollId);

    private void DropYouTubeReferences()
    {
        DropYouTubeChannelReferences();
        _youTubeSlot = null;
        _youTubeOverlay = null;
        _renderedYouTubeVersion = -1;
        _renderedYouTubeStyle = null;
        _renderedYouTubeOverlayKey = null;
        _renderedYouTubeOverlayStyle = null;
    }
}
