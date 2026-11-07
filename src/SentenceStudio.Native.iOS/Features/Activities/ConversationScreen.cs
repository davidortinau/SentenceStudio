using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.SwiftUI;
using SentenceStudio.Native.iOS.Styling;
using Views = Microsoft.SwiftUI.SwiftUI;

namespace SentenceStudio.Native.iOS.Features.Activities;

/// <summary>
/// SwiftUI Conversation (source Conversation.razor). Toolbar scenario and
/// language menus; persona and learner bubbles with grammar corrections,
/// comprehension and Listen; reply-pending indicator; Playing audio / Stop bar.
/// The composer follows Messages (Captain, 2026-11-06): a round quick-phrases
/// button before a capsule text field, Return sends (no Send button), and the
/// waveform button inside the field starts voice mode. While voice runs, a
/// voice panel (status, input level, End) takes the composer's place and
/// spoken turns join the chat.
///
/// While the layout is unchanged, state updates mutate the mounted slots in
/// place, so the mounted text field (and any Korean IME composition or
/// keyboard focus in it) survives replies and audio events. The chat is only
/// rebuilt when its content changed, so input-level updates stay cheap.
/// </summary>
internal sealed class ConversationScreen : IActivityScreen
{
    private readonly ActivityScreenHost _host;
    private readonly NativeConversationPresenter _presenter;
    private NativeConversationState _state = new(NativePhase.Loading, IsStarting: true);
    private string? _layoutKey;
    private int _chatKey;
    private int _renderedDraftVersion = -1;
    private SwiftView? _top;
    private SwiftView? _chat;
    private SwiftView? _audio;
    private SwiftView? _phrases;
    private SwiftView? _field;
    private SwiftView? _toggle;
    private SwiftView? _voiceStart;
    private SwiftView? _voiceStatus;
    private SwiftView? _voiceLevel;

    public ConversationScreen(ActivityScreenHost host)
    {
        _host = host;
        _presenter = host.Services.GetRequiredService<NativeConversationPresenter>();
        _presenter.StateChanged += OnStateChanged;
    }

    public string TitleKey => NativeConversationText.Title;

    public NativeMessage? Toast => NativeMessages.LatestToast(_state.Notices);

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
                Notices = [new(NativeMessageKind.Error, NativeText.UnexpectedError)]
            };
        }
        _host.Publish();
    }

    private string LayoutKey(NativeConversationState state, SourceUiStyle style) =>
        string.Join('|', state.Phase, state.Destination, state.TargetLanguage, state.ActiveScenarioId,
            state.Scenarios.IsDefault ? 0 : state.Scenarios.Length, state.IsVoiceOn, state.IsVoiceAvailable,
            style.GetHashCode(), Math.Round(style.ViewportWidth));

    /// <summary>Changes when anything a bubble shows changes; the mic level alone leaves it alone.</summary>
    private static int ChatKey(NativeConversationState state)
    {
        var hash = new HashCode();
        hash.Add(state.IsWaitingForReply);
        foreach (var message in state.Messages.IsDefault ? [] : state.Messages)
        {
            hash.Add(message.Index);
            hash.Add(message.IsUser);
            hash.Add(message.Author);
            hash.Add(message.Text);
            hash.Add(message.IsVoice);
            hash.Add(message.Comprehension);
            foreach (var correction in message.Corrections.IsDefault ? [] : message.Corrections)
                hash.Add(correction);
            foreach (var note in message.ToolNotes.IsDefault ? [] : message.ToolNotes)
            {
                hash.Add(note.Term);
                foreach (var match in note.Matches.IsDefault ? [] : note.Matches)
                    hash.Add(match);
            }
        }
        return hash.ToHashCode();
    }

    public bool TryUpdateInPlace(SourceUiStyle style)
    {
        var state = _state;
        if (_layoutKey is null || _layoutKey != LayoutKey(state, style) || state.Phase != NativePhase.Ready
            || _top is null || _chat is null || _audio is null)
            return false;
        if (state.IsVoiceOn ? _voiceStatus is null || _voiceLevel is null : _phrases is null || _field is null || _toggle is null)
            return false;
        var text = _host.Text;
        Fill(_top, TopRows(state, style, text));
        var chatKey = ChatKey(state);
        if (chatKey != _chatKey)
        {
            _chatKey = chatKey;
            Fill(_chat, ChatRows(state, style, text));
        }
        Fill(_audio, AudioRows(state, style, text));
        if (state.IsVoiceOn)
        {
            Fill(_voiceStatus!, VoiceStatusRows(state, style, text));
            _voiceLevel!.Value = state.Voice == NativeConversationVoice.Listening ? state.MicLevel : 0;
        }
        else
        {
            Fill(_phrases!, PhraseRows(state, style, text));
            UpdateInput(state);
        }
        return true;
    }

    public SwiftView Render(SourceUiStyle style)
    {
        var text = _host.Text;
        var state = _state;
        _top = _chat = _audio = _phrases = _field = _toggle = _voiceStart = _voiceStatus = _voiceLevel = null;
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
        if (state.Phase is NativePhase.Blocked || state.Destination != NativeDestination.Activity)
            return page;
        return state.Phase == NativePhase.Ready && !state.Scenarios.IsDefaultOrEmpty
            ? Views.Toolbar(page, ScenarioMenu(state, text), LanguageMenu(state, style, text))
            : Views.Toolbar(page, LanguageMenu(state, style, text));
    }

    private SwiftView ReadyPage(NativeConversationState state, SourceUiStyle style,
        Func<string, string> text, double gutter)
    {
        _top = Views.VStack(TopRows(state, style, text).ToArray())
            .WithPadding(8, gutter, 0, gutter)
            .WithFlexibleWidth();
        _top.Spacing = 8;
        _chatKey = ChatKey(state);
        _chat = Views.VStack(ChatRows(state, style, text).ToArray())
            .WithPadding(8, gutter, 8, gutter)
            .WithFlexibleWidth()
            .WithAccessibilityIdentifier(NativeConversationAccess.Messages);
        _chat.Spacing = 8;
        _audio = Views.VStack(AudioRows(state, style, text).ToArray()).WithFlexibleWidth();

        var composer = state.IsVoiceOn
            ? VoicePanel(state, style, text)
            : Composer(state, style, text);
        var footer = Views.VStack(_audio, composer)
            .WithPadding(8, gutter, 8, gutter)
            .WithFlexibleWidth()
            .WithBackgroundColor(style.Appearance.BodyBackground.Light, style.Appearance.BodyBackground.Dark);
        footer.Spacing = 8;

        var chat = Views.BottomInset(Views.Scroll(_chat).WithScrollAnchoredToBottom(), footer);
        var page = Views.VStack(_top, chat).WithFlexibleWidth()
            .WithAccessibilityIdentifier(NativeConversationAccess.Screen);
        page.Spacing = 0;
        return page;
    }

    /// <summary>
    /// Messages-style composer: quick phrases above when open; a round quick-phrases button, then
    /// the capsule with the field and the waveform button. Return sends.
    /// </summary>
    private SwiftView Composer(NativeConversationState state, SourceUiStyle style, Func<string, string> text)
    {
        _phrases = Views.VStack(PhraseRows(state, style, text).ToArray()).WithFlexibleWidth();
        var field = style.ComposerField(Views.TextField(text(NativeConversationText.TypeMessage), state.Draft,
                value => _presenter.SetDraft(value)))
            .WithFlexibleWidth()
            .WithAccessibilityLabel(text(NativeConversationText.TypeMessage))
            .WithAccessibilityIdentifier(NativeConversationAccess.Input)
            .OnSubmit(() => Fire(_presenter.SendAsync));
        _field = field;
        _renderedDraftVersion = state.DraftVersion;
        // Source: chat-quote toggle (its title reuses the placeholder text; native names it).
        _toggle = style.ComposerIconButton("quote.bubble", () => Fire(_presenter.ToggleQuickPhrasesAsync),
            text(NativeConversationText.QuickPhrases), NativeConversationAccess.QuickPhrasesToggle);
        var capsuleRow = new List<SwiftView> { field };
        if (state.IsVoiceAvailable)
        {
            var appearance = style.Appearance;
            _voiceStart = Views.Button(Views.Image("waveform")
                        .WithFixedSymbolSize(19 * appearance.Selection.FontScale)
                        .WithForegroundColor(appearance.SecondaryText.Light, appearance.SecondaryText.Dark),
                    () => Fire(_presenter.StartVoiceAsync))
                .WithButtonStyle(SwiftButtonStyle.Plain)
                .WithMinimumHeight(SourceUiStyle.ComposerHeight - 8)
                .WithAccessibilityLabel(text(NativeConversationText.VoiceStart))
                .WithAccessibilityIdentifier(NativeConversationAccess.VoiceStart);
            capsuleRow.Add(_voiceStart);
        }
        var capsuleContent = Views.HStack(capsuleRow.ToArray()).WithFlexibleWidth();
        capsuleContent.Spacing = 8;
        var row = Views.HStack(_toggle, style.ComposerCapsule(capsuleContent)).WithFlexibleWidth();
        row.Spacing = 8;
        var composer = Views.VStack(_phrases, row).WithFlexibleWidth();
        composer.Spacing = 8;
        UpdateInput(state);
        return composer;
    }

    /// <summary>Voice mode: what is happening (connecting, listening, partner speaking), the input level, and End.</summary>
    private SwiftView VoicePanel(NativeConversationState state, SourceUiStyle style, Func<string, string> text)
    {
        _voiceStatus = Views.HStack(VoiceStatusRows(state, style, text).ToArray());
        _voiceLevel = style.Progress(Views.Progress(
                state.Voice == NativeConversationVoice.Listening ? state.MicLevel : 0, 1, 4,
                text(NativeConversationText.VoiceListening)), completion: false)
            .WithAccessibilityIdentifier(NativeConversationAccess.VoiceLevel);
        var middle = Views.VStack(_voiceStatus, _voiceLevel).WithFlexibleWidth(alignment: SwiftHorizontalAlignment.Leading);
        middle.Spacing = 6;
        var end = style.LabelButton(
                Views.HStack(Views.Image("stop.fill"), Views.Text(text(NativeConversationText.VoiceEnd))),
                () => Fire(_presenter.StopVoiceAsync), SourceButtonRole.FilledSecondary, 12)
            .WithAccessibilityLabel(text(NativeConversationText.VoiceEnd))
            .WithAccessibilityIdentifier(NativeConversationAccess.VoiceStop);
        var row = Views.HStack(middle, end).WithFlexibleWidth();
        row.Spacing = 12;
        return row.WithPadding(8, 16, 8, 8)
            .WithRoundedSurface(style.Appearance.CardSurface.Light, style.Appearance.CardSurface.Dark,
                24, style.Appearance.Border.Light, style.Appearance.Border.Dark, 1)
            .WithAccessibilityIdentifier(NativeConversationAccess.VoicePanel);
    }

    private IEnumerable<SwiftView> VoiceStatusRows(NativeConversationState state, SourceUiStyle style,
        Func<string, string> text)
    {
        var status = state.Voice switch
        {
            NativeConversationVoice.Connecting => text(NativeConversationText.VoiceConnecting),
            NativeConversationVoice.PartnerSpeaking => string.Format(CultureInfo.CurrentCulture,
                text(NativeConversationText.VoiceSpeaking), state.PartnerName),
            _ => text(NativeConversationText.VoiceListening),
        };
        var primary = style.Appearance.Primary;
        yield return state.Voice == NativeConversationVoice.Connecting
            ? Views.IndeterminateProgress(status)
            : Views.Image(state.Voice == NativeConversationVoice.PartnerSpeaking ? "speaker.wave.2.fill" : "mic.fill")
                .WithForegroundColor(primary.Light, primary.Dark);
        yield return style.Text(Views.Text(status), SourceTextRole.Body2)
            .WithAccessibilityIdentifier(NativeConversationAccess.VoiceStatus);
    }

    private IEnumerable<SwiftView> TopRows(NativeConversationState state, SourceUiStyle style,
        Func<string, string> text)
    {
        if (!state.Notices.IsDefaultOrEmpty)
            if (Notices(state, style, text) is { } notices)
                yield return notices;
    }

    /// <summary>Scenario: a toolbar menu (Captain, 2026-11-06) instead of the source picker above the chat.</summary>
    private SwiftView ScenarioMenu(NativeConversationState state, Func<string, string> text)
    {
        var selected = state.ActiveScenarioId ?? state.Scenarios[0].Id;
        var actions = state.Scenarios.Select(item =>
            {
                var active = item.Id == selected;
                var id = item.Id;
                var button = active
                    ? Views.Button(Views.HStack(Views.Text(item.DisplayName), Views.Image("checkmark")),
                        () => Fire(token => _presenter.SelectScenarioAsync(id, token)))
                    : Views.Button(item.DisplayName, () => Fire(token => _presenter.SelectScenarioAsync(id, token)));
                return button.WithAccessibilitySelected(active)
                    .WithAccessibilityIdentifier($"{NativeConversationAccess.Scenario}.{id}");
            })
            .ToArray();
        var name = state.Scenarios.FirstOrDefault(item => item.Id == selected)?.DisplayName ?? string.Empty;
        return Views.Menu(Views.Image("theatermasks"), actions)
            .WithAccessibilityLabel($"{text(NativeConversationText.Scenario)}, {name}")
            .WithAccessibilityIdentifier(NativeConversationAccess.Scenario);
    }

    /// <summary>Errors and warnings stay inline until the next action; success and info are the scene's toast.</summary>
    private SwiftView? Notices(NativeConversationState state, SourceUiStyle style, Func<string, string> text)
    {
        return style.MessageBanners(state.Notices, text, index => $"{NativeConversationAccess.Notice}.{index}",
            () => Fire(_presenter.DismissNoticesAsync), NativeConversationAccess.DismissNotices,
            text(NativeConversationText.Dismiss));
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
        var rows = new List<SwiftView>();
        var noteIndex = 0;
        foreach (var note in message.ToolNotes.IsDefault ? [] : message.ToolNotes)
        {
            var noteRow = Views.HStack(
                    Views.Image("book.closed")
                        .WithForegroundColor(appearance.SecondaryText.Light, appearance.SecondaryText.Dark),
                    style.Text(Views.Text(ToolNoteText(note, text)), SourceTextRole.Caption1,
                        SourceTextTone.Secondary))
                .WithAccessibilityIdentifier($"{id}.tool.{noteIndex++}");
            noteRow.Spacing = 4;
            rows.Add(noteRow);
        }
        SwiftView body = message.IsTranscribing
            ? style.Text(Views.Text(text(NativeConversationText.VoiceTranscribing)), SourceTextRole.Body1,
                SourceTextTone.Secondary)
            : style.Text(Views.Text(message.Text), SourceTextRole.Body1);
        if (message.IsVoice)
        {
            // Source demo: a small microphone marks a spoken turn.
            var spoken = Views.HStack(
                Views.Image("mic.fill")
                    .WithForegroundColor(appearance.SecondaryText.Light, appearance.SecondaryText.Dark)
                    .WithAccessibilityLabel(text(NativeConversationText.Voice)),
                body);
            spoken.Spacing = 6;
            body = spoken;
        }
        rows.Add(body.WithAccessibilityIdentifier($"{id}.text"));
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
        if (message.CanListen)
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

    /// <summary>"Checked your vocabulary: 커피 → 커피 = coffee", or that the word is not in the list.</summary>
    private static string ToolNoteText(NativeConversationToolNote note, Func<string, string> text)
    {
        var result = note.Matches.IsDefaultOrEmpty
            ? string.Format(CultureInfo.CurrentCulture, text(NativeConversationText.VoiceToolNotFound), note.Term)
            : $"{note.Term} → {string.Join("; ", note.Matches)}";
        return string.Format(CultureInfo.CurrentCulture, text(NativeConversationText.VoiceToolNote), result);
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

    private void UpdateInput(NativeConversationState state)
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
        if (_toggle is not null && _toggle.IsEnabled != editable)
            _toggle.IsEnabled = editable;
        if (_voiceStart is not null && _voiceStart.IsEnabled != state.CanStartVoice)
            _voiceStart.IsEnabled = state.CanStartVoice;
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
