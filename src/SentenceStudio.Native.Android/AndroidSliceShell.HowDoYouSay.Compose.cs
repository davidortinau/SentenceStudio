using System.Globalization;
using AndroidX.Compose;
using AndroidX.Compose.Runtime;
using AndroidX.Compose.UI.Text.Input;
using Microsoft.Extensions.DependencyInjection;
using SentenceStudio.Native;

namespace SentenceStudio.Native.Android;

/// <summary>
/// Compose How Do You Say (source HowDoYouSay.razor): the input card (label,
/// phrase field that speaks on Done, voice dropdown when voices were found,
/// full-width Speak button with a Generating spinner), the source toast as an
/// inline notice, and the History list with play/pause and delete. Commands
/// and the open itself bypass the shell's busy gate so Back stays available
/// while voices load or speech is generated.
/// </summary>
internal sealed partial class AndroidSliceShell
{
    private NativeHowDoYouSayPresenter? _howDoYouSay;
    private bool _howDoYouSayVoiceMenu;
    private string _howDoYouSayAppliedInput = string.Empty;

    internal NativeHowDoYouSayState HowDoYouSay { get; private set; } = new(NativePhase.Loading);

    internal MutableState<TextFieldValue> HowDoYouSayInput { get; } =
        new(ComposeExtensions.NewTextFieldValue(string.Empty));

    private Task OpenHowDoYouSayAsync(NativeActivityLaunch launch, CancellationToken cancellationToken)
    {
        if (_scope is null)
            return Task.CompletedTask;
        var presenter = _scope.ServiceProvider.GetRequiredService<NativeHowDoYouSayPresenter>();
        presenter.StateChanged += OnHowDoYouSayChanged;
        _howDoYouSay = presenter;
        Publish(() =>
        {
            _howDoYouSayVoiceMenu = false;
            ApplyHowDoYouSay(new(NativePhase.Loading));
        });
        // Not awaited: the voice list can take the speech timeout, and Back must stay enabled.
        _ = RunHowDoYouSayAsync(presenter, (p, token) => p.OpenAsync(launch, token));
        return Task.CompletedTask;
    }

    private async Task ExitHowDoYouSayAsync(CancellationToken cancellationToken)
    {
        if (_howDoYouSay is { } presenter)
            await presenter.ExitAsync(cancellationToken);
    }

    private void ReleaseHowDoYouSay()
    {
        if (_howDoYouSay is { } presenter)
        {
            presenter.StateChanged -= OnHowDoYouSayChanged;
            presenter.Dispose();
        }
        _howDoYouSay = null;
        _howDoYouSayVoiceMenu = false;
    }

    private void OnHowDoYouSayChanged(NativeHowDoYouSayState state) =>
        Publish(() => ApplyHowDoYouSay(state));

    /// <summary>
    /// Replaces the field only when the presenter changed its input (cleared after
    /// Speak), so an unrelated update such as playback ending never interrupts typing.
    /// </summary>
    private void ApplyHowDoYouSay(NativeHowDoYouSayState state)
    {
        HowDoYouSay = state;
        if (!string.Equals(_howDoYouSayAppliedInput, state.Input, StringComparison.Ordinal))
        {
            _howDoYouSayAppliedInput = state.Input;
            if (!string.Equals(HowDoYouSayInput.Value.Text ?? string.Empty, state.Input,
                    StringComparison.Ordinal))
                HowDoYouSayInput.Value = ComposeExtensions.NewTextFieldValue(state.Input);
        }
    }

    private void HowDoYouSayCommand(
        Func<NativeHowDoYouSayPresenter, CancellationToken, Task<NativeHowDoYouSayState>> command)
    {
        if (_howDoYouSay is not { } presenter)
            return;
        presenter.SetInput(HowDoYouSayInput.Value.Text);
        _ = RunHowDoYouSayAsync(presenter, command);
    }

    private async Task RunHowDoYouSayAsync(NativeHowDoYouSayPresenter presenter,
        Func<NativeHowDoYouSayPresenter, CancellationToken, Task<NativeHowDoYouSayState>> command)
    {
        try
        {
            var state = await command(presenter, _lifetime.Token);
            Publish(() =>
            {
                if (ReferenceEquals(_howDoYouSay, presenter))
                    ApplyHowDoYouSay(state);
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

    private void ToggleHowDoYouSayVoiceMenu(bool open) =>
        Publish(() => _howDoYouSayVoiceMenu = open);

    private ComposableNode RenderHowDoYouSay(AndroidVisualPalette palette)
    {
        var state = HowDoYouSay;
        var viewport = new Column(Arrangement.SpacedBy(12))
        {
            Modifier = Modifier.FillMaxWidth().Weight(1f)
                .VerticalScroll(_quizScroll)
                .Padding(horizontal: AndroidVisualTheme.PagePadding, vertical: 16)
                .TestTag(NativeHowDoYouSayAccess.Screen),
        };
        switch (state.Phase)
        {
            case NativePhase.Idle:
            case NativePhase.Loading:
                viewport.Add(new CircularProgressIndicator
                {
                    Modifier = Modifier.Size(28).TestTag(NativeHowDoYouSayAccess.Loading)
                });
                break;
            case NativePhase.Ready:
                if (state.ErrorKey is { } error)
                    viewport.Add(StatusAlert(Localize(error), palette, error: true,
                        automationId: NativeHowDoYouSayAccess.Error));
                viewport.Add(HowDoYouSayInputCard(state, palette));
                if (state.Notice is { } notice)
                    viewport.Add(StatusAlert(Localize(notice.Key), palette,
                        error: notice.Kind == NativeHowDoYouSayNoticeKind.Error,
                        automationId: NativeHowDoYouSayAccess.Notice,
                        success: notice.Kind == NativeHowDoYouSayNoticeKind.Success));
                if (state.HistoryLoading)
                    viewport.Add(new CircularProgressIndicator
                    {
                        Modifier = Modifier.Size(20).TestTag(NativeHowDoYouSayAccess.HistoryLoading)
                    });
                else if (state.ShowsHistory)
                {
                    viewport.Add(Heading(Localize(NativeHowDoYouSayText.History), palette, 20,
                        Modifier.TestTag(NativeHowDoYouSayAccess.HistoryTitle)
                            .Semantics(semantics => semantics.Heading())));
                    var list = new Column(Arrangement.SpacedBy(8))
                    {
                        Modifier = Modifier.FillMaxWidth().TestTag(NativeHowDoYouSayAccess.History),
                    };
                    foreach (var entry in state.History)
                        list.Add(HowDoYouSayEntry(entry, state.PlayingId == entry.Id, palette));
                    viewport.Add(list);
                }
                break;
            default:
                viewport.Add(StatusAlert(Localize(state.ErrorKey ?? NativeHowDoYouSayText.Unavailable),
                    palette, error: true, automationId: NativeHowDoYouSayAccess.Error));
                viewport.Add(Action(Localize(NativeHowDoYouSayText.GoBack),
                    "native.howdoyousay.go-back", ExitActivity, true, primary: false));
                break;
        }

        return new Column
        {
            Modifier.FillMaxSize().TestTag("native.howdoyousay.page"),
            ScreenHeader(Localize(NativeHowDoYouSayText.Title), palette,
                back: true, backAutomationId: NativeHowDoYouSayAccess.Back),
            viewport,
        };
    }

    private ComposableNode HowDoYouSayInputCard(NativeHowDoYouSayState state,
        AndroidVisualPalette palette)
    {
        var language = NativeHowDoYouSayText.LanguageKey(state.TargetLanguage) is { } key
            ? Localize(key) : state.TargetLanguage;
        var label = string.Format(CultureInfo.CurrentCulture,
            Localize(NativeHowDoYouSayText.EnterPhrase), language);
        var placeholder = Localize(NativeHowDoYouSayText.TypePhrase);
        var children = new List<ComposableNode>
        {
            Body(label, palette, secondary: true,
                Modifier.TestTag(NativeHowDoYouSayAccess.InputLabel)),
        };
        var keyboard = KeyboardOptionsCompanion.Default;
        children.Add(new OutlinedTextField(HowDoYouSayInput, enabled: true, singleLine: true)
        {
            Modifier = Modifier.FillMaxWidth().TestTag(NativeHowDoYouSayAccess.Input)
                .Semantics(semantics => semantics.ContentDescription(label)),
            Label = new Text(placeholder),
            KeyboardOptions = keyboard.Copy(
                keyboard.Capitalization, keyboard.AutoCorrectEnabled,
                AndroidX.Compose.KeyboardType.Text,
                AndroidX.Compose.ImeAction.Done,
                keyboard.PlatformImeOptions, keyboard.ShowKeyboardOnFocus,
                keyboard.HintLocales),
            KeyboardActions = KeyboardActionsHelper.Create(onDone: () =>
                HowDoYouSayCommand((presenter, token) => presenter.SpeakAsync(token))),
        });

        if (state.ShowsVoices)
        {
            var voiceLabel = Localize(NativeHowDoYouSayText.Voice);
            children.Add(Body(voiceLabel, palette, secondary: true,
                Modifier.TestTag(NativeHowDoYouSayAccess.VoiceLabel)));
            var selected = state.Voices.FirstOrDefault(voice => voice.Id == state.SelectedVoiceId)?.Label
                ?? string.Empty;
            var anchor = new OutlinedButton(() => ToggleHowDoYouSayVoiceMenu(!_howDoYouSayVoiceMenu),
                enabled: true)
            {
                Modifier = Modifier.FillMaxWidth().HeightIn(min: AndroidVisualTheme.ControlHeight)
                    .TestTag(NativeHowDoYouSayAccess.Voice)
                    .Semantics(semantics =>
                    {
                        semantics.ContentDescription($"{voiceLabel}: {selected}");
                        semantics.StateDescription(selected);
                    }),
                Shape = Shape.RoundedCorners(AndroidVisualTheme.ControlRadius),
            };
            anchor.Add(new Row(Arrangement.SpaceBetween, Alignment.Vertical.CenterVertically)
            {
                Modifier.FillMaxWidth(),
                new Text(selected) { Color = palette.Text },
                new Icon(Icons.Default.ArrowDropDown, voiceLabel)
                {
                    Modifier = Modifier.Size(20), Tint = palette.Text
                },
            });
            var dropdown = new DropdownMenu(expanded: _howDoYouSayVoiceMenu,
                onDismissRequest: () => ToggleHowDoYouSayVoiceMenu(false));
            for (var index = 0; index < state.Voices.Length; index++)
            {
                var voice = state.Voices[index];
                var isSelected = voice.Id == state.SelectedVoiceId;
                dropdown.Add(new DropdownMenuItem(text: new Text(voice.Label), onClick: () =>
                {
                    ToggleHowDoYouSayVoiceMenu(false);
                    HowDoYouSayCommand((presenter, token) => presenter.SelectVoiceAsync(voice.Id, token));
                })
                {
                    Modifier = Modifier.TestTag($"{NativeHowDoYouSayAccess.Voice}.option.{index}")
                        .Semantics(semantics =>
                        {
                            semantics.ContentDescription(voice.Label);
                            semantics.Selected(isSelected);
                        }),
                });
            }
            children.Add(new Box { anchor, dropdown });
        }

        var speakLabel = Localize(state.IsBusy ? NativeHowDoYouSayText.Generating : NativeHowDoYouSayText.Speak);
        // Speak is enabled for typed text; the presenter ignores an empty phrase (source disables it).
        var speak = new AndroidX.Compose.Button(
            onClick: () => HowDoYouSayCommand((presenter, token) => presenter.SpeakAsync(token)),
            enabled: !state.IsBusy)
        {
            Modifier = Modifier.FillMaxWidth().HeightIn(min: AndroidVisualTheme.ControlHeight)
                .TestTag(state.IsBusy ? NativeHowDoYouSayAccess.Generating : NativeHowDoYouSayAccess.Speak)
                .Semantics(semantics => semantics.ContentDescription(speakLabel)),
            Shape = Shape.RoundedCorners(AndroidVisualTheme.ControlRadius),
        };
        if (state.IsBusy)
            speak.Add(new Row(Arrangement.SpacedBy(8), Alignment.Vertical.CenterVertically)
            {
                new CircularProgressIndicator { Modifier = Modifier.Size(18) },
                new Text(speakLabel),
            });
        else
            speak.Add(new Text(speakLabel));
        children.Add(speak);
        return Card(palette, "native.howdoyousay.card", children.ToArray());
    }

    private ComposableNode HowDoYouSayEntry(NativeHowDoYouSayEntry entry, bool playing,
        AndroidVisualPalette palette)
    {
        var playLabel = Localize(playing ? NativeHowDoYouSayText.Pause : NativeHowDoYouSayText.Play);
        var play = new AndroidX.Compose.Button(
            onClick: () => HowDoYouSayCommand((presenter, token) => presenter.TogglePlayAsync(entry.Id, token)),
            enabled: true)
        {
            Modifier = Modifier.HeightIn(min: AndroidVisualTheme.ControlHeight)
                .TestTag(NativeHowDoYouSayAccess.EntryPlay(entry.Id))
                .Semantics(semantics =>
                {
                    semantics.ContentDescription(playLabel);
                    semantics.Selected(playing);
                }),
            Shape = Shape.RoundedCorners(AndroidVisualTheme.ControlRadius),
        };
        play.Add(playing
            // Material core icons have no Pause glyph: two bars (as in Shadowing).
            ? new Row(Arrangement.SpacedBy(4), Alignment.Vertical.CenterVertically)
            {
                new Box { Modifier = Modifier.Width(5).Height(18).Background(palette.OnPrimary) },
                new Box { Modifier = Modifier.Width(5).Height(18).Background(palette.OnPrimary) },
            }
            : new Icon(Icons.Default.PlayArrow, null) { Modifier = Modifier.Size(22), Tint = palette.OnPrimary });

        var deleteLabel = Localize(NativeHowDoYouSayText.Delete);
        var delete = new IconButton(
            () => HowDoYouSayCommand((presenter, token) => presenter.DeleteAsync(entry.Id, token)),
            enabled: true)
        {
            Modifier = Modifier.TestTag(NativeHowDoYouSayAccess.EntryDelete(entry.Id))
                .Border(1, palette.Danger, AndroidVisualTheme.ControlRadius)
                .Semantics(semantics => semantics.ContentDescription(deleteLabel)),
        };
        delete.Add(new Icon(Icons.Default.Delete, deleteLabel)
        {
            Modifier = Modifier.Size(22), Tint = palette.Danger,
        });

        var details = new Column(Arrangement.SpacedBy(2))
        {
            Modifier = Modifier.Weight(1f),
        };
        details.Add(new Text(entry.Phrase)
        {
            Modifier = Modifier.TestTag(NativeHowDoYouSayAccess.EntryPhrase(entry.Id)),
            Color = palette.Text,
            FontSize = new Sp(16),
            FontWeight = FontWeight.Bold,
        });
        details.Add(new Text(entry.CreatedAt.ToString("g", CultureInfo.CurrentCulture))
        {
            Modifier = Modifier.TestTag(NativeHowDoYouSayAccess.EntryTime(entry.Id)),
            Color = palette.SecondaryText,
            FontSize = new Sp(AndroidVisualTheme.CaptionSize),
        });
        return Card(palette, NativeHowDoYouSayAccess.Entry(entry.Id),
            new Row(Arrangement.SpacedBy(8), Alignment.Vertical.CenterVertically)
            {
                Modifier.FillMaxWidth(),
                details,
                play,
                delete,
            });
    }
}
