using System.Globalization;
using AndroidX.Compose;
using AndroidX.Compose.Runtime;
using AndroidX.Compose.UI.Text.Input;
using Microsoft.Extensions.DependencyInjection;
using SentenceStudio.Native;

namespace SentenceStudio.Native.Android;

/// <summary>
/// Compose Conversation (source Conversation.razor): header language menu,
/// scenario dropdown, persona and learner bubbles (grammar corrections,
/// comprehension, Listen), reply indicator, Playing audio / Stop bar, and the
/// input card with quick phrases pinned below the scrolling chat. The field
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
        _conversationDraftVersion = -1;
        ConversationDraft.Value = ComposeExtensions.NewTextFieldValue(string.Empty);
    }

    private void OnConversationChanged(NativeConversationState state) =>
        Publish(() => ApplyConversation(state));

    private void ApplyConversation(NativeConversationState state)
    {
        Conversation = state;
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
        header.Actions = ConversationLanguageMenu(state, palette);
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

        var top = new Column(Arrangement.SpacedBy(8))
        {
            Modifier = Modifier.FillMaxWidth()
                .Padding(horizontal: AndroidVisualTheme.PagePadding, vertical: 8),
        };
        if (!state.Notices.IsDefaultOrEmpty)
            top.Add(ConversationNotices(state, palette));
        if (!state.Scenarios.IsDefaultOrEmpty)
            top.Add(ConversationScenario(state, palette));
        page.Add(top);

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
        page.Add(ConversationInputCard(state, palette));
        return page;
    }

    private ComposableNode ConversationNotices(NativeConversationState state, AndroidVisualPalette palette)
    {
        var list = new Column(Arrangement.SpacedBy(6))
        {
            Modifier.Weight(1f).TestTag(NativeConversationAccess.Notices),
        };
        var index = 0;
        foreach (var notice in state.Notices)
            list.Add(StatusAlert(Localize(notice.Key), palette,
                error: notice.Kind == NativeConversationNoticeKind.Error,
                automationId: $"{NativeConversationAccess.Notice}.{index++}"));
        var dismissLabel = Localize(NativeConversationText.Dismiss);
        var dismiss = new IconButton(
            () => ConversationCommand((presenter, token) => presenter.DismissNoticesAsync(token)))
        {
            Modifier = Modifier.TestTag(NativeConversationAccess.DismissNotices)
                .Semantics(semantics => semantics.ContentDescription(dismissLabel)),
        };
        dismiss.Add(new Icon(Icons.Default.Close, dismissLabel)
        {
            Modifier = Modifier.Size(20),
            Tint = palette.Text,
        });
        return new Row(Arrangement.SpacedBy(8))
        {
            Modifier.FillMaxWidth(),
            list,
            dismiss,
        };
    }

    /// <summary>Source scenario select: label plus a dropdown of the scenarios.</summary>
    private ComposableNode ConversationScenario(NativeConversationState state, AndroidVisualPalette palette)
    {
        var active = state.Scenarios.FirstOrDefault(item => item.Id == state.ActiveScenarioId)
            ?? state.Scenarios[0];
        var label = Localize(NativeConversationText.Scenario);
        var button = new TextButton(() => Publish(() => _conversationScenarioMenuOpen = true))
        {
            Modifier = Modifier.TestTag(NativeConversationAccess.Scenario)
                .Semantics(semantics => semantics.ContentDescription($"{label} {active.DisplayName}")),
        };
        button.Add(new Text(active.DisplayName));
        button.Add(new Icon(Icons.Default.ArrowDropDown, null) { Modifier = Modifier.Size(20) });
        var menu = new DropdownMenu(expanded: _conversationScenarioMenuOpen,
            onDismissRequest: () => Publish(() => _conversationScenarioMenuOpen = false));
        foreach (var scenario in state.Scenarios)
        {
            var id = scenario.Id;
            menu.Add(new DropdownMenuItem(
                text: new Text(scenario.DisplayName),
                onClick: () =>
                {
                    Publish(() => _conversationScenarioMenuOpen = false);
                    ConversationCommand((presenter, token) => presenter.SelectScenarioAsync(id, token));
                })
            {
                Modifier = Modifier.TestTag(
                    $"{NativeConversationAccess.Scenario}.{id.ToString(CultureInfo.InvariantCulture)}"),
            });
        }
        return new Row(Arrangement.SpacedBy(8), Alignment.Vertical.CenterVertically)
        {
            Modifier.FillMaxWidth(),
            Body(label, palette, secondary: true),
            new Box { button, menu },
        };
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
        var menu = new DropdownMenu(expanded: _conversationLanguageMenuOpen,
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
                item.LeadingIcon = new Icon(Icons.Default.Check, null) { Modifier = Modifier.Size(18) };
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
        if (!message.IsUser)
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

    private ComposableNode ConversationInputCard(NativeConversationState state, AndroidVisualPalette palette)
    {
        var editable = state.CanEdit;
        var hasText = !string.IsNullOrWhiteSpace(ConversationDraft.Value.Text);
        var card = new Column(Arrangement.SpacedBy(8))
        {
            Modifier = Modifier.FillMaxWidth()
                .Padding(horizontal: AndroidVisualTheme.PagePadding, vertical: 8)
                .TestTag("native.conversation.input-card"),
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
            card.Add(phrases);
        }
        var placeholder = Localize(NativeConversationText.TypeMessage);
        var keyboard = KeyboardOptionsCompanion.Default;
        var field = new OutlinedTextField(ConversationDraft, enabled: editable, singleLine: true)
        {
            Modifier = Modifier.Weight(1f).TestTag(NativeConversationAccess.Input)
                .Semantics(semantics => semantics.ContentDescription(placeholder)),
            Label = new Text(placeholder),
            KeyboardOptions = keyboard.Copy(
                keyboard.Capitalization, keyboard.AutoCorrectEnabled,
                AndroidX.Compose.KeyboardType.Text,
                AndroidX.Compose.ImeAction.Send,
                keyboard.PlatformImeOptions, keyboard.ShowKeyboardOnFocus,
                keyboard.HintLocales),
            KeyboardActions = KeyboardActionsHelper.Create(onSend: () =>
            {
                if (!string.IsNullOrWhiteSpace(ConversationDraft.Value.Text))
                    ConversationCommand((presenter, token) => presenter.SendAsync(token));
            }),
        };
        var phrasesLabel = Localize(NativeConversationText.QuickPhrases);
        var toggle = new IconButton(
            () => ConversationCommand((presenter, token) => presenter.ToggleQuickPhrasesAsync(token)),
            enabled: editable)
        {
            Modifier = Modifier.TestTag(NativeConversationAccess.QuickPhrasesToggle)
                .Semantics(semantics =>
                {
                    semantics.ContentDescription(phrasesLabel);
                    semantics.Selected(state.ShowQuickPhrases);
                }),
        };
        toggle.Add(new Icon(Icons.AutoMirrored.Default.List, phrasesLabel)
        {
            Modifier = Modifier.Size(24),
            Tint = palette.Text,
        });
        var sendLabel = Localize(NativeConversationText.Send);
        var send = new AndroidX.Compose.Button(
            onClick: () => ConversationCommand((presenter, token) => presenter.SendAsync(token)),
            enabled: editable && hasText)
        {
            Modifier = Modifier.TestTag(NativeConversationAccess.Send)
                .Semantics(semantics => semantics.ContentDescription(sendLabel)),
            Shape = Shape.RoundedCorners(AndroidVisualTheme.ControlRadius),
        };
        send.Add(new Text(sendLabel));
        card.Add(new Row(Arrangement.SpacedBy(8), Alignment.Vertical.CenterVertically)
        {
            Modifier.FillMaxWidth(),
            field,
            toggle,
            send,
        });
        return card;
    }
}
