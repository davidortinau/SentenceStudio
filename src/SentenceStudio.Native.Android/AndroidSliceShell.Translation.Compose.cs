using System.Globalization;
using AndroidX.Compose;
using AndroidX.Compose.Runtime;
using AndroidX.Compose.UI.Text.Input;
using Microsoft.Extensions.DependencyInjection;
using SentenceStudio.Native;

namespace SentenceStudio.Native.Android;

/// <summary>
/// Compose Translation (source Translation.razor). The answer lives in a
/// Compose text-field state so an input method (Korean Hangul) composes
/// undisturbed; it is handed to the presenter before every command.
/// </summary>
internal sealed partial class AndroidSliceShell
{
    private NativeTranslationPresenter? _translation;

    internal NativeTranslationState Translation { get; private set; } = new(NativePhase.Loading);

    internal MutableState<TextFieldValue> TranslationAnswer { get; } =
        new(ComposeExtensions.NewTextFieldValue(string.Empty));

    private async Task OpenTranslationAsync(NativeActivityLaunch launch, CancellationToken cancellationToken)
    {
        if (ActivityServices is not { } services)
            return;
        var presenter = services.GetRequiredService<NativeTranslationPresenter>();
        presenter.StateChanged += OnTranslationChanged;
        _translation = presenter;
        Publish(() => Translation = new(NativePhase.Loading));
        var state = await presenter.OpenAsync(launch, cancellationToken);
        Publish(() =>
        {
            if (ReferenceEquals(_translation, presenter))
                ApplyTranslation(state);
        });
    }

    private async Task ExitTranslationAsync(CancellationToken cancellationToken)
    {
        if (_translation is { } presenter)
            await presenter.ExitAsync(cancellationToken);
    }

    private void ReleaseTranslation()
    {
        if (_translation is { } presenter)
        {
            presenter.StateChanged -= OnTranslationChanged;
            presenter.Dispose();
        }
        _translation = null;
    }

    private void OnTranslationChanged(NativeTranslationState state) =>
        Publish(() => ApplyTranslation(state));

    /// <summary>Replaces the field only when the presenter changed the answer (block, new sentence).</summary>
    private void ApplyTranslation(NativeTranslationState state)
    {
        Translation = state;
        if (!string.Equals(TranslationAnswer.Value.Text ?? string.Empty, state.Input, StringComparison.Ordinal))
            TranslationAnswer.Value = ComposeExtensions.NewTextFieldValue(state.Input);
    }

    /// <summary>Commands bypass the shell's busy gate so a slow grade does not disable Back.</summary>
    private void TranslationCommand(
        Func<NativeTranslationPresenter, CancellationToken, Task<NativeTranslationState>> command)
    {
        if (_translation is not { } presenter)
            return;
        presenter.SetInput(TranslationAnswer.Value.Text);
        _ = RunTranslationAsync(presenter, command);
    }

    private async Task RunTranslationAsync(NativeTranslationPresenter presenter,
        Func<NativeTranslationPresenter, CancellationToken, Task<NativeTranslationState>> command)
    {
        try
        {
            var state = await command(presenter, _lifetime.Token);
            Publish(() =>
            {
                if (ReferenceEquals(_translation, presenter))
                    ApplyTranslation(state);
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

    private ComposableNode RenderTranslation(AndroidVisualPalette palette)
    {
        var state = Translation;
        var viewport = new Column(Arrangement.SpacedBy(12))
        {
            Modifier = Modifier.FillMaxWidth().Weight(1f)
                .VerticalScroll(_quizScroll)
                .Padding(horizontal: AndroidVisualTheme.PagePadding, vertical: 16)
                .TestTag(NativeTranslationAccess.Screen),
        };
        if (state.Notice is { } notice && state.Phase != NativePhase.Blocked)
        {
            var message = notice.Argument is null ? Localize(notice.Key)
                : string.Format(CultureInfo.CurrentCulture, Localize(notice.Key), notice.Argument);
            viewport.Add(StatusAlert(message, palette, error: notice.IsError,
                automationId: NativeTranslationAccess.Notice,
                success: !notice.IsError && !notice.IsWarning));
        }
        ComposableNode? inputBar = null;
        switch (state.Phase)
        {
            case NativePhase.Idle:
            case NativePhase.Loading:
                viewport.Add(new CircularProgressIndicator
                {
                    Modifier = Modifier.Size(28).TestTag(NativeTranslationAccess.Loading)
                });
                viewport.Add(Body(Localize(NativeTranslationText.Loading), palette, secondary: true));
                break;
            case NativePhase.Summary:
                viewport.Add(TranslationSummary(state, palette));
                break;
            case NativePhase.Ready when state.ShowIntro:
                viewport.Add(TranslationIntro(state, palette));
                break;
            case NativePhase.Ready:
                if (state.Feedback is { } feedback)
                    viewport.Add(TranslationFeedback(state, feedback, palette));
                inputBar = TranslationInputBar(state, palette);
                break;
            default:
                var empty = state.Phase == NativePhase.Empty;
                viewport.Add(StatusAlert(
                    Localize(state.ErrorKey ?? NativeTranslationText.NoSentences), palette,
                    error: !empty,
                    automationId: empty ? NativeTranslationAccess.Empty : NativeTranslationAccess.Error));
                viewport.Add(Action(Localize(NativeTranslationText.GoBack),
                    NativeTranslationAccess.EmptyBack, ExitActivity, !Busy.Value, primary: false));
                break;
        }

        var page = new Column
        {
            Modifier.FillMaxSize().TestTag("native.translation.page"),
            ScreenHeader(Localize(NativeTranslationText.Title), palette,
                back: true, backAutomationId: NativeTranslationAccess.Back),
            viewport,
        };
        if (inputBar is not null)
            page.Add(inputBar);
        return page;
    }

    private ComposableNode TranslationIntro(NativeTranslationState state, AndroidVisualPalette palette)
    {
        var rows = new List<ComposableNode>();
        if (!string.IsNullOrWhiteSpace(state.IntroResourceTitle))
            rows.Add(Body($"{Localize(NativeTranslationText.IntroResource)}: {state.IntroResourceTitle}",
                palette, secondary: true, Modifier.TestTag(NativeTranslationAccess.IntroResource)));
        if (!state.IntroVocabulary.IsDefaultOrEmpty)
        {
            rows.Add(Heading(Localize(NativeTranslationText.IntroVocabulary), palette, 18,
                Modifier.Semantics(semantics => semantics.Heading())));
            var words = new Column(Arrangement.SpacedBy(4))
            {
                Modifier.FillMaxWidth().TestTag(NativeTranslationAccess.IntroVocabulary),
            };
            foreach (var word in state.IntroVocabulary)
                words.Add(Body(string.IsNullOrEmpty(word.Native)
                    ? $"• {word.Target}" : $"• {word.Target} — {word.Native}", palette));
            rows.Add(words);
        }
        if (!state.IntroGrammar.IsDefaultOrEmpty)
        {
            rows.Add(Heading(Localize(NativeTranslationText.IntroGrammar), palette, 18,
                Modifier.Semantics(semantics => semantics.Heading())));
            var grammar = new Column(Arrangement.SpacedBy(4))
            {
                Modifier.FillMaxWidth().TestTag(NativeTranslationAccess.IntroGrammar),
            };
            foreach (var line in state.IntroGrammar)
                grammar.Add(Body($"• {line}", palette));
            rows.Add(grammar);
        }
        return new Column(Arrangement.SpacedBy(16))
        {
            Modifier.FillMaxWidth().TestTag(NativeTranslationAccess.Intro),
            Heading(Localize(NativeTranslationText.IntroTitle), palette, 24,
                Modifier.Semantics(semantics => semantics.Heading())),
            Card(palette, null, rows.ToArray()),
            Action(Localize(NativeTranslationText.Start), NativeTranslationAccess.Start,
                () => TranslationCommand((presenter, token) => presenter.StartAsync(token)), true),
        };
    }

    private ComposableNode TranslationFeedback(NativeTranslationState state,
        NativeTranslationFeedback feedback, AndroidVisualPalette palette)
    {
        var rows = new List<ComposableNode>();
        if (!string.IsNullOrWhiteSpace(feedback.SubmittedInput))
            rows.Add(Body(feedback.SubmittedInput, palette,
                modifier: Modifier.TestTag(NativeTranslationAccess.FeedbackInput)));
        var scores = new Row(Arrangement.SpacedBy(16), Alignment.Vertical.CenterVertically)
        {
            Modifier.FillMaxWidth(),
        };
        if (feedback.Accuracy > 0)
            scores.Add(TranslationScore(feedback.Accuracy, NativeTranslationText.Accuracy,
                NativeTranslationAccess.Accuracy, palette));
        if (feedback.Fluency > 0)
            scores.Add(TranslationScore(feedback.Fluency, NativeTranslationText.Fluency,
                NativeTranslationAccess.Fluency, palette));
        rows.Add(scores);
        if (!string.IsNullOrEmpty(feedback.RecommendedTranslation))
            rows.Add(Body($"{Localize(NativeTranslationText.Suggested)} {feedback.RecommendedTranslation}",
                palette, secondary: true, Modifier.TestTag(NativeTranslationAccess.Suggested)));
        if (feedback.HasDetails)
        {
            var label = Localize(NativeTranslationText.ShowDetails);
            var expanded = state.DetailsExpanded;
            var toggle = new TextButton(
                () => TranslationCommand((presenter, token) => presenter.ToggleDetailsAsync(token)))
            {
                Modifier = Modifier.TestTag(NativeTranslationAccess.DetailsToggle)
                    .Semantics(semantics =>
                    {
                        semantics.ContentDescription(label);
                        semantics.Selected(expanded);
                    }),
            };
            toggle.Add(new Icon(expanded ? Icons.Default.KeyboardArrowUp : Icons.Default.KeyboardArrowDown, null)
            {
                Modifier = Modifier.Size(20),
                Tint = palette.SecondaryText,
            });
            toggle.Add(new Text(label));
            rows.Add(toggle);
            if (expanded)
            {
                var details = new Column(Arrangement.SpacedBy(4))
                {
                    Modifier.FillMaxWidth().TestTag(NativeTranslationAccess.Details),
                };
                void Detail(string prefix, string value)
                {
                    if (!string.IsNullOrEmpty(value))
                        details.Add(Body($"{prefix} {value}", palette, secondary: true));
                }
                Detail($"{Localize(NativeTranslationText.Accuracy)}:", feedback.AccuracyExplanation);
                Detail($"{Localize(NativeTranslationText.Fluency)}:", feedback.FluencyExplanation);
                Detail(Localize(NativeTranslationText.Notes), feedback.GrammarNotes);
                rows.Add(details);
            }
        }
        return Card(palette, NativeTranslationAccess.Feedback, rows.ToArray());
    }

    private ComposableNode TranslationScore(double value, string key, string tag,
        AndroidVisualPalette palette)
    {
        var color = NativeTranslationFeedback.Tone(value) switch
        {
            NativeScoreTone.Success => palette.Success,
            NativeScoreTone.Warning => palette.WarningText,
            _ => palette.Danger
        };
        var percent = $"{(int)value}%";
        var label = $"{Localize(key)} {percent}";
        return new Column(Arrangement.SpacedBy(2))
        {
            Modifier.TestTag(tag).Semantics(semantics => semantics.ContentDescription(label)),
            new Text(percent) { Color = color, FontSize = new Sp(16), FontWeight = FontWeight.SemiBold },
            Body(Localize(key), palette, secondary: true),
        };
    }

    private ComposableNode TranslationInputBar(NativeTranslationState state, AndroidVisualPalette palette)
    {
        var bar = new Column(Arrangement.SpacedBy(8))
        {
            Modifier.FillMaxWidth()
                .Padding(horizontal: AndroidVisualTheme.PagePadding, vertical: 10)
                .TestTag("native.translation.input-bar"),
            new Text(state.Prompt)
            {
                Modifier = Modifier.FillMaxWidth().TestTag(NativeTranslationAccess.Prompt)
                    .Semantics(semantics => semantics.ContentDescription(state.Prompt)),
                Color = palette.Text,
                FontSize = new Sp(20),
                FontWeight = FontWeight.SemiBold,
            },
        };
        if (state.ShowsBlocks)
        {
            var blocks = new FlowRow { Modifier.FillMaxWidth().TestTag(NativeTranslationAccess.Blocks) };
            for (var index = 0; index < state.Blocks.Length; index++)
            {
                var position = index;
                var block = state.Blocks[index];
                var button = new OutlinedButton(
                    onClick: () => TranslationCommand((presenter, token) => presenter.UseBlockAsync(position, token)),
                    enabled: !state.IsGrading)
                {
                    Modifier = Modifier.Padding(end: 6, bottom: 6)
                        .TestTag($"{NativeTranslationAccess.Block}.{index}")
                        .Semantics(semantics => semantics.ContentDescription(block)),
                    Shape = Shape.RoundedCorners(AndroidVisualTheme.ControlRadius),
                };
                button.Add(new Text(block));
                blocks.Add(button);
            }
            bar.Add(blocks);
        }

        var placeholder = string.Format(CultureInfo.CurrentCulture,
            Localize(NativeTranslationText.TranslateTo), state.TargetLanguage);
        var keyboard = KeyboardOptionsCompanion.Default;
        bar.Add(new OutlinedTextField(TranslationAnswer, enabled: true, singleLine: true)
        {
            Modifier = Modifier.FillMaxWidth().TestTag(NativeTranslationAccess.Answer)
                .Semantics(semantics => semantics.ContentDescription(placeholder)),
            Label = new Text(placeholder),
            KeyboardOptions = keyboard.Copy(
                keyboard.Capitalization, keyboard.AutoCorrectEnabled,
                AndroidX.Compose.KeyboardType.Text,
                AndroidX.Compose.ImeAction.Done,
                keyboard.PlatformImeOptions, keyboard.ShowKeyboardOnFocus,
                keyboard.HintLocales),
            KeyboardActions = KeyboardActionsHelper.Create(onDone: () =>
                TranslationCommand((presenter, token) => presenter.GradeAsync(token))),
        });

        var toggleLabel = Localize(state.BlocksMode ? NativeTranslationText.Type : NativeTranslationText.Blocks);
        var toggle = new OutlinedButton(
            onClick: () => TranslationCommand((presenter, token) => presenter.ToggleInputModeAsync(token)),
            enabled: true)
        {
            Modifier = Modifier.Weight(1f).HeightIn(min: AndroidVisualTheme.ControlHeight)
                .TestTag(NativeTranslationAccess.ModeToggle)
                .Semantics(semantics => semantics.ContentDescription(toggleLabel)),
            Shape = Shape.RoundedCorners(AndroidVisualTheme.ControlRadius),
        };
        toggle.Add(new Text(toggleLabel));
        var gradeLabel = Localize(NativeTranslationText.Grade);
        var grade = new AndroidX.Compose.Button(
            onClick: () => TranslationCommand((presenter, token) => presenter.GradeAsync(token)),
            enabled: !state.IsGrading)
        {
            Modifier = Modifier.Weight(1f).HeightIn(min: AndroidVisualTheme.ControlHeight)
                .TestTag(state.IsGrading ? NativeTranslationAccess.Grading : NativeTranslationAccess.Grade)
                .Semantics(semantics => semantics.ContentDescription(gradeLabel)),
            Shape = Shape.RoundedCorners(AndroidVisualTheme.ControlRadius),
        };
        if (state.IsGrading)
            grade.Add(new CircularProgressIndicator { Modifier = Modifier.Size(20) });
        else
            grade.Add(new Text(gradeLabel));
        bar.Add(new Row(Arrangement.SpacedBy(8), Alignment.Vertical.CenterVertically)
        {
            Modifier.FillMaxWidth(),
            toggle,
            grade,
        });

        var previousLabel = Localize(NativeTranslationText.Previous);
        var previous = new IconButton(
            () => TranslationCommand((presenter, token) => presenter.PreviousAsync(token)),
            enabled: state.CanGoPrevious)
        {
            Modifier = Modifier.TestTag(NativeTranslationAccess.Previous)
                .Semantics(semantics => semantics.ContentDescription(previousLabel)),
        };
        previous.Add(new Icon(Icons.AutoMirrored.Default.KeyboardArrowLeft, previousLabel)
        {
            Modifier = Modifier.Size(24), Tint = palette.Text,
        });
        var nextLabel = Localize(NativeTranslationText.Next);
        var next = new IconButton(
            () => TranslationCommand((presenter, token) => presenter.NextAsync(token)),
            enabled: !state.IsGrading)
        {
            Modifier = Modifier.TestTag(NativeTranslationAccess.Next)
                .Semantics(semantics => semantics.ContentDescription(nextLabel)),
        };
        next.Add(new Icon(Icons.AutoMirrored.Default.KeyboardArrowRight, nextLabel)
        {
            Modifier = Modifier.Size(24), Tint = palette.Text,
        });
        bar.Add(new Row(Arrangement.SpaceBetween, Alignment.Vertical.CenterVertically)
        {
            Modifier.FillMaxWidth(),
            previous,
            Body(string.Format(CultureInfo.CurrentCulture, "{0} / {1}", state.Index + 1, state.Count),
                palette, secondary: true, Modifier.TestTag(NativeTranslationAccess.Position)),
            next,
        });
        var surface = new Surface
        {
            Modifier = Modifier.FillMaxWidth(),
            Color = palette.Card,
        };
        surface.Add(bar);
        return surface;
    }

    private ComposableNode TranslationSummary(NativeTranslationState state, AndroidVisualPalette palette)
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
        return Card(palette, NativeTranslationAccess.Summary,
            Heading(Localize(NativeTranslationText.SessionComplete), palette, 24,
                Modifier.Semantics(semantics => semantics.Heading())),
            new Row(Arrangement.SpacedBy(24))
            {
                Stat(state.GradedCount.ToString(CultureInfo.CurrentCulture), NativeTranslationText.Graded,
                    palette.Success, NativeTranslationAccess.SummaryGraded),
                Stat($"{state.AverageAccuracy}%", NativeTranslationText.AvgAccuracy,
                    palette.Primary, NativeTranslationAccess.SummaryAccuracy),
                Stat($"{state.AverageFluency}%", NativeTranslationText.AvgFluency,
                    palette.Accent, NativeTranslationAccess.SummaryFluency),
            },
            Action(Localize(NativeTranslationText.ContinuePractice), NativeTranslationAccess.Continue,
                () => TranslationCommand((presenter, token) => presenter.ContinueAsync(token)), !Busy.Value),
            Action(Localize(NativeTranslationText.Done), NativeTranslationAccess.Done,
                ExitActivity, !Busy.Value, primary: false));
    }
}
