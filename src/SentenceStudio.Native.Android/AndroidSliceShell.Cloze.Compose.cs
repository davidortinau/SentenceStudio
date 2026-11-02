using System.Globalization;
using AndroidX.Compose;
using AndroidX.Compose.Runtime;
using AndroidX.Compose.UI.Text.Input;
using Microsoft.Extensions.DependencyInjection;
using SentenceStudio.Native;

namespace SentenceStudio.Native.Android;

/// <summary>
/// Compose Cloze (source Cloze.razor): sentence with blank and native
/// translation, inline feedback, then the input bar with the five choices
/// (Choices mode) or the typed answer (Type mode), the Type/Choices toggle,
/// Go, and the previous / n of total / next row; summary with Continue
/// practice and Done.
/// </summary>
internal sealed partial class AndroidSliceShell
{
    private NativeClozePresenter? _cloze;
    private int _clozeFieldGeneration = -1;

    internal NativeClozeState Cloze { get; private set; } = new(NativePhase.Loading);

    internal MutableState<TextFieldValue> ClozeAnswer { get; } =
        new(ComposeExtensions.NewTextFieldValue(string.Empty));

    private async Task OpenClozeAsync(NativeActivityLaunch launch, CancellationToken cancellationToken)
    {
        if (ActivityServices is not { } services)
            return;
        var presenter = services.GetRequiredService<NativeClozePresenter>();
        presenter.StateChanged += OnClozeChanged;
        _cloze = presenter;
        Publish(() => Cloze = new(NativePhase.Loading));
        var state = await presenter.OpenAsync(launch, cancellationToken);
        Publish(() =>
        {
            if (ReferenceEquals(_cloze, presenter))
                Cloze = state;
        });
    }

    private async Task ExitClozeAsync(CancellationToken cancellationToken)
    {
        if (_cloze is { } presenter)
            await presenter.ExitAsync(cancellationToken);
    }

    private void ReleaseCloze()
    {
        if (_cloze is { } presenter)
        {
            presenter.StateChanged -= OnClozeChanged;
            presenter.Dispose();
        }
        _cloze = null;
        _clozeFieldGeneration = -1;
    }

    private void OnClozeChanged(NativeClozeState state) =>
        Publish(() => Cloze = state);

    /// <summary>Answers and navigation bypass the shell's busy gate so the 1.2 s advance does not disable Back; the presenter serializes them.</summary>
    private void ClozeCommand(Func<NativeClozePresenter, CancellationToken, Task<NativeClozeState>> command)
    {
        if (_cloze is not { } presenter)
            return;
        _ = RunClozeAsync(presenter, command);
    }

    private async Task RunClozeAsync(NativeClozePresenter presenter,
        Func<NativeClozePresenter, CancellationToken, Task<NativeClozeState>> command)
    {
        try
        {
            var state = await command(presenter, _lifetime.Token);
            Publish(() =>
            {
                if (ReferenceEquals(_cloze, presenter))
                    Cloze = state;
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

    private void SubmitCloze() => ClozeCommand((presenter, token) =>
    {
        presenter.SetTypedAnswer(ClozeAnswer.Value.Text);
        return presenter.SubmitAsync(token);
    });

    private ComposableNode RenderCloze(AndroidVisualPalette palette)
    {
        var state = Cloze;
        // A new sentence (or reload) clears the typed answer, as the source does.
        if (state.Generation != _clozeFieldGeneration)
        {
            _clozeFieldGeneration = state.Generation;
            ClozeAnswer.Value = ComposeExtensions.NewTextFieldValue(state.TypedAnswer);
        }
        var viewport = new Column(Arrangement.SpacedBy(12))
        {
            Modifier = Modifier.FillMaxWidth().Weight(1f)
                .VerticalScroll(_quizScroll)
                .Padding(horizontal: AndroidVisualTheme.PagePadding, vertical: 16)
                .TestTag(NativeClozeAccess.Screen),
        };
        switch (state.Phase)
        {
            case NativePhase.Idle:
            case NativePhase.Loading:
                viewport.Add(new CircularProgressIndicator
                {
                    Modifier = Modifier.Size(28).TestTag(NativeClozeAccess.Loading)
                });
                viewport.Add(Body(Localize(NativeClozeText.Loading), palette, secondary: true));
                break;
            case NativePhase.Summary:
                viewport.Add(ClozeSummary(state, palette));
                break;
            case NativePhase.Ready:
                ClozeSentence(viewport, state, palette);
                ClozeInputBar(viewport, state, palette);
                break;
            default:
                var empty = state.Phase == NativePhase.Empty;
                viewport.Add(StatusAlert(Localize(state.ErrorKey ?? NativeClozeText.NoSentences),
                    palette, error: !empty,
                    automationId: empty ? NativeClozeAccess.Empty : NativeClozeAccess.Error));
                viewport.Add(Action(Localize(NativeClozeText.GoBack), NativeClozeAccess.EmptyBack,
                    ExitActivity, !Busy.Value, primary: false));
                break;
        }

        return new Column
        {
            Modifier.FillMaxSize().TestTag("native.cloze.page"),
            ScreenHeader(Localize(NativeClozeText.Title), palette,
                back: true, backAutomationId: NativeClozeAccess.Back),
            viewport,
        };
    }

    private void ClozeSentence(Column viewport, NativeClozeState state, AndroidVisualPalette palette)
    {
        // TalkBack reads the blank as a word, never as underscores or the answer.
        var spoken = state.DisplaySentence.Replace(NativeClozeChallenge.BlankMarker,
            Localize(NativeClozeText.Blank), StringComparison.Ordinal);
        viewport.Add(Heading(state.DisplaySentence, palette, 28,
            Modifier.FillMaxWidth().TestTag(NativeClozeAccess.Sentence)
                .Semantics(semantics => semantics.ContentDescription(spoken))));
        if (state.Translation.Length > 0)
            viewport.Add(Body(state.Translation, palette, secondary: true,
                Modifier.FillMaxWidth().TestTag(NativeClozeAccess.Translation)));
        if (state.IsGraded)
            viewport.Add(StatusAlert(state.LastWasCorrect == true
                    ? Localize(NativeClozeText.Correct)
                    : string.Format(CultureInfo.CurrentCulture,
                        Localize(NativeClozeText.AnswerWas), state.CorrectAnswer),
                palette, error: state.LastWasCorrect != true,
                automationId: NativeClozeAccess.Feedback, success: state.LastWasCorrect == true));
        // The probe outcome is a success/info snackbar (shell), never inline.
        if (MessageBanners(state.Notice, palette, NativeClozeAccess.Notice) is { } banner)
            viewport.Add(banner);
        if (state.ErrorKey is { } error)
            viewport.Add(StatusAlert(Localize(error), palette, error: true,
                automationId: NativeClozeAccess.Error));
    }

    private void ClozeInputBar(Column viewport, NativeClozeState state, AndroidVisualPalette palette)
    {
        var choices = state.InputMode == NativeClozeInputMode.MultipleChoice;
        if (choices && !state.Options.IsDefaultOrEmpty)
        {
            var columns = WindowWidthDp > 768 ? 5 : 2;
            for (var start = 0; start < state.Options.Length; start += columns)
            {
                var row = new Row(Arrangement.SpacedBy(8), Alignment.Vertical.CenterVertically)
                {
                    Modifier.FillMaxWidth(),
                };
                for (var index = start; index < start + columns; index++)
                    row.Add(index < state.Options.Length
                        ? ClozeChoice(state, palette, state.Options[index], index)
                        : new Box { Modifier.Weight(1f) });
                viewport.Add(row);
            }
        }

        var actions = new Row(Arrangement.SpacedBy(8), Alignment.Vertical.CenterVertically)
        {
            Modifier.FillMaxWidth(),
        };
        if (!choices)
        {
            var label = Localize(NativeClozeText.TypeMissingWord);
            actions.Add(new OutlinedTextField(ClozeAnswer, enabled: !state.IsGraded, singleLine: true)
            {
                Modifier = Modifier.Weight(1f).TestTag(NativeClozeAccess.Answer)
                    .Semantics(semantics => semantics.ContentDescription(label)),
                Placeholder = new Text(label),
                KeyboardActions = KeyboardActionsHelper.Create(onDone: SubmitCloze),
            });
        }
        var toggleLabel = Localize(choices ? NativeClozeText.Type : NativeClozeText.Choices);
        var toggle = new OutlinedButton(
            onClick: () => ClozeCommand((presenter, token) => presenter.ToggleModeAsync(token)),
            enabled: true)
        {
            Modifier = Modifier.HeightIn(min: AndroidVisualTheme.ControlHeight)
                .TestTag(NativeClozeAccess.ModeToggle)
                .Semantics(semantics => semantics.ContentDescription(toggleLabel)),
            Shape = Shape.RoundedCorners(AndroidVisualTheme.ControlRadius),
        };
        toggle.Add(new Text(toggleLabel));
        actions.Add(toggle);
        var goLabel = Localize(NativeClozeText.Go);
        var go = new AndroidX.Compose.Button(onClick: SubmitCloze, enabled: !state.IsGraded)
        {
            Modifier = Modifier.HeightIn(min: AndroidVisualTheme.ControlHeight)
                .TestTag(NativeClozeAccess.Submit)
                .Semantics(semantics => semantics.ContentDescription(goLabel)),
            Shape = Shape.RoundedCorners(AndroidVisualTheme.ControlRadius),
        };
        go.Add(new Text(goLabel));
        actions.Add(go);
        viewport.Add(actions);

        viewport.Add(new Row(Arrangement.SpacedBy(8), Alignment.Vertical.CenterVertically)
        {
            Modifier.FillMaxWidth(),
            ClozeChevron(Icons.AutoMirrored.Default.KeyboardArrowLeft, NativeClozeText.Previous,
                NativeClozeAccess.Previous, state.CanGoPrevious,
                (presenter, token) => presenter.PreviousAsync(token)),
            new Text(string.Format(CultureInfo.CurrentCulture, "{0} / {1}", state.Index + 1, state.Total))
            {
                Modifier = Modifier.Weight(1f).TestTag(NativeClozeAccess.Position),
                Color = palette.SecondaryText,
                FontSize = new Sp(14),
                Align = TextAlign.Center,
            },
            ClozeChevron(Icons.AutoMirrored.Default.KeyboardArrowRight, NativeClozeText.Next,
                NativeClozeAccess.Next, true,
                (presenter, token) => presenter.NextAsync(token)),
        });
    }

    private ComposableNode ClozeChevron(AndroidX.Compose.UI.Graphics.Vector.ImageVector icon, string labelKey, string tag, bool enabled,
        Func<NativeClozePresenter, CancellationToken, Task<NativeClozeState>> command)
    {
        var label = Localize(labelKey);
        var button = new OutlinedButton(onClick: () => ClozeCommand(command), enabled: enabled)
        {
            Modifier = Modifier.HeightIn(min: AndroidVisualTheme.ControlHeight)
                .TestTag(tag)
                .Semantics(semantics => semantics.ContentDescription(label)),
            Shape = Shape.RoundedCorners(AndroidVisualTheme.ControlRadius),
        };
        button.Add(new Icon(icon, label) { Modifier = Modifier.Size(20) });
        return button;
    }

    private ComposableNode ClozeChoice(NativeClozeState state, AndroidVisualPalette palette,
        string option, int index)
    {
        var correct = state.IsGraded && option == state.CorrectAnswer;
        var wrong = state.IsGraded && !correct && option == state.SelectedOption;
        var selected = option == state.SelectedOption;
        var border = correct ? palette.Success : wrong ? palette.Danger
            : selected ? palette.Primary : palette.Border;
        var modifier = Modifier.Weight(1f).HeightIn(min: AndroidVisualTheme.ControlHeight)
            .Border(1, border, AndroidVisualTheme.ControlRadius)
            .TestTag($"{NativeClozeAccess.Choice}.{index}");
        modifier = state.IsGraded
            ? modifier.Semantics(semantics =>
            {
                semantics.ContentDescription(option);
                semantics.Selected(selected);
            })
            : modifier.Clickable(() => ClozeCommand(
                    (presenter, token) => presenter.ChooseAsync(option, token)))
                .Semantics(semantics =>
                {
                    semantics.ContentDescription(option);
                    semantics.Role(SemanticsRole.Button);
                    semantics.Selected(selected);
                });
        var surface = new Surface
        {
            Modifier = modifier,
            Shape = Shape.RoundedCorners(AndroidVisualTheme.ControlRadius),
            Color = correct ? palette.Success : wrong ? palette.Danger : palette.Background,
        };
        surface.Add(new Box
        {
            Modifier.FillMaxWidth().HeightIn(min: AndroidVisualTheme.ControlHeight),
            new Text(option)
            {
                Modifier = Modifier.Align(Alignment.Center).FillMaxWidth().Padding(8),
                Color = correct || wrong ? palette.OnPrimary : palette.Text,
                FontSize = new Sp(16),
                Align = TextAlign.Center,
            },
        });
        return surface;
    }

    private ComposableNode ClozeSummary(NativeClozeState state, AndroidVisualPalette palette)
    {
        ComposableNode Stat(string value, string key, Color color, string tag)
        {
            var label = $"{Localize(key)} {value}";
            return new Column(Arrangement.SpacedBy(2))
            {
                Modifier.TestTag(tag).Semantics(semantics => semantics.ContentDescription(label)),
                new Text(value) { Color = color, FontSize = new Sp(28), FontWeight = FontWeight.SemiBold },
                Body(Localize(key), palette, secondary: true),
            };
        }
        return Card(palette, NativeClozeAccess.Summary,
            Heading(Localize(NativeClozeText.SessionComplete), palette, 24,
                Modifier.Semantics(semantics => semantics.Heading())),
            new Row(Arrangement.SpacedBy(32))
            {
                Stat(state.SessionCorrect.ToString(CultureInfo.CurrentCulture),
                    NativeClozeText.CorrectCount, palette.Success, NativeClozeAccess.SummaryCorrect),
                Stat(state.SessionIncorrect.ToString(CultureInfo.CurrentCulture),
                    NativeClozeText.IncorrectCount, palette.Danger, NativeClozeAccess.SummaryIncorrect),
                Stat(string.Format(CultureInfo.CurrentCulture, "{0}%", state.AccuracyPercent),
                    NativeClozeText.Accuracy, palette.Primary, NativeClozeAccess.SummaryAccuracy),
            },
            Action(Localize(NativeClozeText.ContinuePractice), NativeClozeAccess.ContinuePractice,
                () => ClozeCommand((presenter, token) => presenter.ContinuePracticeAsync(token)),
                !Busy.Value),
            Action(Localize(NativeClozeText.Done), NativeClozeAccess.Done, ExitActivity,
                !Busy.Value, primary: false));
    }
}
