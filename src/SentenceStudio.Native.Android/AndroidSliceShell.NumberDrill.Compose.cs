using System.Globalization;
using AndroidX.Compose;
using AndroidX.Compose.Runtime;
using AndroidX.Compose.UI.Text.Input;
using Microsoft.Extensions.DependencyInjection;
using SentenceStudio.Native;
using ComposeButton = AndroidX.Compose.Button;

namespace SentenceStudio.Native.Android;

/// <summary>
/// Compose Number Drill (source NumberDrill.razor): setup card, item card per
/// sub-mode with the feedback panel, progress footer and summary card. The
/// typed answer lives in its own TextFieldValue state so Korean IME
/// composition survives recomposition.
/// </summary>
internal sealed partial class AndroidSliceShell
{
    private NativeNumberDrillPresenter? _numberDrill;
    private bool _numberHintsOpen;
    private string? _numberItemKey;

    internal NativeNumberDrillState NumberDrill { get; private set; } = new(NativePhase.Loading);

    internal MutableState<TextFieldValue> NumberDrillAnswer { get; } =
        new(ComposeExtensions.NewTextFieldValue(string.Empty));

    private async Task OpenNumberDrillAsync(NativeActivityLaunch launch, CancellationToken cancellationToken)
    {
        if (ActivityServices is not { } services)
            return;
        var presenter = services.GetRequiredService<NativeNumberDrillPresenter>();
        presenter.StateChanged += OnNumberDrillChanged;
        _numberDrill = presenter;
        Publish(() => NumberDrill = new(NativePhase.Loading));
        var state = await presenter.OpenAsync(launch, cancellationToken);
        Publish(() =>
        {
            if (ReferenceEquals(_numberDrill, presenter))
                SetNumberDrill(state);
        });
    }

    private async Task ExitNumberDrillAsync(CancellationToken cancellationToken)
    {
        if (_numberDrill is { } presenter)
            await presenter.ExitAsync(cancellationToken);
    }

    private void ReleaseNumberDrill()
    {
        if (_numberDrill is { } presenter)
        {
            presenter.StateChanged -= OnNumberDrillChanged;
            presenter.Dispose();
        }
        _numberDrill = null;
        _numberHintsOpen = false;
        _numberItemKey = null;
    }

    private void OnNumberDrillChanged(NativeNumberDrillState state) =>
        Publish(() => SetNumberDrill(state));

    private void SetNumberDrill(NativeNumberDrillState state)
    {
        NumberDrill = state;
        var key = state.Item is { } item && state.Stage == NativeNumberDrillStage.InSession
            ? $"{state.ItemIndex}:{item.DisplayPrompt}:{item.PromptA}" : null;
        if (key != _numberItemKey)
        {
            _numberItemKey = key;
            _numberHintsOpen = false;
            NumberDrillAnswer.Value = ComposeExtensions.NewTextFieldValue(string.Empty);
        }
    }

    /// <summary>Drill actions bypass the shell's busy gate so auto-advance pauses do not disable Back; the presenter serializes them.</summary>
    private void NumberDrillCommand(
        Func<NativeNumberDrillPresenter, CancellationToken, Task<NativeNumberDrillState>> command)
    {
        if (_numberDrill is not { } presenter)
            return;
        _ = RunNumberDrillAsync(presenter, command);
    }

    private async Task RunNumberDrillAsync(NativeNumberDrillPresenter presenter,
        Func<NativeNumberDrillPresenter, CancellationToken, Task<NativeNumberDrillState>> command)
    {
        try
        {
            var state = await command(presenter, _lifetime.Token);
            Publish(() =>
            {
                if (ReferenceEquals(_numberDrill, presenter))
                    SetNumberDrill(state);
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

    private void SubmitNumberAnswer()
    {
        var answer = NumberDrillAnswer.Value.Text;
        if (!string.IsNullOrWhiteSpace(answer))
            NumberDrillCommand((presenter, token) => presenter.SubmitAnswerAsync(answer, token));
    }

    private ComposableNode RenderNumberDrill(AndroidVisualPalette palette)
    {
        var state = NumberDrill;
        var viewport = new Column(Arrangement.SpacedBy(12))
        {
            Modifier = Modifier.FillMaxWidth().Weight(1f)
                .VerticalScroll(_quizScroll)
                .Padding(horizontal: AndroidVisualTheme.PagePadding, vertical: 16)
                .TestTag(NativeNumberDrillAccess.Screen),
        };
        switch (state.Phase)
        {
            case NativePhase.Idle:
            case NativePhase.Loading:
                viewport.Add(new CircularProgressIndicator
                {
                    Modifier = Modifier.Size(28).TestTag(NativeNumberDrillAccess.Loading)
                });
                break;
            case NativePhase.Empty:
            case NativePhase.Blocked:
            case NativePhase.Error:
                var empty = state.Phase == NativePhase.Empty;
                viewport.Add(StatusAlert(Localize(state.ErrorKey ?? NativeNumberDrillText.Unavailable),
                    palette, error: !empty,
                    automationId: empty ? NativeNumberDrillAccess.Gate : NativeNumberDrillAccess.Error));
                viewport.Add(Action(Localize(NativeNumberDrillText.GoBack),
                    NativeNumberDrillAccess.GateBack, ExitActivity, !Busy.Value, primary: false));
                break;
            default:
                if (state.Stage == NativeNumberDrillStage.Setup)
                    viewport.Add(NumberSetup(state, palette));
                else if (state.Stage == NativeNumberDrillStage.Summary && state.Summary is { } summary)
                    viewport.Add(NumberSummary(summary, palette));
                else if (state.Item is { } item)
                    foreach (var node in NumberSession(state, item, palette))
                        viewport.Add(node);
                break;
        }

        var page = new Column
        {
            Modifier.FillMaxSize().TestTag("native.numberdrill.page"),
            ScreenHeader(Localize(NativeNumberDrillText.Title), palette,
                back: true, backAutomationId: NativeNumberDrillAccess.Back),
            viewport,
        };
        if (state.Stage == NativeNumberDrillStage.InSession && state.Item is not null
            && state.Phase is NativePhase.Ready or NativePhase.Feedback)
            page.Add(NumberFooter(state, palette));
        return page;
    }

    // ============ SETUP CARD ============
    private ComposableNode NumberSetup(NativeNumberDrillState state, AndroidVisualPalette palette)
    {
        var enabled = !state.IsBusy;
        var nodes = new List<ComposableNode>
        {
            Heading(Localize(NativeNumberDrillText.ChooseDrill), palette, 20,
                Modifier.Semantics(semantics => semantics.Heading())),
            NumberLabel(Localize(NativeNumberDrillText.Context), palette),
        };
        var contexts = new FlowRow { Modifier.FillMaxWidth() };
        foreach (var context in state.Contexts.IsDefault ? [] : state.Contexts)
        {
            var code = context.Code;
            contexts.Add(NumberChip(context.DisplayName, state.SelectedContext == code, enabled,
                () => NumberDrillCommand((p, t) => p.SelectContextAsync(code, t)),
                $"{NativeNumberDrillAccess.Context}.{code}"));
        }
        contexts.Add(NumberChip(Localize(NativeNumberDrillText.AnyContext), state.SelectedContext is null,
            enabled, () => NumberDrillCommand((p, t) => p.SelectContextAsync(null, t)),
            NativeNumberDrillAccess.ContextAny));
        nodes.Add(contexts);

        nodes.Add(NumberLabel(Localize(NativeNumberDrillText.Mode), palette));
        var modes = new FlowRow { Modifier.FillMaxWidth() };
        foreach (var mode in state.SubModes.IsDefault ? [] : state.SubModes)
        {
            var code = mode.Code;
            modes.Add(NumberChip(mode.DisplayName, state.SelectedSubMode == code, enabled,
                () => NumberDrillCommand((p, t) => p.SelectSubModeAsync(code, t)),
                $"{NativeNumberDrillAccess.Mode}.{code}"));
        }
        nodes.Add(modes);

        nodes.Add(NumberLabel(string.Format(CultureInfo.CurrentCulture,
            Localize(NativeNumberDrillText.Items), state.ItemCount), palette, NativeNumberDrillAccess.ItemCount));
        nodes.Add(new Row(Arrangement.SpacedBy(12), Alignment.Vertical.CenterVertically)
        {
            NumberIconButton(Localize(NativeNumberDrillText.FewerItems), "-",
                enabled && state.ItemCount > NativeNumberDrillPresenter.MinItems,
                () => NumberDrillCommand((p, t) => p.ChangeItemCountAsync(-1, t)),
                NativeNumberDrillAccess.ItemsDecrease),
            Body(state.ItemCount.ToString(CultureInfo.CurrentCulture), palette),
            NumberIconButton(Localize(NativeNumberDrillText.MoreItems), "+",
                enabled && state.ItemCount < NativeNumberDrillPresenter.MaxItems,
                () => NumberDrillCommand((p, t) => p.ChangeItemCountAsync(+1, t)),
                NativeNumberDrillAccess.ItemsIncrease),
        });

        var dueLabel = Localize(NativeNumberDrillText.DueOnly);
        var dueOnly = state.DueOnly;
        nodes.Add(new Row(Arrangement.SpacedBy(8), Alignment.Vertical.CenterVertically)
        {
            Modifier.FillMaxWidth().HeightIn(min: AndroidVisualTheme.ControlHeight)
                .TestTag(NativeNumberDrillAccess.DueOnly),
            new Checkbox(@checked: dueOnly,
                onCheckedChange: value => NumberDrillCommand((p, t) => p.SetDueOnlyAsync(value, t)),
                enabled: enabled)
            {
                Modifier = Modifier.Semantics(semantics => semantics.ContentDescription(dueLabel))
            },
            Body(dueLabel, palette),
        });

        nodes.Add(Action(Localize(NativeNumberDrillText.StartSession), NativeNumberDrillAccess.Start,
            () => NumberDrillCommand((p, t) => p.StartAsync(t)),
            enabled && !string.IsNullOrEmpty(state.SelectedSubMode)));
        if (state.ErrorKey is { } error)
            nodes.Add(StatusAlert(Localize(error), palette, error: true,
                automationId: NativeNumberDrillAccess.Error));
        return Card(palette, NativeNumberDrillAccess.Setup, nodes.ToArray());
    }

    // ============ ITEM CARD ============
    private IEnumerable<ComposableNode> NumberSession(NativeNumberDrillState state,
        NativeNumberDrillItem item, AndroidVisualPalette palette)
    {
        var systemColor = Color.FromHex(item.System == "Sino" ? "#20C997" : "#6F42C1");
        var system = new Surface
        {
            Modifier = Modifier.TestTag(NativeNumberDrillAccess.System),
            Shape = Shape.RoundedCorners(999),
            Color = systemColor,
        };
        system.Add(new Text(item.System)
        {
            Modifier = Modifier.Padding(horizontal: 12, vertical: 4),
            Color = Color.FromHex("#FFFFFF"),
            FontSize = new Sp(13),
            FontWeight = FontWeight.SemiBold,
        });
        yield return new Row(Arrangement.SpaceBetween, Alignment.Vertical.CenterVertically)
        {
            Modifier.FillMaxWidth(),
            system,
            Body(item.ContextCode, palette, secondary: true,
                Modifier.TestTag(NativeNumberDrillAccess.ItemContext)),
        };

        if (state.ErrorKey is { } error)
            yield return StatusAlert(Localize(error), palette, error: true,
                automationId: NativeNumberDrillAccess.Error);
        if (state.NoticeKey is { } notice)
            yield return MessageBanner(NativeNumberDrillMessages.Notice(notice), palette,
                NativeNumberDrillAccess.Notice);

        if (state.Phase == NativePhase.Feedback && state.Feedback is { } feedback)
        {
            if (item.SubModeCode == NativeNumberSubMode.Disambiguate)
            {
                yield return NumberPaired(state, item, true, palette);
                yield return NumberPaired(state, item, false, palette);
            }
            foreach (var node in NumberFeedback(state, feedback, palette))
                yield return node;
            yield break;
        }

        switch (item.SubModeCode)
        {
            case NativeNumberSubMode.ListenAndType:
                yield return NumberPlay(state, NativeNumberDrillText.Play, palette);
                break;
            case NativeNumberSubMode.ReadAndProduce:
                yield return new Text(item.DisplayPrompt)
                {
                    Modifier = Modifier.FillMaxWidth().Padding(vertical: 16)
                        .TestTag(NativeNumberDrillAccess.Prompt),
                    Color = palette.Primary,
                    FontSize = new Sp(44),
                    FontWeight = FontWeight.Bold,
                    Align = TextAlign.Center,
                };
                break;
            case NativeNumberSubMode.TapTheCounter:
                if (!string.IsNullOrEmpty(item.NounCue))
                    yield return new Text(item.NounCue)
                    {
                        Modifier = Modifier.FillMaxWidth().TestTag(NativeNumberDrillAccess.NounCue),
                        Color = palette.SecondaryText,
                        FontSize = new Sp(15),
                        Align = TextAlign.Center,
                    };
                yield return new Text(item.DisplayPrompt)
                {
                    Modifier = Modifier.FillMaxWidth().Padding(vertical: 12)
                        .TestTag(NativeNumberDrillAccess.Prompt),
                    Color = palette.Text,
                    FontSize = new Sp(36),
                    FontWeight = FontWeight.Bold,
                    Align = TextAlign.Center,
                };
                yield return NumberChoices(state, item, palette);
                break;
            case NativeNumberSubMode.ListenAndPlace:
                yield return NumberPlay(state, NativeNumberDrillText.PlayAudio, palette);
                yield return new Text(Localize(NativeNumberDrillText.ListenAndPlaceInstructions))
                {
                    Modifier = Modifier.FillMaxWidth(),
                    Color = palette.SecondaryText,
                    FontSize = new Sp(15),
                    Align = TextAlign.Center,
                };
                yield return NumberChoices(state, item, palette);
                yield return Action(Localize(NativeNumberDrillText.Replay), NativeNumberDrillAccess.Replay,
                    () => NumberDrillCommand((p, t) => p.PlayAudioAsync(t)),
                    !state.IsBusy && !state.IsPlayingAudio, primary: false);
                break;
            case NativeNumberSubMode.Disambiguate:
                yield return NumberPaired(state, item, true, palette);
                yield return NumberPaired(state, item, false, palette);
                yield return Action(Localize(NativeNumberDrillText.SubmitBoth), NativeNumberDrillAccess.SubmitBoth,
                    () => NumberDrillCommand((p, t) => p.SubmitBothAsync(t)), state.CanSubmitBoth);
                break;
        }

        if (!NativeNumberSubMode.IsTextEntry(item.SubModeCode))
            yield break;

        if (!item.Hints.IsDefaultOrEmpty)
        {
            var hintLabel = Localize(NativeNumberDrillText.ShowHint);
            var open = _numberHintsOpen;
            var toggle = new TextButtonNode(hintLabel, NativeNumberDrillAccess.HintToggle, () =>
                Publish(() => _numberHintsOpen = !_numberHintsOpen));
            yield return toggle.Build(open);
            if (open)
            {
                var hints = new Column(Arrangement.SpacedBy(4))
                {
                    Modifier.FillMaxWidth().Padding(start: 16).TestTag(NativeNumberDrillAccess.Hints),
                };
                foreach (var hint in item.Hints)
                    hints.Add(Body($"• {hint}", palette, secondary: true));
                yield return hints;
            }
        }

        var placeholder = string.Format(CultureInfo.CurrentCulture,
            Localize(NativeNumberDrillText.ExamplePattern), item.SpacingHint);
        var keyboard = KeyboardOptionsCompanion.Default;
        yield return new OutlinedTextField(NumberDrillAnswer, enabled: !state.IsBusy, singleLine: true)
        {
            Modifier = Modifier.FillMaxWidth().TestTag(NativeNumberDrillAccess.Answer)
                .Semantics(semantics => semantics.ContentDescription(placeholder)),
            Placeholder = new Text(placeholder),
            KeyboardOptions = keyboard.Copy(
                keyboard.Capitalization, keyboard.AutoCorrectEnabled,
                AndroidX.Compose.KeyboardType.Text,
                AndroidX.Compose.ImeAction.Done,
                keyboard.PlatformImeOptions, keyboard.ShowKeyboardOnFocus,
                keyboard.HintLocales),
            KeyboardActions = KeyboardActionsHelper.Create(onDone: SubmitNumberAnswer),
        };
        yield return Action(Localize(NativeNumberDrillText.Submit), NativeNumberDrillAccess.Submit,
            SubmitNumberAnswer, !state.IsBusy && !string.IsNullOrWhiteSpace(NumberDrillAnswer.Value.Text));
    }

    private ComposableNode NumberPlay(NativeNumberDrillState state, string idleKey,
        AndroidVisualPalette palette)
    {
        var label = Localize(state.IsPlayingAudio ? NativeNumberDrillText.Playing : idleKey);
        var button = new ComposeButton(() => NumberDrillCommand((p, t) => p.PlayAudioAsync(t)),
            enabled: !state.IsPlayingAudio && !state.IsBusy && state.Item?.HasAudio == true)
        {
            Modifier = Modifier.HeightIn(min: AndroidVisualTheme.ControlHeight + 8)
                .TestTag(NativeNumberDrillAccess.Play)
                .Semantics(semantics => semantics.ContentDescription(label)),
            Shape = Shape.RoundedCorners(AndroidVisualTheme.ControlRadius),
        };
        button.Add(new Row(Arrangement.SpacedBy(8), Alignment.Vertical.CenterVertically)
        {
            new Icon(Icons.Default.PlayArrow, null) { Modifier = Modifier.Size(24) },
            new Text(label) { FontSize = new Sp(18) },
        });
        return new Box { Modifier.FillMaxWidth(), new Box { Modifier.Align(Alignment.Center), button } };
    }

    private ComposableNode NumberChoices(NativeNumberDrillState state, NativeNumberDrillItem item,
        AndroidVisualPalette palette)
    {
        var row = new FlowRow { Modifier.FillMaxWidth().TestTag(NativeNumberDrillAccess.Choice) };
        for (var index = 0; index < item.Choices.Length; index++)
        {
            var choice = item.Choices[index];
            var button = new OutlinedButton(
                () => NumberDrillCommand((p, t) => p.TapChoiceAsync(choice, t)), enabled: !state.IsBusy)
            {
                Modifier = Modifier.Padding(end: 12, bottom: 12).HeightIn(min: 64)
                    .TestTag($"{NativeNumberDrillAccess.Choice}.{index}")
                    .Semantics(semantics => semantics.ContentDescription(choice)),
                Shape = Shape.RoundedCorners(AndroidVisualTheme.ControlRadius),
            };
            button.Add(new Text(choice) { FontSize = new Sp(24), Color = palette.Text });
            row.Add(button);
        }
        return row;
    }

    private ComposableNode NumberPaired(NativeNumberDrillState state, NativeNumberDrillItem item, bool a,
        AndroidVisualPalette palette)
    {
        var feedback = state.Phase == NativePhase.Feedback ? state.Feedback : null;
        var choices = a ? item.ChoicesA : item.ChoicesB;
        var selected = a ? state.SelectedAnswerA : state.SelectedAnswerB;
        var correct = a ? feedback?.CorrectA : feedback?.CorrectB;
        var content = new Column(Arrangement.SpacedBy(8))
        {
            Modifier.FillMaxWidth().Padding(16),
            Body(Localize(a ? NativeNumberDrillText.PromptA : NativeNumberDrillText.PromptB), palette,
                secondary: true),
            Heading((a ? item.PromptA : item.PromptB) ?? string.Empty, palette, 20,
                Modifier.TestTag(a ? NativeNumberDrillAccess.PromptA : NativeNumberDrillAccess.PromptB)),
        };
        if (!choices.IsDefaultOrEmpty)
        {
            var row = new FlowRow { Modifier.FillMaxWidth() };
            for (var index = 0; index < choices.Length; index++)
            {
                var choice = choices[index];
                row.Add(NumberChip(choice, selected == choice, feedback is null && !state.IsBusy,
                    () => NumberDrillCommand((p, t) => p.SelectPairedAnswerAsync(a, choice, t)),
                    $"{(a ? NativeNumberDrillAccess.ChoiceA : NativeNumberDrillAccess.ChoiceB)}.{index}"));
            }
            content.Add(row);
        }
        var hint = a ? item.HintA : item.HintB;
        if (feedback is not null && !string.IsNullOrEmpty(hint))
            content.Add(StatusAlert(hint, palette,
                automationId: a ? NativeNumberDrillAccess.HintA : NativeNumberDrillAccess.HintB));
        var border = correct is null ? palette.Border : correct.Value ? palette.Success : palette.Danger;
        var surface = new Surface
        {
            Modifier = Modifier.FillMaxWidth()
                .Border(correct is null ? 1 : 2, border, AndroidVisualTheme.CardRadius),
            Shape = Shape.RoundedCorners(AndroidVisualTheme.CardRadius),
            Color = palette.Card,
        };
        surface.Add(content);
        return surface;
    }

    private IEnumerable<ComposableNode> NumberFeedback(NativeNumberDrillState state,
        NativeNumberDrillFeedback feedback, AndroidVisualPalette palette)
    {
        var verdict = feedback.VerdictKey is { } key ? Localize(key) : feedback.Verdict ?? string.Empty;
        var panel = new Column(Arrangement.SpacedBy(6))
        {
            Modifier.FillMaxWidth().Padding(16),
            new Row(Arrangement.SpacedBy(8), Alignment.Vertical.CenterVertically)
            {
                Modifier.TestTag(NativeNumberDrillAccess.Verdict)
                    .Semantics(semantics => semantics.ContentDescription(verdict)),
                new Icon(feedback.IsCorrect ? Icons.Default.CheckCircle : Icons.Default.Close, null)
                {
                    Modifier = Modifier.Size(24),
                    Tint = feedback.IsCorrect ? palette.Success : palette.Danger,
                },
                Heading(verdict, palette, 20),
            },
        };
        if (!string.IsNullOrEmpty(feedback.UserAnswer))
            panel.Add(NumberLine(Localize(NativeNumberDrillText.YouTyped), feedback.UserAnswer, palette,
                NativeNumberDrillAccess.YouTyped));
        if (!string.IsNullOrEmpty(feedback.DigitDisplay))
            panel.Add(NumberLine(Localize(NativeNumberDrillText.Number), feedback.DigitDisplay, palette,
                NativeNumberDrillAccess.DigitAnswer));
        panel.Add(NumberLine(Localize(NativeNumberDrillText.Korean), feedback.CanonicalAnswer, palette,
            NativeNumberDrillAccess.KoreanAnswer));
        if (!feedback.IsCorrect && !string.IsNullOrEmpty(feedback.ErrorClass))
            panel.Add(NumberLine($"{feedback.ErrorClass}:",
                feedback.TipKey is { } tipKey ? Localize(tipKey) : feedback.Tip ?? string.Empty, palette,
                NativeNumberDrillAccess.ErrorClass));
        if (feedback.ProgressTotal is int total && feedback.NextReviewUtc is { } due)
            panel.Add(new Text(string.Format(CultureInfo.CurrentCulture,
                Localize(NativeNumberDrillText.MasteryProgress), feedback.ProgressCorrect ?? 0, total,
                due.ToLocalTime().ToString("MMM d", CultureInfo.CurrentCulture)))
            {
                Modifier = Modifier.TestTag(NativeNumberDrillAccess.MasteryProgress),
                Color = palette.SecondaryText,
                FontSize = new Sp(13),
            });
        var surface = new Surface
        {
            Modifier = Modifier.FillMaxWidth().TestTag(NativeNumberDrillAccess.Feedback)
                .Border(1, feedback.IsCorrect ? palette.Success : palette.Danger,
                    AndroidVisualTheme.ControlRadius),
            Shape = Shape.RoundedCorners(AndroidVisualTheme.ControlRadius),
            Color = feedback.IsCorrect ? palette.Elevated : palette.DangerSurface,
        };
        surface.Add(panel);
        yield return surface;

        if (!feedback.IsCorrect)
        {
            var label = Localize(NativeNumberDrillText.IWasCorrect);
            var overrideButton = new OutlinedButton(
                () => NumberDrillCommand((p, t) => p.OverrideAsync(t)), enabled: !state.IsOverriding)
            {
                Modifier = Modifier.TestTag(NativeNumberDrillAccess.Override)
                    .Semantics(semantics => semantics.ContentDescription(label)),
                Shape = Shape.RoundedCorners(AndroidVisualTheme.ControlRadius),
            };
            overrideButton.Add(new Row(Arrangement.SpacedBy(4), Alignment.Vertical.CenterVertically)
            {
                new Icon(Icons.Default.Check, null) { Modifier = Modifier.Size(18), Tint = palette.Success },
                new Text(label) { Color = palette.Success },
            });
            yield return new Box { Modifier.FillMaxWidth(), new Box { Modifier.Align(Alignment.Center), overrideButton } };
        }
        yield return Action(Localize(state.IsLastItem ? NativeNumberDrillText.Finish : NativeNumberDrillText.Next),
            NativeNumberDrillAccess.Next, () => NumberDrillCommand((p, t) => p.NextAsync(t)), true);
    }

    private static ComposableNode NumberLine(string label, string value, AndroidVisualPalette palette, string tag)
    {
        var line = $"{label} {value}";
        return new Row(Arrangement.SpacedBy(4))
        {
            Modifier.TestTag(tag).Semantics(semantics => semantics.ContentDescription(line)),
            new Text(label) { Color = palette.Text, FontSize = new Sp(15), FontWeight = FontWeight.SemiBold },
            Body(value, palette),
        };
    }

    private ComposableNode NumberFooter(NativeNumberDrillState state, AndroidVisualPalette palette)
    {
        var correct = string.Format(CultureInfo.CurrentCulture,
            Localize(NativeNumberDrillText.CorrectCount), state.CorrectCount);
        var badge = new Surface
        {
            Modifier = Modifier.TestTag(NativeNumberDrillAccess.CorrectCount)
                .Semantics(semantics => semantics.ContentDescription(correct)),
            Shape = Shape.RoundedCorners(999),
            Color = palette.Success,
        };
        badge.Add(new Text(correct)
        {
            Modifier = Modifier.Padding(horizontal: 12, vertical: 4),
            Color = Color.FromHex("#FFFFFF"),
            FontSize = new Sp(13),
            FontWeight = FontWeight.SemiBold,
        });
        var bar = new Surface { Modifier = Modifier.FillMaxWidth(), Color = palette.Card };
        bar.Add(new Row(Arrangement.SpaceBetween, Alignment.Vertical.CenterVertically)
        {
            Modifier.FillMaxWidth().Padding(horizontal: AndroidVisualTheme.PagePadding, vertical: 12),
            Body($"{state.ItemIndex + 1} / {state.ItemTotal}", palette, secondary: true,
                Modifier.TestTag(NativeNumberDrillAccess.Progress)),
            badge,
        });
        return bar;
    }

    // ============ SUMMARY SCREEN ============
    private ComposableNode NumberSummary(NativeNumberDrillSummary summary, AndroidVisualPalette palette)
    {
        ComposableNode Stat(string value, string label, Color color, string tag) =>
            new Column(Arrangement.SpacedBy(2))
            {
                Modifier.TestTag(tag).Semantics(semantics => semantics.ContentDescription($"{label} {value}")),
                new Text(value) { Color = color, FontSize = new Sp(28), FontWeight = FontWeight.SemiBold },
                Body(label, palette, secondary: true),
            };
        var nodes = new List<ComposableNode>
        {
            new Row(Arrangement.SpacedBy(8), Alignment.Vertical.CenterVertically)
            {
                new Icon(Icons.Default.CheckCircle, null) { Modifier = Modifier.Size(28), Tint = palette.Success },
                Heading(Localize(NativeNumberDrillText.SessionComplete), palette, 24,
                    Modifier.Semantics(semantics => semantics.Heading())),
            },
            new Row(Arrangement.SpacedBy(32))
            {
                Stat($"{summary.CorrectCount}/{summary.TotalItems}", Localize(NativeNumberDrillText.Correct),
                    palette.Success, NativeNumberDrillAccess.SummaryCorrect),
                Stat(string.Format(CultureInfo.CurrentCulture, Localize(NativeNumberDrillText.Milliseconds),
                        summary.MedianLatencyMs),
                    Localize(NativeNumberDrillText.MedianTime), palette.Text, NativeNumberDrillAccess.SummaryMedian),
            },
        };
        if (!summary.ErrorClassBreakdown.IsDefaultOrEmpty)
        {
            var patterns = new Column(Arrangement.SpacedBy(6))
            {
                Modifier.FillMaxWidth().TestTag(NativeNumberDrillAccess.Patterns),
                Heading(Localize(NativeNumberDrillText.PatternsToReview), palette, 18,
                    Modifier.Semantics(semantics => semantics.Heading())),
            };
            foreach (var (error, count) in summary.ErrorClassBreakdown)
            {
                var badge = new Surface { Shape = Shape.RoundedCorners(6), Color = Color.FromHex("#FFC233") };
                badge.Add(new Text(count.ToString(CultureInfo.CurrentCulture))
                {
                    Modifier = Modifier.Padding(horizontal: 8, vertical: 2),
                    Color = Color.FromHex("#212529"),
                    FontSize = new Sp(13),
                    FontWeight = FontWeight.SemiBold,
                });
                patterns.Add(new Row(Arrangement.SpaceBetween, Alignment.Vertical.CenterVertically)
                {
                    Modifier.FillMaxWidth().Semantics(semantics => semantics.ContentDescription($"{error} {count}")),
                    Body(error, palette),
                    badge,
                });
            }
            nodes.Add(patterns);
        }
        nodes.Add(Action(Localize(NativeNumberDrillText.AnotherRound), NativeNumberDrillAccess.AnotherRound,
            () => NumberDrillCommand((p, t) => p.TryAgainAsync(t)), true));
        nodes.Add(Action(Localize(NativeNumberDrillText.Done), NativeNumberDrillAccess.Done,
            ExitActivity, !Busy.Value, primary: false));
        return Card(palette, NativeNumberDrillAccess.Summary, nodes.ToArray());
    }

    private static ComposableNode NumberLabel(string text, AndroidVisualPalette palette, string? tag = null) =>
        new Text(text)
        {
            Modifier = tag is null ? null : Modifier.TestTag(tag),
            Color = palette.Text,
            FontSize = new Sp(15),
            FontWeight = FontWeight.SemiBold,
        };

    /// <summary>Source chip buttons: btn-ss-primary when selected, btn-outline-secondary otherwise.</summary>
    private static ComposableNode NumberChip(string label, bool selected, bool enabled, Action command, string tag)
    {
        var modifier = Modifier.Padding(end: 8, bottom: 8).HeightIn(min: AndroidVisualTheme.ControlHeight)
            .TestTag(tag)
            .Semantics(semantics =>
            {
                semantics.ContentDescription(label);
                semantics.Selected(selected);
            });
        if (selected)
        {
            var filled = new ComposeButton(onClick: command, enabled: enabled)
            {
                Modifier = modifier,
                Shape = Shape.RoundedCorners(AndroidVisualTheme.ControlRadius),
            };
            filled.Add(new Text(label));
            return filled;
        }
        var outlined = new OutlinedButton(onClick: command, enabled: enabled)
        {
            Modifier = modifier,
            Shape = Shape.RoundedCorners(AndroidVisualTheme.ControlRadius),
        };
        outlined.Add(new Text(label));
        return outlined;
    }

    private static ComposableNode NumberIconButton(string label, string glyph, bool enabled, Action command,
        string tag)
    {
        var button = new OutlinedButton(onClick: command, enabled: enabled)
        {
            Modifier = Modifier.HeightIn(min: AndroidVisualTheme.ControlHeight).TestTag(tag)
                .Semantics(semantics => semantics.ContentDescription(label)),
            Shape = Shape.RoundedCorners(AndroidVisualTheme.ControlRadius),
        };
        button.Add(new Text(glyph) { FontSize = new Sp(20) });
        return button;
    }

    /// <summary>Source "Show hint" details/summary disclosure.</summary>
    private sealed class TextButtonNode(string label, string tag, Action toggle)
    {
        public ComposableNode Build(bool open)
        {
            var button = new TextButton(toggle, true)
            {
                Modifier = Modifier.TestTag(tag).Semantics(semantics =>
                {
                    semantics.ContentDescription(label);
                    semantics.Selected(open);
                }),
            };
            button.Add(new Row(Arrangement.SpacedBy(4), Alignment.Vertical.CenterVertically)
            {
                new Icon(open ? Icons.Default.KeyboardArrowDown : Icons.AutoMirrored.Default.KeyboardArrowRight, null)
                {
                    Modifier = Modifier.Size(20),
                },
                new Text(label),
            });
            return button;
        }
    }
}
