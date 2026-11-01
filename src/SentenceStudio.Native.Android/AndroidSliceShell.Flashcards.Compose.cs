using System.Globalization;
using AndroidX.Compose;
using Microsoft.Extensions.DependencyInjection;
using SentenceStudio.Native;

namespace SentenceStudio.Native.Android;

/// <summary>
/// Compose Flashcard Preview (source FlashcardActivity.razor), opened from the
/// Dashboard insight "Preview plan vocabulary". Tap flips the card; a
/// horizontal drag follows the finger with the source rotation and, released
/// at least 110 dp away, answers (right = Correct, left = Incorrect); the
/// Incorrect/Correct buttons do the same. In memory only, no timer.
/// </summary>
internal sealed partial class AndroidSliceShell
{
    private NativeFlashcardPresenter? _flashcards;
    private float _flashcardDragPx;

    internal NativeFlashcardState Flashcards { get; private set; } = new(NativePhase.Loading);

    /// <summary>Dashboard insight button (source OpenFlashcardActivity).</summary>
    private void OpenFlashcardPreview() => Execute(async cancellationToken =>
    {
        if (_scope is null || _preview || Destination != NativeDestination.Dashboard
            || Dashboard.Phase != NativePhase.Ready
            || Dashboard.PlanDate is not { } planDate
            || Dashboard.Narrative?.VocabInsight?.PreviewWords is not { Count: > 0 })
            return;
        var launch = _scope.ServiceProvider
            .GetRequiredService<NativeActivityLaunchService>()
            .FromPlanPreview(planDate);
        if (launch is null)
        {
            ShowError(Localize(NativeFlashcardText.Unavailable));
            return;
        }
        await StartActivityAsync(launch, cancellationToken);
    });

    private async Task OpenFlashcardsAsync(NativeActivityLaunch launch, CancellationToken cancellationToken)
    {
        if (ActivityServices is not { } services)
            return;
        var presenter = services.GetRequiredService<NativeFlashcardPresenter>();
        presenter.StateChanged += OnFlashcardsChanged;
        _flashcards = presenter;
        Publish(() =>
        {
            Flashcards = new(NativePhase.Loading);
            _flashcardDragPx = 0;
        });
        var state = await presenter.OpenAsync(launch, cancellationToken);
        Publish(() =>
        {
            if (ReferenceEquals(_flashcards, presenter))
                Flashcards = state;
        });
    }

    private async Task ExitFlashcardsAsync(CancellationToken cancellationToken)
    {
        if (_flashcards is { } presenter)
            await presenter.ExitAsync(cancellationToken);
    }

    private void ReleaseFlashcards()
    {
        if (_flashcards is { } presenter)
        {
            presenter.StateChanged -= OnFlashcardsChanged;
            presenter.Dispose();
        }
        _flashcards = null;
        _flashcardDragPx = 0;
    }

    private void OnFlashcardsChanged(NativeFlashcardState state) =>
        Publish(() => Flashcards = state);

    /// <summary>Card actions bypass the shell's busy gate so the 160 ms throw never disables Back.</summary>
    private void FlashcardCommand(
        Func<NativeFlashcardPresenter, CancellationToken, Task<NativeFlashcardState>> command)
    {
        if (_flashcards is not { } presenter)
            return;
        _ = RunFlashcardAsync(presenter, command);
    }

    private async Task RunFlashcardAsync(NativeFlashcardPresenter presenter,
        Func<NativeFlashcardPresenter, CancellationToken, Task<NativeFlashcardState>> command)
    {
        try
        {
            var state = await command(presenter, _lifetime.Token);
            Publish(() =>
            {
                if (ReferenceEquals(_flashcards, presenter))
                    Flashcards = state;
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

    private float FlashcardDensity => activity.Resources?.DisplayMetrics?.Density ?? 1f;

    private void OnFlashcardDragEnd()
    {
        var deltaDp = _flashcardDragPx / FlashcardDensity;
        Publish(() => _flashcardDragPx = 0);
        switch (NativeFlashcardGesture.Classify(deltaDp))
        {
            case NativeFlashcardRelease.Correct:
                FlashcardCommand((presenter, token) => presenter.AnswerAsync(true, token));
                break;
            case NativeFlashcardRelease.Incorrect:
                FlashcardCommand((presenter, token) => presenter.AnswerAsync(false, token));
                break;
            case NativeFlashcardRelease.Flip:
                FlashcardCommand((presenter, token) => presenter.FlipAsync(token));
                break;
        }
    }

    private ComposableNode RenderFlashcards(AndroidVisualPalette palette)
    {
        var state = Flashcards;
        var viewport = new Column(Arrangement.SpacedBy(12))
        {
            Modifier = Modifier.FillMaxWidth().Weight(1f)
                .VerticalScroll(_quizScroll)
                .Padding(horizontal: AndroidVisualTheme.PagePadding, vertical: 16)
                .TestTag(NativeFlashcardAccess.Screen),
        };
        switch (state.Phase)
        {
            case NativePhase.Idle:
            case NativePhase.Loading:
                viewport.Add(new CircularProgressIndicator
                {
                    Modifier = Modifier.Size(28).TestTag(NativeFlashcardAccess.Loading)
                });
                break;
            case NativePhase.Summary:
                viewport.Add(FlashcardSummary(state, palette));
                break;
            case NativePhase.Ready when state.Current is { } card:
                viewport.Add(FlashcardHeader(state, palette));
                viewport.Add(FlashcardCard(card, state, palette));
                viewport.Add(FlashcardAnswers(state, palette));
                break;
            default:
                var empty = state.Phase == NativePhase.Empty;
                viewport.Add(StatusAlert(
                    Localize(state.ErrorKey ?? NativeFlashcardText.NoVocabulary), palette,
                    error: !empty,
                    automationId: empty ? NativeFlashcardAccess.Empty : NativeFlashcardAccess.Error));
                viewport.Add(Action(Localize(NativeFlashcardText.GoBack),
                    NativeFlashcardAccess.EmptyBack, ExitActivity, true, primary: false));
                break;
        }

        return new Column
        {
            Modifier.FillMaxSize().TestTag("native.flashcards.page"),
            ScreenHeader(Localize(NativeFlashcardText.Title), palette,
                back: true, backAutomationId: NativeFlashcardAccess.Back),
            viewport,
        };
    }

    private ComposableNode FlashcardHeader(NativeFlashcardState state, AndroidVisualPalette palette)
    {
        var counter = string.Format(CultureInfo.CurrentCulture,
            Localize(NativeFlashcardText.CardCounter), state.CurrentIndex + 1, state.Total);
        var correct = $"{state.CorrectCount.ToString(CultureInfo.CurrentCulture)} {Localize(NativeFlashcardText.Correct)}";
        var incorrect = $"{state.IncorrectCount.ToString(CultureInfo.CurrentCulture)} {Localize(NativeFlashcardText.Incorrect)}";
        return new Row(Arrangement.SpaceBetween, Alignment.Vertical.CenterVertically)
        {
            Modifier.FillMaxWidth(),
            Body(counter, palette, secondary: true,
                modifier: Modifier.TestTag(NativeFlashcardAccess.Counter)),
            new Row(Arrangement.SpacedBy(4), Alignment.Vertical.CenterVertically)
            {
                new Text(correct)
                {
                    Modifier = Modifier.TestTag(NativeFlashcardAccess.CorrectTally),
                    Color = palette.Success, FontSize = new Sp(15),
                },
                Body("·", palette, secondary: true),
                new Text(incorrect)
                {
                    Modifier = Modifier.TestTag(NativeFlashcardAccess.IncorrectTally),
                    Color = palette.Danger, FontSize = new Sp(15),
                },
            },
        };
    }

    private ComposableNode FlashcardCard(NativeFlashcard card, NativeFlashcardState state,
        AndroidVisualPalette palette)
    {
        var term = state.IsFlipped ? card.NativeTerm : card.TargetTerm;
        // Drag follows the finger (px); the 160 ms throw moves the card off screen.
        var offsetPx = state.PendingAnswer is { } pending
            ? (float)(pending ? NativeFlashcardGesture.ThrowDistance : -NativeFlashcardGesture.ThrowDistance)
                * FlashcardDensity
            : _flashcardDragPx;
        var rotation = (float)NativeFlashcardGesture.RotationDegrees(offsetPx / FlashcardDensity);
        var flipLabel = Localize(NativeFlashcardText.Flip);
        var modifier = Modifier.FillMaxWidth().HeightIn(min: 280)
            .GraphicsLayer(translationX: offsetPx, rotationZ: rotation)
            .TestTag(NativeFlashcardAccess.Card);
        if (!state.IsAnimating)
            modifier = modifier
                .Clickable(() => FlashcardCommand((presenter, token) => presenter.FlipAsync(token)))
                .DetectDragGestures(
                    onDragStart: _ => _flashcardDragPx = 0,
                    onDrag: delta => Publish(() => _flashcardDragPx += delta.X),
                    onDragEnd: OnFlashcardDragEnd,
                    onDragCancel: () => Publish(() => _flashcardDragPx = 0),
                    key: $"flashcard-{card.Index}");
        modifier = modifier.Semantics(semantics =>
        {
            semantics.ContentDescription(term);
            semantics.StateDescription(flipLabel);
            semantics.Role(SemanticsRole.Button);
        });
        var surface = new Surface
        {
            Modifier = modifier,
            Shape = Shape.RoundedCorners(AndroidVisualTheme.CardRadius),
            Color = palette.Card,
        };
        surface.Add(new Box
        {
            Modifier.FillMaxWidth().HeightIn(min: 280),
            new Text(term)
            {
                Modifier = Modifier.Align(Alignment.Center).FillMaxWidth().Padding(24),
                Color = palette.Primary,
                FontSize = new Sp(36),
                FontWeight = FontWeight.SemiBold,
                Align = TextAlign.Center,
            },
        });
        return surface;
    }

    private ComposableNode FlashcardAnswers(NativeFlashcardState state, AndroidVisualPalette palette)
    {
        ComposableNode Answer(bool correct)
        {
            var label = Localize(correct ? NativeFlashcardText.Correct : NativeFlashcardText.Incorrect);
            var button = new OutlinedButton(
                onClick: () => FlashcardCommand((presenter, token) => presenter.AnswerAsync(correct, token)),
                enabled: !state.IsAnimating)
            {
                Modifier = Modifier.Weight(1f).HeightIn(min: AndroidVisualTheme.ControlHeight)
                    .TestTag(correct ? NativeFlashcardAccess.Correct : NativeFlashcardAccess.Incorrect)
                    .Semantics(semantics => semantics.ContentDescription(label)),
                Shape = Shape.RoundedCorners(AndroidVisualTheme.ControlRadius),
            };
            var color = correct ? palette.Success : palette.Danger;
            var icon = new Icon(correct ? Icons.AutoMirrored.Default.KeyboardArrowRight
                    : Icons.AutoMirrored.Default.KeyboardArrowLeft, null)
            {
                Modifier = Modifier.Size(18),
                Tint = color,
            };
            var text = new Text(label) { Color = color };
            var content = new Row(Arrangement.SpacedBy(4), Alignment.Vertical.CenterVertically);
            if (correct)
            {
                content.Add(text);
                content.Add(icon);
            }
            else
            {
                content.Add(icon);
                content.Add(text);
            }
            button.Add(content);
            return button;
        }
        return new Row(Arrangement.SpacedBy(8), Alignment.Vertical.CenterVertically)
        {
            Modifier.FillMaxWidth(),
            Answer(false),
            Answer(true),
        };
    }

    private ComposableNode FlashcardSummary(NativeFlashcardState state, AndroidVisualPalette palette)
    {
        ComposableNode Stat(int value, string key, Color color, string tag)
        {
            var label = $"{Localize(key)} {value}";
            return new Column(Arrangement.SpacedBy(2), Alignment.Horizontal.CenterHorizontally)
            {
                Modifier.TestTag(tag).Semantics(semantics => semantics.ContentDescription(label)),
                new Text(value.ToString(CultureInfo.CurrentCulture))
                {
                    Color = color, FontSize = new Sp(28), FontWeight = FontWeight.SemiBold,
                },
                Body(Localize(key), palette, secondary: true),
            };
        }
        var results = state.Results.IsDefault ? [] : state.Results;
        var children = new List<ComposableNode>
        {
            new Row(Arrangement.SpaceBetween, Alignment.Vertical.CenterVertically)
            {
                Modifier.FillMaxWidth(),
                Stat(state.CorrectCount, NativeFlashcardText.Correct, palette.Success,
                    NativeFlashcardAccess.SummaryCorrect),
                Stat(state.IncorrectCount, NativeFlashcardText.Incorrect, palette.Danger,
                    NativeFlashcardAccess.SummaryIncorrect),
                Stat(results.Length, NativeFlashcardText.Total, palette.Text,
                    NativeFlashcardAccess.SummaryTotal),
            },
            new HorizontalDivider { Color = palette.Border },
        };
        foreach (var result in results)
        {
            var status = Localize(result.WasCorrect ? NativeFlashcardText.Correct : NativeFlashcardText.Incorrect);
            var description = $"{result.Card.TargetTerm}, {result.Card.NativeTerm}, {status}";
            children.Add(new Row(Arrangement.SpacedBy(8), Alignment.Vertical.CenterVertically)
            {
                Modifier.FillMaxWidth().Padding(vertical: 4)
                    .TestTag($"{NativeFlashcardAccess.SummaryRow}.{result.Card.Index}")
                    .Semantics(semantics => semantics.ContentDescription(description)),
                new Icon(result.WasCorrect ? Icons.Default.CheckCircle : Icons.Default.Close, null)
                {
                    Modifier = Modifier.Size(20),
                    Tint = result.WasCorrect ? palette.Success : palette.Danger,
                },
                new Text(result.Card.TargetTerm)
                {
                    Color = palette.Primary, FontSize = new Sp(16), FontWeight = FontWeight.Bold,
                },
                Body("→", palette, secondary: true),
                Body(result.Card.NativeTerm, palette),
            });
        }
        return new Column(Arrangement.SpacedBy(12), Alignment.Horizontal.CenterHorizontally)
        {
            Modifier.FillMaxWidth().TestTag(NativeFlashcardAccess.Summary),
            Heading(Localize(NativeFlashcardText.Summary), palette, 24,
                Modifier.Semantics(semantics => semantics.Heading())),
            Card(palette, null, children.ToArray()),
            Action(Localize(NativeFlashcardText.Done), NativeFlashcardAccess.Done,
                ExitActivity, true, primary: false),
        };
    }
}
