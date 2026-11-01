using Microsoft.SwiftUI;
using SentenceStudio.Native.iOS.Features.Sam;
using Views = Microsoft.SwiftUI.SwiftUI;

namespace SentenceStudio.Native.iOS.App;

/// <summary>
/// Sam's SwiftUI presentation: one sheet over the tab shell holding a
/// NavigationStack (chat, then the pushed conversation shelf). The composer
/// nodes stay mounted while the stream re-renders, so typing keeps focus.
/// </summary>
internal sealed partial class ApplicationScene
{
    private SwiftView? _samSheet;
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
    private (NativeSamPanelSize Size, string Persona, bool Resizable)? _renderedSamPanel;
    private double _samDetent;

    /// <summary>Wraps the tab shell in the Sam sheet. Called once per tab install.</summary>
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
        _samDetent = SamDetent();
        _samSheet = Views.Sheet(tabs, _samStack, _app.DismissSam)
            .WithSheetDetentFraction(_samDetent);
        _renderedSamVersion = -1;
        _renderedSamPanel = null;
        return _samSheet;
    }

    private bool SamResizable => _style.ViewportWidth < 768;

    private double SamDetent() =>
        SamResizable && _app.SamState.Size == NativeSamPanelSize.Compact ? 0.6 : 1.0;

    private void UpdateSam()
    {
        if (_samSheet is null || _samPanelSlot is null || _samStack is null)
            return;
        _app.SetSamViewport(_style.ViewportWidth);
        var state = _app.SamState;
        var presented = _app.IsSamPresented;
        var detent = SamDetent();
        if (Math.Abs(_samDetent - detent) > 0.001)
        {
            _samDetent = detent;
            _samSheet.WithSheetDetentFraction(detent);
        }
        if (!presented)
        {
            if (_samSheet.IsPresented)
                _samSheet.IsPresented = false;
            return;
        }

        var persona = SamView.Persona(state);
        var panelKey = (state.Size, persona, SamResizable);
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

        if (!_samSheet.IsPresented)
            _samSheet.IsPresented = true;
    }

    private SwiftView BuildSamPanel(NativeSamState state, string persona)
    {
        _samStreamSlot = Views.VStack(_app.RenderSamStream(_style)).WithFlexibleWidth();
        var composer = SamView.Composer(_style, state, persona, _app.Localize, _app.SamActions,
            out var field, out var send, out var counter);
        _samField = field;
        _samSend = send;
        _samCounter = counter;
        _samHeader = SamView.HeaderActions(state, _app.Localize, _app.SamActions, SamResizable);
        var body = Views.BottomInset(_style.Page(Views.Scroll(_samStreamSlot).WithScrollAnchoredToBottom()),
            composer);
        return Views.Toolbar(_style.Page(body), _samHeader)
            .WithNavigationTitle(SamView.Title(state, _app.Localize))
            .WithInlineNavigationTitle();
    }

    private void DropSamReferences()
    {
        _samSheet = null;
        _samStack = null;
        _samPanelSlot = null;
        _samStreamSlot = null;
        _samConversationsSlot = null;
        _samField = null;
        _samSend = null;
        _samCounter = null;
        _samHeader = [];
        _renderedSamPanel = null;
        _renderedSamVersion = -1;
        _renderedSamConversationsVersion = -1;
    }
}
