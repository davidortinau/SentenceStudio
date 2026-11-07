using System.Globalization;
using AndroidX.Compose;
using AndroidX.Compose.Runtime;
using AndroidX.Compose.UI.Text.Input;
using Microsoft.Extensions.DependencyInjection;
using SentenceStudio.Native;

namespace SentenceStudio.Native.Android;

/// <summary>
/// Compose Conversation (source Conversation.razor): header scenario and
/// language menus, persona and learner bubbles (grammar corrections,
/// comprehension, Listen), reply indicator, Playing audio / Stop bar, and a
/// Messages-style composer pinned below the scrolling chat (departures 46-47,
/// matching iOS): a round quick-phrases button before a capsule field, and
/// the keyboard's Send action sends (no Send button). The field
/// binds a Compose TextFieldValue state, so typing (including Korean IME
/// composition) never goes through a presenter publish; only presenter-made
/// draft changes (quick phrase, sent) are pushed back into it.
/// </summary>
internal sealed partial class AndroidSliceShell
{
    private NativeConversationPresenter? _conversation;
    private bool _conversationLanguageMenuOpen;
    private bool _conversationScenarioMenuOpen;
    private int _conversationDraftVersion = -1;
    private FocusRequester? _conversationFocus;
    /// <summary>
    /// Set when the keyboard's Send action sends. The field is disabled while the reply is pending,
    /// which drops focus and closes the keyboard; with no Send button the field takes focus back
    /// once it is editable again.
    /// </summary>
    private bool _conversationRefocus;

    internal NativeConversationState Conversation { get; private set; } =
        new(NativePhase.Loading, IsStarting: true);

    internal MutableState<TextFieldValue> ConversationDraft { get; } =
        new(ComposeExtensions.NewTextFieldValue(string.Empty));

    private async Task OpenConversationAsync(NativeActivityLaunch launch, CancellationToken cancellationToken)
    {
        if (ActivityServices is not { } services)
            return;
        var presenter = services.GetRequiredService<NativeConversationPresenter>();
        presenter.StateChanged += OnConversationChanged;
        _conversation = presenter;
        Publish(() => Conversation = new(NativePhase.Loading, IsStarting: true));
        // Returns once scenarios and vocabulary load; the opening line arrives
        // through StateChanged, so the shell's busy gate never waits on the AI.
        await presenter.OpenAsync(launch, cancellationToken);
        Publish(() =>
        {
            if (ReferenceEquals(_conversation, presenter))
                ApplyConversation(presenter.State);
        });
    }

    private async Task ExitConversationAsync(CancellationToken cancellationToken)
    {
        if (_conversation is { } presenter)
            await presenter.ExitAsync(cancellationToken);
    }

    private void ReleaseConversation()
    {
        if (_conversation is { } presenter)
        {
            presenter.StateChanged -= OnConversationChanged;
            presenter.Dispose();
        }
        _conversation = null;
        _conversationLanguageMenuOpen = false;
        _conversationScenarioMenuOpen = false;
        _conversationRefocus = false;
        _conversationDraftVersion = -1;
        ConversationDraft.Value = ComposeExtensions.NewTextFieldValue(string.Empty);
    }

    /// <summary>After a keyboard send, the field takes focus back once the reply is in and it is editable.</summary>
    private void RememberConversationEffects(IComposer composer)
    {
        _conversationFocus = composer.Remember(() => new FocusRequester());
        var state = Conversation;
        var refocus = _conversation is not null && _conversationRefocus && state.CanEdit
            ? AndroidEffectKey.Of(state.Messages.IsDefault ? 0 : state.Messages.Length, state.DraftVersion)
            : null;
        composer.Effect(refocus, _ =>
        {
            if (refocus is not null && _conversationRefocus && Conversation.CanEdit)
            {
                _conversationRefocus = false;
                RequestFocusQuietly(_conversationFocus);
            }
            return Task.CompletedTask;
        });
    }

    private void OnConversationChanged(NativeConversationState state) =>
        Publish(() => ApplyConversation(state));

    private void ApplyConversation(NativeConversationState state)
    {
        Conversation = state;
        // The scenario menu is only in the header while Ready; do not let it reopen by itself.
        if (state.Phase != NativePhase.Ready)
            _conversationScenarioMenuOpen = false;
        if (state.DraftVersion != _conversationDraftVersion)
        {
            _conversationDraftVersion = state.DraftVersion;
            if (!string.Equals(state.Draft, ConversationDraft.Value.Text, StringComparison.Ordinal))
                ConversationDraft.Value = ComposeExtensions.NewTextFieldValue(state.Draft);
        }
    }

    /// <summary>Conversation commands bypass the shell's busy gate so a slow reply never disables Back; the presenter serializes them.</summary>
    private void ConversationCommand(
        Func<NativeConversationPresenter, CancellationToken, Task<NativeConversationState>> command)
    {
        if (_conversation is not { } presenter)
            return;
        presenter.SetDraft(ConversationDraft.Value.Text);
        _ = RunConversationAsync(presenter, command);
    }

    private async Task RunConversationAsync(NativeConversationPresenter presenter,
        Func<NativeConversationPresenter, CancellationToken, Task<NativeConversationState>> command)
    {
        try
        {
            var state = await command(presenter, _lifetime.Token);
            Publish(() =>
            {
                if (ReferenceEquals(_conversation, presenter))
                    ApplyConversation(state);
            });
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            ShowFailure(ex);
        }
    }

    private ComposableNode RenderConversation(AndroidVisualPalette palette)
    {
        var state = Conversation;
        var header = (TopAppBar)ScreenHeader(Localize(NativeConversationText.Title), palette,
            back: true, backAutomationId: NativeConversationAccess.Back);
        var page = new Column
        {
            Modifier.FillMaxSize().TestTag(NativeConversationAccess.Screen),
        };
        if (state.Phase is NativePhase.Blocked || state.Destination != NativeDestination.Activity)
        {
            page.Add(header);
            page.Add(new Column(Arrangement.SpacedBy(12))
            {
                Modifier.FillMaxWidth()
                    .Padding(horizontal: AndroidVisualTheme.PagePadding, vertical: 16),
                StatusAlert(Localize(state.ErrorKey ?? NativeConversationText.Unavailable), palette,
                    error: true, automationId: NativeConversationAccess.Blocked),
                Action(Localize(NativeConversationText.GoBack), NativeConversationAccess.BlockedBack,
                    ExitActivity, true, primary: false),
            });
            return page;
        }
        header.Actions = state.Phase == NativePhase.Ready && !state.Scenarios.IsDefaultOrEmpty
            ? new Row(Arrangement.SpacedBy(4), Alignment.Vertical.CenterVertically)
            {
                ConversationScenarioMenu(state, palette),
                ConversationLanguageMenu(state, palette),
            }
            : ConversationLanguageMenu(state, palette);
        page.Add(header);
        if (state.Phase is NativePhase.Loading or NativePhase.Idle)
        {
            page.Add(new Column(Arrangement.SpacedBy(12), Alignment.Horizontal.CenterHorizontally)
            {
                Modifier.FillMaxWidth().Weight(1f).Padding(vertical: 48),
                new CircularProgressIndicator
                {
                    Modifier = Modifier.Size(28).TestTag(NativeConversationAccess.Loading)
                },
                Body(Localize(NativeConversationText.Starting), palette, secondary: true),
            });
            return page;
        }

        if (!state.Notices.IsDefaultOrEmpty && ConversationNotices(state, palette) is { } notices)
            page.Add(new Column(Arrangement.SpacedBy(8))
            {
                Modifier.FillMaxWidth()
                    .Padding(horizontal: AndroidVisualTheme.PagePadding, vertical: 8),
                notices,
            });

        var chat = new Column(Arrangement.SpacedBy(8))
        {
            Modifier = Modifier.FillMaxWidth().Weight(1f)
                .VerticalScroll(_quizScroll)
                .Padding(horizontal: AndroidVisualTheme.PagePadding, vertical: 8)
                .TestTag(NativeConversationAccess.Messages),
        };
        foreach (var message in state.Messages.IsDefault ? [] : state.Messages)
            chat.Add(ConversationBubble(message, palette));
        if (state.IsWaitingForReply)
            chat.Add(new Row(Arrangement.Start)
            {
                Modifier.FillMaxWidth().TestTag(NativeConversationAccess.Typing),
                new CircularProgressIndicator { Modifier = Modifier.Size(24) },
            });
        page.Add(chat);

        if (state.IsPlayingAudio)
            page.Add(ConversationAudioBar(palette));
        page.Add(ConversationComposer(state, palette));
        return page;
    }

    /// <summary>Errors and warnings stay inline until the next action; success and info are the shell's snackbar.</summary>
    private ComposableNode? ConversationNotices(NativeConversationState state, AndroidVisualPalette palette) =>
        MessageBanners(state.Notices, palette, index => $"{NativeConversationAccess.Notice}.{index}",
            () => ConversationCommand((presenter, token) => presenter.DismissNoticesAsync(token)),
            NativeConversationAccess.DismissNotices, Localize(NativeConversationText.Dismiss));

    /// <summary>
    /// Scenario: a header menu (theater masks; the current scenario has a check) instead of the
    /// source picker above the chat (departure 46, Captain 2026-11-06).
    /// </summary>
    private ComposableNode ConversationScenarioMenu(NativeConversationState state, AndroidVisualPalette palette)
    {
        var active = state.Scenarios.FirstOrDefault(item => item.Id == state.ActiveScenarioId)
            ?? state.Scenarios[0];
        var selected = active.Id;
        var name = active.DisplayName;
        // The source label ends with a colon ("Scenario:", "시나리오:"); the spoken name drops it.
        var label = $"{Localize(NativeConversationText.Scenario).TrimEnd(':', ' ')}, {name}";
        var button = new IconButton(() => Publish(() => _conversationScenarioMenuOpen = true), enabled: true)
        {
            Modifier = Modifier.TestTag(NativeConversationAccess.Scenario)
                .Semantics(semantics => semantics.ContentDescription(label)),
        };
        button.Add(new Icon(Resource.Drawable.ic_theater_comedy, null) { Modifier = Modifier.Size(24) });
        var menu = PopupMenu(expanded: _conversationScenarioMenuOpen,
            onDismissRequest: () => Publish(() => _conversationScenarioMenuOpen = false));
        foreach (var scenario in state.Scenarios)
        {
            var id = scenario.Id;
            var isActive = id == selected;
            var item = new DropdownMenuItem(
                text: new Text(scenario.DisplayName),
                onClick: () =>
                {
                    Publish(() => _conversationScenarioMenuOpen = false);
                    ConversationCommand((presenter, token) => presenter.SelectScenarioAsync(id, token));
                })
            {
                Modifier = Modifier.TestTag(
                        $"{NativeConversationAccess.Scenario}.{id.ToString(CultureInfo.InvariantCulture)}")
                    .Semantics(semantics => semantics.Selected(isActive)),
            };
            if (isActive)
                item.LeadingIcon = new Icon(Icons.Default.Check, null) { Modifier = Modifier.Size(18), Tint = palette.Primary };
            menu.Add(item);
        }
        return new Box { button, menu };
    }

    /// <summary>Source toolbar language dropdown (Constants.Languages; page-local).</summary>
    private ComposableNode ConversationLanguageMenu(NativeConversationState state, AndroidVisualPalette palette)
    {
        var label = $"{Localize(NativeConversationText.ChangeLanguage)}, {state.TargetLanguage}";
        var button = new TextButton(() => Publish(() => _conversationLanguageMenuOpen = true))
        {
            Modifier = Modifier.TestTag(NativeConversationAccess.Language)
                .Semantics(semantics => semantics.ContentDescription(label)),
        };
        button.Add(new Text(state.TargetLanguage));
        button.Add(new Icon(Icons.Default.ArrowDropDown, null) { Modifier = Modifier.Size(20) });
        var menu = PopupMenu(expanded: _conversationLanguageMenuOpen,
            onDismissRequest: () => Publish(() => _conversationLanguageMenuOpen = false));
        foreach (var language in NativeConversationState.Languages)
        {
            var value = language;
            var item = new DropdownMenuItem(
                text: new Text(value),
                onClick: () =>
                {
                    Publish(() => _conversationLanguageMenuOpen = false);
                    ConversationCommand((presenter, token) => presenter.SelectLanguageAsync(value, token));
                })
            {
                Modifier = Modifier.TestTag($"{NativeConversationAccess.LanguageOption}.{value}")
                    .Semantics(semantics => semantics.Selected(value == state.TargetLanguage)),
            };
            if (value == state.TargetLanguage)
                item.LeadingIcon = new Icon(Icons.Default.Check, null) { Modifier = Modifier.Size(18), Tint = palette.Primary };
            menu.Add(item);
        }
        return new Box { button, menu };
    }

    private ComposableNode ConversationBubble(NativeConversationMessage message, AndroidVisualPalette palette)
    {
        var id = message.AccessibilityId;
        var content = new Column(Arrangement.SpacedBy(4))
        {
            Modifier.Padding(horizontal: 16, vertical: 12),
            Body(message.Text, palette, modifier: Modifier.TestTag($"{id}.text")),
        };
        var index = 0;
        foreach (var correction in message.Corrections.IsDefault ? [] : message.Corrections)
        {
            var original = new Text(correction.Original ?? string.Empty)
            {
                Color = palette.Danger,
                FontSize = new Sp(13),
                Decoration = TextDecoration.LineThrough,
            };
            var corrected = new Text(correction.Corrected ?? string.Empty)
            {
                Color = palette.Success,
                FontSize = new Sp(13),
                FontWeight = FontWeight.Bold,
            };
            var block = new Column(Arrangement.SpacedBy(2))
            {
                Modifier.FillMaxWidth().TestTag($"{id}.correction.{index++}"),
                new FlowRow
                {
                    original,
                    new Text(" → ") { Color = palette.Text, FontSize = new Sp(13) },
                    corrected,
                },
            };
            if (!string.IsNullOrEmpty(correction.Explanation))
                block.Add(new Text(correction.Explanation) { Color = palette.SecondaryText, FontSize = new Sp(13) });
            content.Add(block);
        }
        if (message.ShowsComprehension)
            content.Add(new Text(string.Format(CultureInfo.CurrentCulture,
                Localize(NativeConversationText.Comprehension), (int)message.Comprehension))
            {
                Modifier = Modifier.TestTag($"{id}.comprehension"),
                Color = palette.SecondaryText,
                FontSize = new Sp(13),
            });
        if (message.CanListen)
        {
            var listenLabel = Localize(NativeConversationText.Listen);
            var index2 = message.Index;
            var listen = new TextButton(
                () => ConversationCommand((presenter, token) => presenter.ListenAsync(index2, token)))
            {
                Modifier = Modifier.TestTag($"{id}.listen")
                    .Semantics(semantics => semantics.ContentDescription(listenLabel)),
            };
            listen.Add(new Icon(Icons.Default.PlayArrow, null) { Modifier = Modifier.Size(18) });
            listen.Add(new Text(listenLabel));
            content.Add(listen);
        }
        var bubble = new Surface
        {
            Modifier = Modifier.WidthIn(max: 300).TestTag(id),
            Shape = Shape.RoundedCorners(16),
            Color = message.IsUser ? palette.Elevated : palette.Card,
        };
        bubble.Add(content);
        return new Row(message.IsUser ? Arrangement.End : Arrangement.Start)
        {
            Modifier.FillMaxWidth(),
            bubble,
        };
    }

    private ComposableNode ConversationAudioBar(AndroidVisualPalette palette)
    {
        var stopLabel = Localize(NativeConversationText.Stop);
        var stop = new OutlinedButton(
            onClick: () => ConversationCommand((presenter, token) => presenter.StopAudioAsync(token)),
            enabled: true)
        {
            Modifier = Modifier.TestTag(NativeConversationAccess.AudioStop)
                .Semantics(semantics => semantics.ContentDescription(stopLabel)),
            Shape = Shape.RoundedCorners(AndroidVisualTheme.ControlRadius),
        };
        stop.Add(new Text(stopLabel));
        return new Row(Arrangement.SpacedBy(8), Alignment.Vertical.CenterVertically)
        {
            Modifier.FillMaxWidth()
                .Padding(horizontal: AndroidVisualTheme.PagePadding, vertical: 4)
                .TestTag(NativeConversationAccess.Audio),
            stop,
            Body(Localize(NativeConversationText.PlayingAudio), palette, secondary: true),
        };
    }

    /// <summary>
    /// Messages-style composer (departure 47): quick phrases above when open; a round
    /// quick-phrases button, then the capsule field. The keyboard's Send action sends.
    /// </summary>
    private ComposableNode ConversationComposer(NativeConversationState state, AndroidVisualPalette palette)
    {
        var editable = state.CanEdit;
        var composer = new Column(Arrangement.SpacedBy(8))
        {
            Modifier = Modifier.FillMaxWidth()
                .Padding(horizontal: AndroidVisualTheme.PagePadding, vertical: 8)
                .TestTag(NativeConversationAccess.Composer),
        };
        if (state.ShowQuickPhrases)
        {
            var phrases = new FlowRow { Modifier.FillMaxWidth() };
            var phraseIndex = 0;
            foreach (var phrase in NativeConversationState.QuickPhrases)
            {
                var index = phraseIndex++;
                var button = new OutlinedButton(
                    onClick: () => ConversationCommand((presenter, token) => presenter.UseQuickPhraseAsync(index, token)),
                    enabled: editable)
                {
                    Modifier = Modifier.Padding(end: 6, bottom: 6)
                        .TestTag($"{NativeConversationAccess.QuickPhrase}.{index}"),
                    Shape = Shape.RoundedCorners(AndroidVisualTheme.ControlRadius),
                };
                button.Add(new Text(phrase));
                phrases.Add(button);
            }
            composer.Add(phrases);
        }
        // Source: chat-quote toggle (its title reuses the placeholder text; native names it).
        var phrasesLabel = Localize(NativeConversationText.QuickPhrases);
        var toggle = new OutlinedIconToggleButton(state.ShowQuickPhrases,
            _ => ConversationCommand((presenter, token) => presenter.ToggleQuickPhrasesAsync(token)),
            enabled: editable)
        {
            Modifier = Modifier.TestTag(NativeConversationAccess.QuickPhrasesToggle)
                .Semantics(semantics => semantics.ContentDescription(phrasesLabel)),
            Shape = Shape.Circle(),
        };
        toggle.Add(new Icon(Resource.Drawable.ic_format_quote, null) { Modifier = Modifier.Size(20) });
        var placeholder = Localize(NativeConversationText.TypeMessage);
        var keyboard = KeyboardOptionsCompanion.Default;
        var fieldModifier = Modifier.Weight(1f);
        if (_conversationFocus is not null)
            fieldModifier = fieldModifier.FocusRequester(_conversationFocus);
        var field = new OutlinedTextField(ConversationDraft, enabled: editable, singleLine: true)
        {
            Modifier = fieldModifier.TestTag(NativeConversationAccess.Input)
                .Semantics(semantics => semantics.ContentDescription(placeholder)),
            Placeholder = new Text(placeholder),
            Shape = Shape.RoundedPercent(50),
            KeyboardOptions = keyboard.Copy(
                keyboard.Capitalization, keyboard.AutoCorrectEnabled,
                AndroidX.Compose.KeyboardType.Text,
                AndroidX.Compose.ImeAction.Send,
                keyboard.PlatformImeOptions, keyboard.ShowKeyboardOnFocus,
                keyboard.HintLocales),
            // The presenter ignores an empty draft or a send while a reply is pending.
            KeyboardActions = KeyboardActionsHelper.Create(onSend: () =>
            {
                _conversationRefocus = true;
                ConversationCommand((presenter, token) => presenter.SendAsync(token));
            }),
        };
        composer.Add(new Row(Arrangement.SpacedBy(8), Alignment.Vertical.CenterVertically)
        {
            Modifier.FillMaxWidth(),
            toggle,
            field,
        });
        return composer;
    }
}
