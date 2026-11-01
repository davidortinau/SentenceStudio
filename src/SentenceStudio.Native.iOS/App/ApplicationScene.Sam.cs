using Microsoft.SwiftUI;
using SentenceStudio.Native.iOS.Features.Sam;
using Views = Microsoft.SwiftUI.SwiftUI;

namespace SentenceStudio.Native.iOS.App;

/// <summary>
/// Sam's SwiftUI presentation: one inspector beside the tab shell holding a
/// NavigationStack (chat, then the pushed conversation shelf). In a regular
/// width (iPad) it is a trailing side panel that leaves the page usable, as the
/// source's non-modal overlay is; in a compact width (iPhone) SwiftUI presents
/// it as a sheet. The composer nodes stay mounted while the stream re-renders,
/// so typing keeps focus.
/// </summary>
internal sealed partial class ApplicationScene
{
    private SwiftView? _samPanelHost;
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

    /// <summary>Wraps the tab shell in the Sam inspector. Called once per tab install.</summary>
    private SwiftView WrapWithSam(SwiftView tabs)
    {
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
        _samPanelHost = Views.Inspector(tabs, _samStack, _app.DismissSam);
        ApplySamSize(_app.SamState.Size);
        _renderedSamVersion = -1;
        _renderedSamPanel = null;
        return _samPanelHost;
    }

    /// <summary>
    /// Source panel sizes: compact 360 pt / expanded 520 pt wide beside the page
    /// (app.css .sam-panel--compact/--expanded); as a phone sheet, compact is 60%
    /// of the screen and expanded the full height.
    /// </summary>
    private void ApplySamSize(NativeSamPanelSize size)
    {
        var width = size == NativeSamPanelSize.Compact ? 360 : 520;
        _samPanelHost!.WithInspectorColumnWidth(width, min: width, max: width)
            .WithSheetDetentFraction(size == NativeSamPanelSize.Compact ? 0.6 : 1.0);
        _renderedSamSize = size;
    }

    private void UpdateSam()
    {
        if (_samPanelHost is null || _samPanelSlot is null || _samStack is null)
            return;
        _app.SetSamViewport(_style.ViewportWidth);
        var state = _app.SamState;
        var presented = _app.IsSamPresented;
        if (_renderedSamSize != state.Size)
            ApplySamSize(state.Size);
        if (!presented)
        {
            if (_samPanelHost.IsPresented)
                _samPanelHost.IsPresented = false;
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

        if (!_samPanelHost.IsPresented)
            _samPanelHost.IsPresented = true;
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
        _samPanelHost = null;
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
