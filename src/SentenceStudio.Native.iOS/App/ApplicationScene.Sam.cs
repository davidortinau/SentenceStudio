using Microsoft.SwiftUI;
using SentenceStudio.Native.iOS.Features.Sam;
using SentenceStudio.Native.iOS.Platform;
using Views = Microsoft.SwiftUI.SwiftUI;

namespace SentenceStudio.Native.iOS.App;

/// <summary>
/// Sam's SwiftUI presentation: one NavigationStack (chat, then the pushed
/// conversation shelf) shown either in a sheet over the tab shell (phone, or
/// narrower than 768 pt: 60% or full height) or in a trailing inspector column
/// beside it (iPad: 360 or 520 pt, the page stays usable, as the source's
/// non-modal overlay is). The stack moves between the two when the width crosses
/// the breakpoint, so chat, draft, and composer focus survive. The composer
/// nodes stay mounted while the stream re-renders, so typing keeps focus.
/// </summary>
internal sealed partial class ApplicationScene
{
    private SwiftView? _samPanelHost;
    private SwiftView? _samSheet;
    private SwiftView? _samSheetHolder;
    private SwiftView? _samInspectorHolder;
    private bool? _samInSheet;
    private SwiftView? _samStack;
    private SwiftView? _samPanelSlot;
    private SwiftView? _samStreamSlot;
    private SwiftView? _samConversationsSlot;
    private SwiftView? _samField;
    private SwiftView? _samSend;
    private SwiftView? _samCounter;
    private SwiftView[] _samHeader = [];
    private long _renderedSamVersion = -1;
    private long _renderedSamConversationsVersion = -1;
    private (NativeSamPanelSize Size, string Persona)? _renderedSamPanel;
    private NativeSamPanelSize? _renderedSamSize;
    private double _samDetent;

    private readonly List<(SwiftView Stack, string Route, SwiftView Slot)> _samToolbars = [];

    /// <summary>
    /// Source MainLayout mounts Sam on every signed-in page. The tab roots keep the
    /// floating entry; every pushed screen (activities, the quiz, Settings, Profile,
    /// Skills, Vocabulary, Recent lookups, Activity Log, the resource form) gets the
    /// same entry in its navigation bar instead, because a floating button there
    /// would cover the screen's bottom answer and send bars.
    /// </summary>
    /// <remarks>
    /// A facade toolbar's actions are fixed once it is built, so the entry lives in
    /// its own small toolbar host inside an empty top overlay layer (the overlay does
    /// not change the page's layout; SwiftUI collects toolbar items from the layer
    /// like any other descendant). <see cref="UpdateSamToolbars"/> adds that host when
    /// Sam becomes available for the owner and removes it when Sam is not, so a screen
    /// pushed before Sam's availability loads still gets the entry.
    /// </remarks>
    private SwiftView WithSamToolbar(SwiftView stack, string route, SwiftView page)
    {
        _samToolbars.RemoveAll(entry => ReferenceEquals(entry.Stack, stack)
            && string.Equals(entry.Route, route, StringComparison.Ordinal));
        var slot = Views.VStack();
        slot.Spacing = 0;
        _samToolbars.Add((stack, route, slot));
        return Views.Overlay(page, slot, SwiftOverlayEdge.Top);
    }

    /// <summary>
    /// Keeps each live screen's Sam entry in step with availability, the panel and
    /// the coach name. Runs after every push in the same update.
    /// </summary>
    private void UpdateSamToolbars()
    {
        _samToolbars.RemoveAll(entry => !entry.Stack.NavigationPath.Contains(entry.Route));
        var available = _app.IsSamAvailable;
        var enabled = _app.IsSamEntryVisible;
        var label = SamView.EntryLabel(_app.SamState, _app.Localize);
        foreach (var entry in _samToolbars)
        {
            var slot = entry.Slot;
            if (!available)
            {
                if (slot.Children.Count > 0)
                    slot.RemoveAt(0);
                continue;
            }
            if (slot.Children.Count == 0)
                slot.Add(SamToolbarHost());
            var button = slot.Children[0].Children[1];
            if (button.IsEnabled != enabled)
                button.IsEnabled = enabled;
            button.WithAccessibilityLabel(label);
        }
    }

    /// <summary>A zero-size view that carries only the Sam toolbar button.</summary>
    private SwiftView SamToolbarHost()
    {
        var button = SamView.ToolbarEntry(_app.SamState, _app.Localize, _app.SamActions.Open);
        button.IsEnabled = _app.IsSamEntryVisible;
        return Views.Toolbar(Views.VStack(Views.Text(string.Empty)), button);
    }

    /// <summary>Wraps the tab shell in the Sam sheet and inspector. Called once per tab install.</summary>
    private SwiftView WrapWithSam(SwiftView tabs)
    {
        _samToolbars.Clear();
        _samPanelSlot = Views.VStack(Views.Text(_app.Localize("Common_Loading")));
        _samStack = Views.NavigationStack(_samPanelSlot, routes =>
        {
            if (routes.Count == 0 && _samConversationsSlot is not null)
            {
                _samConversationsSlot = null;
                _renderedSamConversationsVersion = -1;
                if (_app.SamState.ConversationsOpen)
                    _app.SamActions.ToggleConversations();
            }
        }).WithAccessibilityIdentifier("native.sam.panel");
        _samSheetHolder = Views.VStack().WithFlexibleWidth();
        _samInspectorHolder = Views.VStack().WithFlexibleWidth();
        _samDetent = SamDetent(_app.SamState.Size);
        _samSheet = Views.Sheet(tabs, _samSheetHolder, OnSamSheetDismissed)
            .WithSheetDetentFraction(_samDetent);
        _samPanelHost = Views.Inspector(_samSheet, _samInspectorHolder, _app.DismissSam);
        _samInSheet = null;
        PlaceSamStack();
        ApplySamSize(_app.SamState.Size);
        _renderedSamVersion = -1;
        _renderedSamPanel = null;
        return _samPanelHost;
    }

    /// <summary>Phones always, and anything narrower than 768 pt, present Sam as a sheet.</summary>
    private bool SamUsesSheet => NativeDeviceIdiom.IsPhone || _style.ViewportWidth < 768;

    /// <summary>As a sheet, compact is 60% of the screen and expanded the full height.</summary>
    private static double SamDetent(NativeSamPanelSize size) =>
        size == NativeSamPanelSize.Compact ? 0.6 : 1.0;

    /// <summary>
    /// Source panel sizes beside the page: compact 360 pt / expanded 520 pt wide
    /// (app.css .sam-panel--compact/--expanded). The sheet height is applied in UpdateSam.
    /// </summary>
    private void ApplySamSize(NativeSamPanelSize size)
    {
        var width = size == NativeSamPanelSize.Compact ? 360 : 520;
        _samPanelHost!.WithInspectorColumnWidth(width, min: width, max: width);
        _renderedSamSize = size;
    }

    /// <summary>
    /// Puts the Sam stack in the sheet or the inspector for the current width. The
    /// same nodes move, so the conversation, the draft, and the pushed shelf stay;
    /// a focused composer is focused again in its new place.
    /// </summary>
    private void PlaceSamStack()
    {
        var useSheet = SamUsesSheet;
        if (_samInSheet == useSheet)
            return;
        var focused = _samField?.FocusRequested == true;
        var from = _samInSheet switch
        {
            true => _samSheetHolder!,
            false => _samInspectorHolder!,
            null => null,
        };
        // Set first: the sheet reports a programmatic close as a dismissal too.
        _samInSheet = useSheet;
        if (useSheet && _samPanelHost!.IsPresented)
            _samPanelHost.IsPresented = false;
        else if (!useSheet && _samSheet!.IsPresented)
            _samSheet.IsPresented = false;
        from?.RemoveAt(0);
        (useSheet ? _samSheetHolder! : _samInspectorHolder!).Add(_samStack!);
        if (focused && _samField is { IsEnabled: true } field)
            field.FocusRequested = true;
    }

    /// <summary>Only a sheet the person closes, while Sam lives in it, closes Sam.</summary>
    private void OnSamSheetDismissed()
    {
        if (_samInSheet == true && _samSheet is { IsPresented: false })
            _app.DismissSam();
    }

    private SwiftView ActiveSamHost => _samInSheet == true ? _samSheet! : _samPanelHost!;

    private void UpdateSam()
    {
        if (_samPanelHost is null || _samPanelSlot is null || _samStack is null)
            return;
        UpdateSamToolbars();
        _app.SetSamViewport(_style.ViewportWidth);
        var state = _app.SamState;
        var presented = _app.IsSamPresented;
        PlaceSamStack();
        if (_renderedSamSize != state.Size)
            ApplySamSize(state.Size);
        var detent = SamDetent(state.Size);
        if (Math.Abs(_samDetent - detent) > 0.001)
        {
            _samDetent = detent;
            _samSheet!.WithSheetDetentFraction(detent);
        }
        var host = ActiveSamHost;
        if (!presented)
        {
            if (host.IsPresented)
                host.IsPresented = false;
            return;
        }

        var persona = SamView.Persona(state);
        var panelKey = (state.Size, persona);
        if (_renderedSamPanel != panelKey || _samField is null)
        {
            Replace(_samPanelSlot, BuildSamPanel(state, persona));
            _renderedSamPanel = panelKey;
            _renderedSamVersion = state.Version;
        }
        else if (_renderedSamVersion != state.Version)
        {
            Replace(_samStreamSlot!, _app.RenderSamStream(_style));
            _renderedSamVersion = state.Version;
        }
        foreach (var action in _samHeader)
        {
            var enabled = !state.Busy || action.AccessibilityIdentifier == "native.sam.close";
            if (action.IsEnabled != enabled)
                action.IsEnabled = enabled;
        }
        SamView.UpdateComposer(state, _samField!, _samSend!, _samCounter!, _app.Localize);

        if (state.ConversationsOpen && _samConversationsSlot is null
            && _samStack.NavigationPath.Count == 0)
        {
            _samConversationsSlot = Views.VStack(_app.RenderSamConversations(_style))
                .WithNavigationTitle(_app.Localize("Coach_Conversations"))
                .WithInlineNavigationTitle();
            _renderedSamConversationsVersion = state.Version;
            _samStack.Push("sam-conversations", _samConversationsSlot);
        }
        else if (!state.ConversationsOpen && _samConversationsSlot is not null
            && _samStack.NavigationPath.Count > 0)
        {
            _samStack.Pop();
            _samConversationsSlot = null;
            _renderedSamConversationsVersion = -1;
        }
        else if (_samConversationsSlot is not null
            && _renderedSamConversationsVersion != state.Version)
        {
            Replace(_samConversationsSlot, _app.RenderSamConversations(_style));
            _renderedSamConversationsVersion = state.Version;
        }

        if (!host.IsPresented)
            host.IsPresented = true;
    }

    private SwiftView BuildSamPanel(NativeSamState state, string persona)
    {
        _samStreamSlot = Views.VStack(_app.RenderSamStream(_style)).WithFlexibleWidth();
        var composer = SamView.Composer(_style, state, persona, _app.Localize, _app.SamActions,
            out var field, out var send, out var counter);
        _samField = field;
        _samSend = send;
        _samCounter = counter;
        _samHeader = SamView.HeaderActions(state, _app.Localize, _app.SamActions);
        var body = Views.BottomInset(_style.Page(Views.Scroll(_samStreamSlot).WithScrollAnchoredToBottom()),
            composer);
        return Views.Toolbar(_style.Page(body), _samHeader)
            .WithNavigationTitle(SamView.Title(state, _app.Localize))
            .WithInlineNavigationTitle();
    }

    private void DropSamReferences()
    {
        _samToolbars.Clear();
        _samPanelHost = null;
        _samSheet = null;
        _samSheetHolder = null;
        _samInspectorHolder = null;
        _samInSheet = null;
        _samStack = null;
        _samPanelSlot = null;
        _samStreamSlot = null;
        _samConversationsSlot = null;
        _samField = null;
        _samSend = null;
        _samCounter = null;
        _samHeader = [];
        _renderedSamPanel = null;
        _renderedSamSize = null;
        _renderedSamVersion = -1;
        _renderedSamConversationsVersion = -1;
    }
}
