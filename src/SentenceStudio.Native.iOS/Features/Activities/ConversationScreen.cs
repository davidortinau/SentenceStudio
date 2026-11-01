using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.SwiftUI;
using SentenceStudio.Native.iOS.Styling;
using Views = Microsoft.SwiftUI.SwiftUI;

namespace SentenceStudio.Native.iOS.Features.Activities;

/// <summary>
/// SwiftUI Conversation (source Conversation.razor). Toolbar language menu;
/// scenario picker above the scrolling chat; persona and learner bubbles with
/// grammar corrections, comprehension and Listen; reply-pending indicator;
/// Playing audio / Stop bar; the input card with quick phrases pinned at the
/// bottom. While the layout is unchanged, state updates mutate the mounted
/// slots in place, so the mounted text field (and any Korean IME composition
/// or keyboard focus in it) survives replies and audio events.
/// </summary>
internal sealed class ConversationScreen : IActivityScreen
{
    private readonly ActivityScreenHost _host;
    private readonly NativeConversationPresenter _presenter;
    private NativeConversationState _state = new(NativePhase.Loading, IsStarting: true);
    private string? _layoutKey;
    private int _renderedDraftVersion = -1;
    private SwiftView? _top;
    private SwiftView? _chat;
    private SwiftView? _audio;
    private SwiftView? _phrases;
    private SwiftView? _field;
    private SwiftView? _toggle;
    private SwiftView? _send;

    public ConversationScreen(ActivityScreenHost host)
    {
        _host = host;
        _presenter = host.Services.GetRequiredService<NativeConversationPresenter>();
        _presenter.StateChanged += OnStateChanged;
    }

    public string TitleKey => NativeConversationText.Title;

    public bool HasExited => _state.Destination == NativeDestination.TodaysPlan;

    public async Task OpenAsync(NativeActivityLaunch launch, CancellationToken cancellationToken) =>
        _state = await _presenter.OpenAsync(launch, cancellationToken);

    public async Task ExitAsync(CancellationToken cancellationToken) =>
        _state = await _presenter.ExitAsync(cancellationToken);

    private void OnStateChanged(NativeConversationState state)
    {
        _state = state;
        _host.Publish();
    }

    /// <summary>Commands bypass the controller's busy gate so a slow reply never disables Back; the presenter serializes them.</summary>
    private void Fire(Func<CancellationToken, Task<NativeConversationState>> action) =>
        _ = FireAsync(action);

    private async Task FireAsync(Func<CancellationToken, Task<NativeConversationState>> action)
    {
        try
        {
            _state = await action(_host.Lifetime);
        }
        catch (OperationCanceledException) when (_host.Lifetime.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            _host.Logger?.LogError(ex, "Native iOS Conversation action failed.");
            _state = _state with
            {
                Notices = [new(NativeConversationNoticeKind.Error, NativeText.UnexpectedError)]
            };
        }
        _host.Publish();
    }

    private string LayoutKey(NativeConversationState state, SourceUiStyle style) =>
        string.Join('|', state.Phase, state.Destination, state.TargetLanguage, state.ActiveScenarioId,
            state.Scenarios.IsDefault ? 0 : state.Scenarios.Length,
            style.GetHashCode(), Math.Round(style.ViewportWidth));

    public bool TryUpdateInPlace(SourceUiStyle style)
    {
        var state = _state;
        if (_layoutKey is null || _layoutKey != LayoutKey(state, style) || state.Phase != NativePhase.Ready
            || _top is null || _chat is null || _audio is null || _phrases is null
            || _field is null || _toggle is null || _send is null)
            return false;
        var text = _host.Text;
        Fill(_top, TopRows(state, style, text));
        Fill(_chat, ChatRows(state, style, text));
        Fill(_audio, AudioRows(state, style, text));
        Fill(_phrases, PhraseRows(state, style, text));
        UpdateInput(state, style);
        return true;
    }

    public SwiftView Render(SourceUiStyle style)
    {
        var text = _host.Text;
        var state = _state;
        _top = _chat = _audio = _phrases = _field = _toggle = _send = null;
        _layoutKey = LayoutKey(state, style);
        var gutter = SourceUiLayout.PageGutter(style.ViewportWidth);
        SwiftView page;
        switch (state.Phase)
        {
            case NativePhase.Idle:
            case NativePhase.Loading:
                var loading = Views.VStack(
                        Views.IndeterminateProgress(text(NativeConversationText.Starting))
                            .WithAccessibilityIdentifier(NativeConversationAccess.Loading),
                        style.Text(Views.Text(text(NativeConversationText.Starting)),
                            SourceTextRole.Body1, SourceTextTone.Secondary))
                    .WithFlexibleWidth(alignment: SwiftHorizontalAlignment.Center)
                    .WithPadding(48, gutter, 16, gutter);
                loading.Spacing = 16;
                page = Views.Scroll(loading).WithAccessibilityIdentifier(NativeConversationAccess.Screen);
                break;
            case NativePhase.Ready when state.Destination == NativeDestination.Activity:
                page = ReadyPage(state, style, text, gutter);
                break;
            default:
                var blocked = Views.VStack(
                        style.Text(Views.Text(text(state.ErrorKey ?? NativeConversationText.Unavailable)),
                                SourceTextRole.Body1, SourceTextTone.Danger)
                            .WithAccessibilityIdentifier(NativeConversationAccess.Blocked),
                        style.SecondaryButton(text(NativeConversationText.GoBack), _host.Exit)
                            .WithAccessibilityIdentifier(NativeConversationAccess.BlockedBack))
                    .WithFlexibleWidth()
                    .WithPadding(16, gutter, 16, gutter);
                blocked.Spacing = SourceUiLayout.PageGap;
                page = Views.Scroll(blocked).WithAccessibilityIdentifier(NativeConversationAccess.Screen);
                break;
        }
        page = style.Page(page);
        return state.Phase is NativePhase.Blocked || state.Destination != NativeDestination.Activity
            ? page
            : Views.Toolbar(page, LanguageMenu(state, style, text));
    }

    private SwiftView ReadyPage(NativeConversationState state, SourceUiStyle style,
        Func<string, string> text, double gutter)
    {
        _top = Views.VStack(TopRows(state, style, text).ToArray())
            .WithPadding(8, gutter, 0, gutter)
            .WithFlexibleWidth();
        _top.Spacing = 8;
        _chat = Views.VStack(ChatRows(state, style, text).ToArray())
            .WithPadding(8, gutter, 8, gutter)
            .WithFlexibleWidth()
            .WithAccessibilityIdentifier(NativeConversationAccess.Messages);
        _chat.Spacing = 8;
        _audio = Views.VStack(AudioRows(state, style, text).ToArray()).WithFlexibleWidth();
        _phrases = Views.VStack(PhraseRows(state, style, text).ToArray()).WithFlexibleWidth();

        var field = style.TextInput(Views.TextField(text(NativeConversationText.TypeMessage), state.Draft,
                value =>
                {
                    _presenter.SetDraft(value);
                    UpdateSendAvailability(style, value, _state.IsWaitingForReply);
                }))
            .WithFlexibleWidth()
            .WithAccessibilityLabel(text(NativeConversationText.TypeMessage))
            .WithAccessibilityIdentifier(NativeConversationAccess.Input)
            .OnSubmit(() => Fire(_presenter.SendAsync));
        _field = field;
        _renderedDraftVersion = state.DraftVersion;
        // Source: chat-quote toggle (its title reuses the placeholder text; native names it).
        _toggle = style.LabelButton(Views.Image("quote.bubble"),
                () => Fire(_presenter.ToggleQuickPhrasesAsync), SourceButtonRole.FilledSecondary, 10)
            .WithAccessibilityLabel(text(NativeConversationText.QuickPhrases))
            .WithAccessibilityIdentifier(NativeConversationAccess.QuickPhrasesToggle);
        _send = style.LabelButton(Views.Text(text(NativeConversationText.Send)),
                () => Fire(_presenter.SendAsync), SourceButtonRole.Primary, 20)
            .WithAccessibilityLabel(text(NativeConversationText.Send))
            .WithAccessibilityIdentifier(NativeConversationAccess.Send);
        var inputRow = Views.HStack(field, _toggle, _send).WithFlexibleWidth();
        inputRow.Spacing = 8;
        var card = Views.VStack(_phrases, inputRow).WithPadding(SourceUiLayout.CardInset);
        card.Spacing = 8;
        var footer = Views.VStack(_audio, style.Card(card))
            .WithPadding(8, gutter, 8, gutter)
            .WithFlexibleWidth()
            .WithBackgroundColor(style.Appearance.BodyBackground.Light, style.Appearance.BodyBackground.Dark);
        footer.Spacing = 8;
        UpdateInput(state, style);

        var chat = Views.BottomInset(Views.Scroll(_chat).WithScrollAnchoredToBottom(), footer);
        var page = Views.VStack(_top, chat).WithFlexibleWidth()
            .WithAccessibilityIdentifier(NativeConversationAccess.Screen);
        page.Spacing = 0;
        return page;
    }

    private IEnumerable<SwiftView> TopRows(NativeConversationState state, SourceUiStyle style,
        Func<string, string> text)
    {
        if (!state.Notices.IsDefaultOrEmpty)
            yield return Notices(state, style, text);
        if (!state.Scenarios.IsDefaultOrEmpty)
        {
            var options = state.Scenarios
                .Select(item => new SwiftPickerOption(item.Id.ToString(CultureInfo.InvariantCulture),
                    item.DisplayName))
                .ToArray();
            var selected = (state.ActiveScenarioId ?? state.Scenarios[0].Id)
                .ToString(CultureInfo.InvariantCulture);
            var picker = Views.Picker(text(NativeConversationText.Scenario), options, selected,
                    value =>
                    {
                        if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id))
                            Fire(token => _presenter.SelectScenarioAsync(id, token));
                    })
                .WithPickerStyle(SwiftPickerStyle.Menu)
                .WithAccessibilityIdentifier(NativeConversationAccess.Scenario);
            var row = Views.HStack(
                style.Text(Views.Text(text(NativeConversationText.Scenario)),
                    SourceTextRole.Caption1, SourceTextTone.Secondary),
                picker,
                Views.VStack().WithFlexibleWidth());
            row.Spacing = 8;
            yield return row;
        }
    }

    private SwiftView Notices(NativeConversationState state, SourceUiStyle style, Func<string, string> text)
    {
        var rows = new List<SwiftView>();
        var index = 0;
        foreach (var notice in state.Notices)
        {
            var label = style.Text(Views.Text(text(notice.Key)), SourceTextRole.Body2);
            var view = notice.Kind == NativeConversationNoticeKind.Error
                ? style.Alert(label, success: false, compact: true)
                : style.InfoAlert(label, compact: true);
            rows.Add(view.WithAccessibilityIdentifier($"{NativeConversationAccess.Notice}.{index++}"));
        }
        var dismiss = Views.Button(Views.Image("xmark"), () => Fire(_presenter.DismissNoticesAsync))
            .WithButtonStyle(SwiftButtonStyle.Plain)
            .WithAccessibilityLabel(text(NativeConversationText.Dismiss))
            .WithAccessibilityIdentifier(NativeConversationAccess.DismissNotices);
        var stack = Views.VStack(rows.ToArray()).WithFlexibleWidth();
        stack.Spacing = 6;
        var row = Views.HStack(stack, dismiss).WithAccessibilityIdentifier(NativeConversationAccess.Notices);
        row.Spacing = 8;
        return row;
    }

    private IEnumerable<SwiftView> ChatRows(NativeConversationState state, SourceUiStyle style,
        Func<string, string> text)
    {
        foreach (var message in state.Messages.IsDefault ? [] : state.Messages)
            yield return Bubble(message, style, text);
        if (state.IsWaitingForReply)
        {
            var dots = Views.IndeterminateProgress(text("Common_Loading"))
                .WithPadding(12, 16, 12, 16)
                .WithRoundedSurface(style.Appearance.CardSurface.Light, style.Appearance.CardSurface.Dark, 16)
                .WithAccessibilityIdentifier(NativeConversationAccess.Typing);
            yield return Views.VStack(dots).WithFlexibleWidth(alignment: SwiftHorizontalAlignment.Leading);
        }
    }

    private SwiftView Bubble(NativeConversationMessage message, SourceUiStyle style, Func<string, string> text)
    {
        var appearance = style.Appearance;
        var id = message.AccessibilityId;
        var rows = new List<SwiftView>
        {
            style.Text(Views.Text(message.Text), SourceTextRole.Body1)
                .WithAccessibilityIdentifier($"{id}.text")
        };
        if (!message.Corrections.IsDefaultOrEmpty)
        {
            var corrections = new List<SwiftView> { Views.Divider() };
            var index = 0;
            foreach (var correction in message.Corrections)
            {
                var change = Views.HStack(
                    style.Text(Views.Text(correction.Original ?? string.Empty), SourceTextRole.Small, SourceTextTone.Danger)
                        .WithStrikethrough(),
                    style.Text(Views.Text("→"), SourceTextRole.Small),
                    style.Text(Views.Text(correction.Corrected ?? string.Empty), SourceTextRole.SmallStrong,
                        SourceTextTone.Success));
                change.Spacing = 4;
                var block = new List<SwiftView> { change };
                if (!string.IsNullOrEmpty(correction.Explanation))
                    block.Add(style.Text(Views.Text(correction.Explanation), SourceTextRole.Small,
                        SourceTextTone.Secondary));
                var item = Views.VStack(block.ToArray())
                    .WithAccessibilityIdentifier($"{id}.correction.{index++}");
                item.Spacing = 2;
                corrections.Add(item);
            }
            var stack = Views.VStack(corrections.ToArray());
            stack.Spacing = 4;
            rows.Add(stack);
        }
        if (message.ShowsComprehension)
            rows.Add(style.Text(Views.Text(string.Format(CultureInfo.CurrentCulture,
                        text(NativeConversationText.Comprehension), (int)message.Comprehension)),
                    SourceTextRole.Small, SourceTextTone.Secondary)
                .WithAccessibilityIdentifier($"{id}.comprehension"));
        if (!message.IsUser)
        {
            var listen = Views.Button(
                    Views.HStack(Views.Image("speaker.wave.2"),
                        style.Text(Views.Text(text(NativeConversationText.Listen)), SourceTextRole.Body2,
                            SourceTextTone.Primary)),
                    () => Fire(token => _presenter.ListenAsync(message.Index, token)))
                .WithButtonStyle(SwiftButtonStyle.Plain)
                .WithForegroundColor(appearance.Primary.Light, appearance.Primary.Dark)
                .WithAccessibilityLabel(text(NativeConversationText.Listen))
                .WithAccessibilityIdentifier($"{id}.listen");
            rows.Add(listen);
        }
        var content = Views.VStack(rows.ToArray()).WithPadding(12, 16, 12, 16);
        content.Spacing = 4;
        var fill = message.IsUser
            ? new SourceColorPair(Blend(appearance.Primary.Light, appearance.BodyBackground.Light, 0.1),
                Blend(appearance.Primary.Dark, appearance.BodyBackground.Dark, 0.1))
            : appearance.CardSurface;
        var bubble = content.WithRoundedSurface(fill.Light, fill.Dark, 16).WithAccessibilityIdentifier(id);
        // Source: content-sized bubble up to 75% width, learner bubbles on the right.
        // The bubble is not flexible, so it takes its text's width and wraps at the row.
        var inset = style.ViewportWidth * 0.25;
        return message.IsUser
            ? Views.VStack(bubble).WithFlexibleWidth(alignment: SwiftHorizontalAlignment.Trailing)
                .WithPadding(0, 0, 0, inset)
            : Views.VStack(bubble).WithFlexibleWidth(alignment: SwiftHorizontalAlignment.Leading)
                .WithPadding(0, inset, 0, 0);
    }

    private IEnumerable<SwiftView> AudioRows(NativeConversationState state, SourceUiStyle style,
        Func<string, string> text)
    {
        if (!state.IsPlayingAudio)
            yield break;
        var stop = style.LabelButton(
                Views.HStack(Views.Image("stop.fill"), Views.Text(text(NativeConversationText.Stop))),
                () => Fire(_presenter.StopAudioAsync), SourceButtonRole.FilledSecondary, 12)
            .WithAccessibilityLabel(text(NativeConversationText.Stop))
            .WithAccessibilityIdentifier(NativeConversationAccess.AudioStop);
        var row = Views.HStack(stop,
                style.Text(Views.Text(text(NativeConversationText.PlayingAudio)), SourceTextRole.Caption1,
                    SourceTextTone.Secondary),
                Views.VStack().WithFlexibleWidth())
            .WithPadding(8);
        row.Spacing = 8;
        yield return style.Card(row).WithAccessibilityIdentifier(NativeConversationAccess.Audio);
    }

    private IEnumerable<SwiftView> PhraseRows(NativeConversationState state, SourceUiStyle style,
        Func<string, string> text)
    {
        if (!state.ShowQuickPhrases)
            yield break;
        var buttons = NativeConversationState.QuickPhrases
            .Select((phrase, index) => style.OutlineButton(Views.Text(phrase),
                    () => Fire(token => _presenter.UseQuickPhraseAsync(index, token)))
                .WithAccessibilityLabel(phrase)
                .WithAccessibilityIdentifier($"{NativeConversationAccess.QuickPhrase}.{index}"))
            .ToArray();
        var grid = Views.LazyVGrid(style.ViewportWidth < 600 ? 2 : 4, buttons);
        grid.Spacing = 8;
        yield return grid;
    }

    private SwiftView LanguageMenu(NativeConversationState state, SourceUiStyle style, Func<string, string> text)
    {
        var actions = NativeConversationState.Languages.Select(language =>
            {
                var active = language == state.TargetLanguage;
                var button = active
                    ? Views.Button(Views.HStack(Views.Text(language), Views.Image("checkmark")),
                        () => Fire(token => _presenter.SelectLanguageAsync(language, token)))
                    : Views.Button(language, () => Fire(token => _presenter.SelectLanguageAsync(language, token)));
                return button.WithAccessibilitySelected(active)
                    .WithAccessibilityIdentifier($"{NativeConversationAccess.LanguageOption}.{language}");
            })
            .ToArray();
        // Source: translate icon, with the language name from md widths up.
        SwiftView label = style.ViewportWidth >= 768
            ? Views.HStack(Views.Image("translate"), Views.Text(state.TargetLanguage))
            : Views.Image("translate");
        return Views.Menu(label, actions)
            .WithAccessibilityLabel($"{text(NativeConversationText.ChangeLanguage)}, {state.TargetLanguage}")
            .WithAccessibilityIdentifier(NativeConversationAccess.Language);
    }

    private void UpdateInput(NativeConversationState state, SourceUiStyle style)
    {
        if (_field is null)
            return;
        // Only presenter-made draft changes (quick phrase, sent) are pushed into the
        // field; typing never round-trips, so IME marked text is never replaced.
        if (_renderedDraftVersion != state.DraftVersion)
        {
            _renderedDraftVersion = state.DraftVersion;
            if (!string.Equals(_field.Text, state.Draft, StringComparison.Ordinal))
                _field.Text = state.Draft;
        }
        var editable = state.CanEdit;
        if (_field.IsEnabled != editable)
            _field.IsEnabled = editable;
        if (_toggle is not null)
            SourceUiStyle.SetCustomButtonAvailability(_toggle, style.Appearance,
                SourceButtonRole.FilledSecondary, editable);
        UpdateSendAvailability(style, _presenter.State.Draft, state.IsWaitingForReply);
    }

    private void UpdateSendAvailability(SourceUiStyle style, string? draft, bool waiting)
    {
        if (_send is not null)
            SourceUiStyle.SetCustomButtonAvailability(_send, style.Appearance, SourceButtonRole.Primary,
                !waiting && !string.IsNullOrWhiteSpace(draft) && _state.Phase == NativePhase.Ready);
    }

    private static void Fill(SwiftView slot, IEnumerable<SwiftView> children)
    {
        var next = children.ToList();
        while (slot.Children.Count > 0)
        {
            var previous = slot.Children[0];
            slot.RemoveAt(0);
            previous.Dispose();
        }
        foreach (var child in next)
            slot.Add(child);
    }

    /// <summary>Source bg-primary bg-opacity-10: the primary color at 10% over the page background.</summary>
    private static string Blend(string foreground, string background, double alpha)
    {
        static (int R, int G, int B) Parse(string hex) => (
            int.Parse(hex.AsSpan(1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture),
            int.Parse(hex.AsSpan(3, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture),
            int.Parse(hex.AsSpan(5, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
        var f = Parse(foreground);
        var b = Parse(background);
        int Mix(int top, int bottom) => (int)Math.Round(top * alpha + bottom * (1 - alpha));
        return string.Create(CultureInfo.InvariantCulture,
            $"#{Mix(f.R, b.R):X2}{Mix(f.G, b.G):X2}{Mix(f.B, b.B):X2}");
    }

    public void Dispose()
    {
        _presenter.StateChanged -= OnStateChanged;
        _presenter.Dispose();
    }
}
