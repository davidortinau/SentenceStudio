using System.Globalization;
using AndroidX.Compose;
using AndroidX.Compose.Runtime;
using AndroidX.Compose.UI.Text.Input;
using Microsoft.Extensions.DependencyInjection;
using SentenceStudio.Native;
using ComposeButton = AndroidX.Compose.Button;

namespace SentenceStudio.Native.Android;

/// <summary>
/// Compose Word Association (source WordAssociation.razor): word header,
/// clue list with grades, explanations, corrections and word score, and the
/// source input bar (clue field and add, Grade Clues, previous / position /
/// Next or Finish Round) pinned below the scrolling content; round summary
/// with high-score banner, Play Again and Done. The field binds a Compose
/// TextFieldValue state, so typing (including Korean IME composition) never
/// goes through a presenter publish. Uses only Compose members that already
/// have R8 keep rules.
/// </summary>
internal sealed partial class AndroidSliceShell
{
    private NativeWordAssociationPresenter? _wordAssociation;

    internal NativeWordAssociationState WordAssociation { get; private set; } = new(NativePhase.Loading);

    internal MutableState<TextFieldValue> WordAssociationDraft { get; } =
        new(ComposeExtensions.NewTextFieldValue(string.Empty));

    private async Task OpenWordAssociationAsync(NativeActivityLaunch launch, CancellationToken cancellationToken)
    {
        if (_scope is null)
            return;
        var presenter = _scope.ServiceProvider.GetRequiredService<NativeWordAssociationPresenter>();
        presenter.StateChanged += OnWordAssociationChanged;
        _wordAssociation = presenter;
        Publish(() => WordAssociation = new(NativePhase.Loading));
        var state = await presenter.OpenAsync(launch, cancellationToken);
        Publish(() => ApplyWordAssociation(state));
    }

    private async Task ExitWordAssociationAsync(CancellationToken cancellationToken)
    {
        if (_wordAssociation is { } presenter)
            await presenter.ExitAsync(cancellationToken);
    }

    private void ReleaseWordAssociation()
    {
        if (_wordAssociation is { } presenter)
        {
            presenter.StateChanged -= OnWordAssociationChanged;
            presenter.Dispose();
        }
        _wordAssociation = null;
        WordAssociationDraft.Value = ComposeExtensions.NewTextFieldValue(string.Empty);
    }

    private void OnWordAssociationChanged(NativeWordAssociationState state) =>
        Publish(() => ApplyWordAssociation(state));

    /// <summary>Only commands publish, so a differing draft came from an added clue or a word change.</summary>
    private void ApplyWordAssociation(NativeWordAssociationState state)
    {
        WordAssociation = state;
        if (!string.Equals(state.Draft, WordAssociationDraft.Value.Text, StringComparison.Ordinal))
            WordAssociationDraft.Value = ComposeExtensions.NewTextFieldValue(state.Draft);
    }

    /// <summary>Commands bypass the shell's busy gate so grading does not disable Back; the presenter serializes them.</summary>
    private void WordAssociationCommand(
        Func<NativeWordAssociationPresenter, CancellationToken, Task<NativeWordAssociationState>> command)
    {
        if (_wordAssociation is not { } presenter)
            return;
        presenter.SetDraft(WordAssociationDraft.Value.Text);
        _ = RunWordAssociationAsync(presenter, command);
    }

    private async Task RunWordAssociationAsync(NativeWordAssociationPresenter presenter,
        Func<NativeWordAssociationPresenter, CancellationToken, Task<NativeWordAssociationState>> command)
    {
        try
        {
            var state = await command(presenter, _lifetime.Token);
            Publish(() =>
            {
                if (ReferenceEquals(_wordAssociation, presenter))
                    ApplyWordAssociation(state);
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

    private ComposableNode RenderWordAssociation(AndroidVisualPalette palette)
    {
        var state = WordAssociation;
        var viewport = new Column(Arrangement.SpacedBy(12))
        {
            Modifier = Modifier.FillMaxWidth().Weight(1f)
                .VerticalScroll(_quizScroll)
                .Padding(horizontal: AndroidVisualTheme.PagePadding, vertical: 16)
                .TestTag(NativeWordAssociationAccess.Screen),
        };
        if (!state.Notices.IsDefaultOrEmpty
            && state.Phase is NativePhase.Ready or NativePhase.Empty or NativePhase.Summary)
            viewport.Add(WordAssociationNotices(state, palette));
        switch (state.Phase)
        {
            case NativePhase.Idle:
            case NativePhase.Loading:
                viewport.Add(new CircularProgressIndicator
                {
                    Modifier = Modifier.Size(28).TestTag(NativeWordAssociationAccess.Loading)
                });
                viewport.Add(Body(Localize(NativeWordAssociationText.Loading), palette, secondary: true));
                break;
            case NativePhase.Summary:
                viewport.Add(WordAssociationSummary(state, palette));
                break;
            case NativePhase.Ready:
                viewport.Add(WordAssociationHeader(state, palette));
                WordAssociationClues(viewport, state, palette);
                break;
            default:
                var empty = state.Phase == NativePhase.Empty;
                viewport.Add(Body(Localize(state.ErrorKey ?? NativeWordAssociationText.NoVocabulary), palette,
                    secondary: empty,
                    modifier: Modifier.TestTag(empty ? NativeWordAssociationAccess.Empty
                        : NativeWordAssociationAccess.Error)));
                viewport.Add(Action(Localize(NativeWordAssociationText.GoBack),
                    NativeWordAssociationAccess.EmptyBack, ExitActivity, !Busy.Value, primary: false));
                break;
        }

        var page = new Column
        {
            Modifier.FillMaxSize().TestTag("native.word-association.page"),
            ScreenHeader(Localize(NativeWordAssociationText.Title), palette,
                back: true, backAutomationId: NativeWordAssociationAccess.Back),
            viewport,
        };
        if (state.Phase == NativePhase.Ready)
            page.Add(WordAssociationInputBar(state, palette));
        return page;
    }

    private ComposableNode WordAssociationNotices(NativeWordAssociationState state, AndroidVisualPalette palette)
    {
        var list = new Column(Arrangement.SpacedBy(6))
        {
            Modifier.Weight(1f).TestTag(NativeWordAssociationAccess.Notices),
        };
        var index = 0;
        foreach (var notice in state.Notices)
            list.Add(StatusAlert(Localize(notice.Key), palette,
                error: notice.Kind == NativeWordAssociationNoticeKind.Error,
                success: notice.Kind == NativeWordAssociationNoticeKind.Success,
                automationId: $"{NativeWordAssociationAccess.Notice}.{index++}"));
        var dismissLabel = Localize(NativeWordAssociationText.Dismiss);
        var dismiss = new IconButton(
            () => WordAssociationCommand((presenter, token) => presenter.DismissNoticesAsync(token)))
        {
            Modifier = Modifier.TestTag(NativeWordAssociationAccess.DismissNotices)
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

    private ComposableNode WordAssociationHeader(NativeWordAssociationState state, AndroidVisualPalette palette)
    {
        var header = new Column(Arrangement.SpacedBy(4))
        {
            Modifier.FillMaxWidth(),
            Body(string.Format(CultureInfo.CurrentCulture, Localize(NativeWordAssociationText.Word),
                    state.Index + 1, state.WordCount),
                palette, secondary: true, modifier: Modifier.TestTag(NativeWordAssociationAccess.Progress)),
            Heading(state.TargetTerm, palette, 32,
                Modifier.TestTag(NativeWordAssociationAccess.Target).Semantics(semantics => semantics.Heading())),
        };
        if (!string.IsNullOrEmpty(state.NativeTerm))
            header.Add(Body(state.NativeTerm, palette, secondary: true,
                modifier: Modifier.TestTag(NativeWordAssociationAccess.NativeTerm)));
        return header;
    }

    private void WordAssociationClues(Column viewport, NativeWordAssociationState state,
        AndroidVisualPalette palette)
    {
        if (state.ShowsInstructions)
            viewport.Add(new Column(Arrangement.SpacedBy(4))
            {
                Modifier.FillMaxWidth().TestTag(NativeWordAssociationAccess.Instructions),
                Body(Localize(NativeWordAssociationText.WriteClues), palette, secondary: true),
                Body(Localize(NativeWordAssociationText.ClueBonus), palette, secondary: true),
            });
        if (!state.Clues.IsDefaultOrEmpty)
            foreach (var clue in state.Clues)
                viewport.Add(WordAssociationClue(clue, state, palette));
        if (state.IsGrading)
            viewport.Add(new Row(Arrangement.SpacedBy(8), Alignment.Vertical.CenterVertically)
            {
                Modifier.TestTag(NativeWordAssociationAccess.Grading),
                new CircularProgressIndicator { Modifier = Modifier.Size(18) },
                Body(Localize(NativeWordAssociationText.GradingClues), palette, secondary: true),
            });
        if (state.HasGraded)
            viewport.Add(new Text(string.Format(CultureInfo.CurrentCulture,
                Localize(NativeWordAssociationText.WordScore), state.WordScore, state.WordMax))
            {
                Modifier = Modifier.TestTag(NativeWordAssociationAccess.WordScore),
                Color = palette.Text,
                FontSize = new Sp(15),
                FontWeight = FontWeight.SemiBold,
            });
    }

    private ComposableNode WordAssociationClue(NativeWordAssociationClue clue,
        NativeWordAssociationState state, AndroidVisualPalette palette)
    {
        var id = clue.AccessibilityId;
        if (!clue.Graded)
        {
            var pendingLabel = Localize(NativeWordAssociationText.Pending);
            var removeLabel = $"{Localize(NativeWordAssociationText.RemoveClue)} {clue.Text}";
            var remove = new IconButton(
                () => WordAssociationCommand((presenter, token) => presenter.RemoveClueAsync(clue.Index, token)),
                enabled: !state.IsGrading)
            {
                Modifier = Modifier.TestTag($"{id}.remove")
                    .Semantics(semantics => semantics.ContentDescription(removeLabel)),
            };
            remove.Add(new Icon(Icons.Default.Close, removeLabel)
            {
                Modifier = Modifier.Size(20),
                Tint = palette.Danger,
            });
            return new Row(Arrangement.SpacedBy(8), Alignment.Vertical.CenterVertically)
            {
                Modifier.FillMaxWidth().TestTag(id),
                new Icon(Icons.Default.Info, pendingLabel)
                {
                    Modifier = Modifier.Size(20).TestTag($"{id}.status"),
                    Tint = palette.SecondaryText,
                },
                Body(clue.Text, palette, modifier: Modifier.Weight(1f).TestTag($"{id}.text")),
                remove,
            };
        }

        var statusLabel = Localize(clue.Related ? NativeWordAssociationText.Related
            : NativeWordAssociationText.NotRelated);
        var top = new Row(Arrangement.SpacedBy(6), Alignment.Vertical.CenterVertically)
        {
            Body(clue.Text, palette, modifier: Modifier.TestTag($"{id}.text")),
        };
        if (clue.ShowsDouble)
        {
            var doubleLabel = Localize(NativeWordAssociationText.DoublePoints);
            top.Add(new Text("2x")
            {
                Modifier = Modifier.TestTag($"{id}.double")
                    .Semantics(semantics => semantics.ContentDescription(doubleLabel)),
                Color = palette.Accent,
                FontSize = new Sp(12),
                FontWeight = FontWeight.SemiBold,
            });
        }
        var column = new Column(Arrangement.SpacedBy(2))
        {
            Modifier.Weight(1f),
            top,
        };
        if (!string.IsNullOrEmpty(clue.Explanation))
            column.Add(Body(clue.Explanation, palette, secondary: true,
                modifier: Modifier.TestTag($"{id}.explanation")));
        if (clue.ShowsCorrection)
            column.Add(Body(string.Format(CultureInfo.CurrentCulture,
                    Localize(NativeWordAssociationText.Corrected), clue.CorrectedText),
                palette, secondary: true, modifier: Modifier.TestTag($"{id}.corrected")));
        return new Row(Arrangement.SpacedBy(8))
        {
            Modifier.FillMaxWidth().TestTag(id),
            new Icon(clue.Related ? Icons.Default.CheckCircle : Icons.Default.Close, statusLabel)
            {
                Modifier = Modifier.Size(20).TestTag($"{id}.status"),
                Tint = clue.Related ? palette.Success : palette.Danger,
            },
            column,
        };
    }

    private ComposableNode WordAssociationInputBar(NativeWordAssociationState state, AndroidVisualPalette palette)
    {
        var hasText = !string.IsNullOrWhiteSpace(WordAssociationDraft.Value.Text);
        var placeholder = Localize(NativeWordAssociationText.TypeClue);
        var keyboard = KeyboardOptionsCompanion.Default;
        var field = new OutlinedTextField(WordAssociationDraft, enabled: !state.IsGrading, singleLine: true)
        {
            Modifier = Modifier.Weight(1f).TestTag(NativeWordAssociationAccess.Input)
                .Semantics(semantics => semantics.ContentDescription(placeholder)),
            Label = new Text(placeholder),
            KeyboardOptions = keyboard.Copy(
                keyboard.Capitalization, keyboard.AutoCorrectEnabled,
                AndroidX.Compose.KeyboardType.Text,
                AndroidX.Compose.ImeAction.Done,
                keyboard.PlatformImeOptions, keyboard.ShowKeyboardOnFocus,
                keyboard.HintLocales),
            KeyboardActions = KeyboardActionsHelper.Create(onDone: () =>
            {
                if (!string.IsNullOrWhiteSpace(WordAssociationDraft.Value.Text))
                    WordAssociationCommand((presenter, token) => presenter.AddClueAsync(token));
            }),
        };
        var addLabel = Localize(NativeWordAssociationText.AddClue);
        var add = new IconButton(
            () => WordAssociationCommand((presenter, token) => presenter.AddClueAsync(token)),
            enabled: hasText && !state.IsGrading)
        {
            Modifier = Modifier.TestTag(NativeWordAssociationAccess.Add)
                .Semantics(semantics => semantics.ContentDescription(addLabel)),
        };
        add.Add(new Icon(Icons.Default.Add, addLabel)
        {
            Modifier = Modifier.Size(24),
            Tint = hasText ? palette.Primary : palette.SecondaryText,
        });

        var bar = new Column(Arrangement.SpacedBy(8))
        {
            Modifier.FillMaxWidth()
                .Padding(horizontal: AndroidVisualTheme.PagePadding, vertical: 8)
                .TestTag("native.word-association.input-bar"),
            new Row(Arrangement.SpacedBy(8), Alignment.Vertical.CenterVertically)
            {
                Modifier.FillMaxWidth(),
                field,
                add,
            },
        };
        if (state.HasUngraded)
            bar.Add(Action(Localize(NativeWordAssociationText.GradeClues), NativeWordAssociationAccess.Grade,
                () => WordAssociationCommand((presenter, token) => presenter.GradeAsync(token)),
                state.CanGrade));

        var previousLabel = Localize(NativeWordAssociationText.Previous);
        var previous = new IconButton(
            () => WordAssociationCommand((presenter, token) => presenter.PreviousAsync(token)),
            enabled: state.CanPrevious)
        {
            Modifier = Modifier.TestTag(NativeWordAssociationAccess.Previous)
                .Semantics(semantics => semantics.ContentDescription(previousLabel)),
        };
        previous.Add(new Icon(Icons.AutoMirrored.Default.KeyboardArrowLeft, previousLabel)
        {
            Modifier = Modifier.Size(24),
            Tint = state.CanPrevious ? palette.Text : palette.SecondaryText,
        });
        var position = Body(string.Format(CultureInfo.CurrentCulture, "{0} / {1}",
                state.Index + 1, state.WordCount), palette, secondary: true,
            modifier: Modifier.Weight(1f).TestTag(NativeWordAssociationAccess.Position));
        var last = state.IsLastWord;
        var advanceLabel = Localize(last ? NativeWordAssociationText.FinishRound : NativeWordAssociationText.Next);
        ComposableNode advance;
        Action advanceCommand = last
            ? () => WordAssociationCommand((presenter, token) => presenter.FinishAsync(token))
            : () => WordAssociationCommand((presenter, token) => presenter.NextAsync(token));
        var advanceTag = last ? NativeWordAssociationAccess.Finish : NativeWordAssociationAccess.Next;
        if (last)
        {
            var finish = new ComposeButton(onClick: advanceCommand, enabled: state.CanAdvance)
            {
                Modifier = Modifier.TestTag(advanceTag)
                    .Semantics(semantics => semantics.ContentDescription(advanceLabel)),
                Shape = Shape.RoundedCorners(AndroidVisualTheme.ControlRadius),
            };
            finish.Add(new Text(advanceLabel));
            advance = finish;
        }
        else
        {
            var next = new OutlinedButton(onClick: advanceCommand, enabled: state.CanAdvance)
            {
                Modifier = Modifier.TestTag(advanceTag)
                    .Semantics(semantics => semantics.ContentDescription(advanceLabel)),
                Shape = Shape.RoundedCorners(AndroidVisualTheme.ControlRadius),
            };
            next.Add(new Text(advanceLabel));
            advance = next;
        }
        bar.Add(new Row(Arrangement.SpacedBy(8), Alignment.Vertical.CenterVertically)
        {
            Modifier.FillMaxWidth(),
            previous,
            position,
            advance,
        });
        return bar;
    }

    private ComposableNode WordAssociationSummary(NativeWordAssociationState state, AndroidVisualPalette palette)
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
        var children = new List<ComposableNode>
        {
            Heading(Localize(NativeWordAssociationText.RoundComplete), palette, 24,
                Modifier.Semantics(semantics => semantics.Heading())),
            new Row(Arrangement.SpacedBy(24))
            {
                Stat(state.TotalRelated.ToString(CultureInfo.CurrentCulture), NativeWordAssociationText.Related,
                    palette.Success, NativeWordAssociationAccess.SummaryRelated),
                Stat((state.TotalClues - state.TotalRelated).ToString(CultureInfo.CurrentCulture),
                    NativeWordAssociationText.NotRelated, palette.Danger,
                    NativeWordAssociationAccess.SummaryNotRelated),
                Stat(state.TotalScore.ToString(CultureInfo.CurrentCulture), NativeWordAssociationText.Score,
                    palette.Primary, NativeWordAssociationAccess.SummaryScore),
            },
        };
        if (state.HighScore != NativeWordAssociationHighScore.None)
        {
            var allTime = state.HighScore == NativeWordAssociationHighScore.AllTime;
            var label = Localize(allTime ? NativeWordAssociationText.NewAllTimeHigh
                : NativeWordAssociationText.BestWeek);
            children.Add(new Row(Arrangement.SpacedBy(6), Alignment.Vertical.CenterVertically)
            {
                Modifier.FillMaxWidth().TestTag(NativeWordAssociationAccess.HighScore),
                new Icon(allTime ? Icons.Default.Star : Icons.Default.DateRange, null)
                {
                    Modifier = Modifier.Size(20),
                    Tint = allTime ? palette.Success : palette.Accent,
                },
                Body(label, palette),
            });
        }
        children.Add(Action(Localize(NativeWordAssociationText.PlayAgain), NativeWordAssociationAccess.PlayAgain,
            () => WordAssociationCommand((presenter, token) => presenter.PlayAgainAsync(token)), !Busy.Value));
        children.Add(Action(Localize(NativeWordAssociationText.Done), NativeWordAssociationAccess.Done,
            ExitActivity, !Busy.Value, primary: false));
        return Card(palette, NativeWordAssociationAccess.Summary, [.. children]);
    }
}
