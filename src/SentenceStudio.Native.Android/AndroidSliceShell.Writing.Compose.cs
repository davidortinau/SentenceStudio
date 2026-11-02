using System.Globalization;
using AndroidX.Compose;
using AndroidX.Compose.Runtime;
using AndroidX.Compose.UI.Text.Input;
using Microsoft.Extensions.DependencyInjection;
using SentenceStudio.Native;

namespace SentenceStudio.Native.Android;

/// <summary>
/// Compose Writing (source Writing.razor): graded sentence cards, four
/// vocabulary blocks with shuffle, and the source input bar (sentence field,
/// translate, grade, count, End session) pinned below the scrolling content.
/// The field binds a Compose TextFieldValue state, so typing (including
/// Korean IME composition) never goes through a presenter publish. Uses only
/// Compose members that already have R8 keep rules.
/// </summary>
internal sealed partial class AndroidSliceShell
{
    private NativeWritingPresenter? _writing;
    private readonly HashSet<int> _writingExpanded = [];

    internal NativeWritingState Writing { get; private set; } = new(NativePhase.Loading);

    internal MutableState<TextFieldValue> WritingDraft { get; } =
        new(ComposeExtensions.NewTextFieldValue(string.Empty));

    private async Task OpenWritingAsync(NativeActivityLaunch launch, CancellationToken cancellationToken)
    {
        if (ActivityServices is not { } services)
            return;
        var presenter = services.GetRequiredService<NativeWritingPresenter>();
        presenter.StateChanged += OnWritingChanged;
        _writing = presenter;
        Publish(() => Writing = new(NativePhase.Loading));
        var state = await presenter.OpenAsync(launch, cancellationToken);
        Publish(() =>
        {
            if (ReferenceEquals(_writing, presenter))
                ApplyWriting(state);
        });
    }

    private async Task ExitWritingAsync(CancellationToken cancellationToken)
    {
        if (_writing is { } presenter)
            await presenter.ExitAsync(cancellationToken);
    }

    private void ReleaseWriting()
    {
        if (_writing is { } presenter)
        {
            presenter.StateChanged -= OnWritingChanged;
            presenter.Dispose();
        }
        _writing = null;
        _writingExpanded.Clear();
        WritingDraft.Value = ComposeExtensions.NewTextFieldValue(string.Empty);
    }

    private void OnWritingChanged(NativeWritingState state) =>
        Publish(() => ApplyWriting(state));

    /// <summary>Only commands publish, so a differing draft came from a block tap or a cleared grade.</summary>
    private void ApplyWriting(NativeWritingState state)
    {
        Writing = state;
        if (state.Sentences.IsDefaultOrEmpty)
            _writingExpanded.Clear();
        if (!string.Equals(state.Draft, WritingDraft.Value.Text, StringComparison.Ordinal))
            WritingDraft.Value = ComposeExtensions.NewTextFieldValue(state.Draft);
    }

    /// <summary>Writing commands bypass the shell's busy gate so grading does not disable Back; the presenter serializes them.</summary>
    private void WritingCommand(Func<NativeWritingPresenter, CancellationToken, Task<NativeWritingState>> command)
    {
        if (_writing is not { } presenter)
            return;
        presenter.SetDraft(WritingDraft.Value.Text);
        _ = RunWritingAsync(presenter, command);
    }

    private async Task RunWritingAsync(NativeWritingPresenter presenter,
        Func<NativeWritingPresenter, CancellationToken, Task<NativeWritingState>> command)
    {
        try
        {
            var state = await command(presenter, _lifetime.Token);
            Publish(() =>
            {
                if (ReferenceEquals(_writing, presenter))
                    ApplyWriting(state);
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

    private ComposableNode RenderWriting(AndroidVisualPalette palette)
    {
        var state = Writing;
        var viewport = new Column(Arrangement.SpacedBy(12))
        {
            Modifier = Modifier.FillMaxWidth().Weight(1f)
                .VerticalScroll(_quizScroll)
                .Padding(horizontal: AndroidVisualTheme.PagePadding, vertical: 16)
                .TestTag(NativeWritingAccess.Screen),
        };
        if (!state.Notices.IsDefaultOrEmpty && state.Phase is NativePhase.Ready or NativePhase.Empty)
            if (WritingNotices(state, palette) is { } notices)
                viewport.Add(notices);
        switch (state.Phase)
        {
            case NativePhase.Idle:
            case NativePhase.Loading:
                viewport.Add(new CircularProgressIndicator
                {
                    Modifier = Modifier.Size(28).TestTag(NativeWritingAccess.Loading)
                });
                viewport.Add(Body(Localize(NativeWritingText.Loading), palette, secondary: true));
                break;
            case NativePhase.Summary:
                viewport.Add(WritingSummary(state, palette));
                break;
            case NativePhase.Ready:
                if (!state.Sentences.IsDefaultOrEmpty)
                    foreach (var sentence in state.Sentences)
                        viewport.Add(WritingSentence(sentence, palette));
                viewport.Add(WritingBlocks(state, palette));
                break;
            default:
                var empty = state.Phase == NativePhase.Empty;
                viewport.Add(Body(Localize(state.ErrorKey ?? NativeWritingText.NoVocabulary), palette,
                    secondary: empty,
                    modifier: Modifier.TestTag(empty ? NativeWritingAccess.Empty : NativeWritingAccess.Error)));
                viewport.Add(Action(Localize(NativeWritingText.GoBack),
                    NativeWritingAccess.EmptyBack, ExitActivity, !Busy.Value, primary: false));
                break;
        }

        var page = new Column
        {
            Modifier.FillMaxSize().TestTag("native.writing.page"),
            ScreenHeader(Localize(NativeWritingText.Title), palette,
                back: true, backAutomationId: NativeWritingAccess.Back),
            viewport,
        };
        if (state.Phase == NativePhase.Ready)
            page.Add(WritingInputBar(state, palette));
        return page;
    }

    /// <summary>Errors and warnings stay inline until the next action; success and info are the shell's snackbar.</summary>
    private ComposableNode? WritingNotices(NativeWritingState state, AndroidVisualPalette palette) =>
        MessageBanners(state.Notices, palette, index => $"{NativeWritingAccess.Notice}.{index}",
            () => WritingCommand((presenter, token) => presenter.DismissNoticesAsync(token)),
            NativeWritingAccess.DismissNotices, Localize(NativeWritingText.Dismiss));

    private ComposableNode WritingSentence(NativeWritingSentence sentence, AndroidVisualPalette palette)
    {
        var id = sentence.AccessibilityId;
        var answer = new Column(Arrangement.SpacedBy(4))
        {
            Modifier.Weight(1f),
            Body(sentence.Answer, palette, modifier: Modifier.TestTag($"{id}.answer")),
        };
        if (!string.IsNullOrEmpty(sentence.RecommendedSentence))
            answer.Add(Body($"{Localize(NativeWritingText.Suggested)} {sentence.RecommendedSentence}",
                palette, secondary: true, modifier: Modifier.TestTag($"{id}.suggested")));
        var top = new Row(Arrangement.SpacedBy(12))
        {
            Modifier.FillMaxWidth(),
            answer,
        };
        if (sentence.Accuracy > 0)
            top.Add(WritingScore(sentence.Accuracy, Localize(NativeWritingText.Accuracy),
                $"{id}.accuracy", palette));
        if (sentence.Fluency > 0)
            top.Add(WritingScore(sentence.Fluency, Localize(NativeWritingText.Fluency),
                $"{id}.fluency", palette));

        var children = new List<ComposableNode> { top };
        if (sentence.HasDetails)
        {
            var expanded = _writingExpanded.Contains(sentence.Index);
            var label = Localize(NativeWritingText.ShowDetails);
            var toggle = new Row(Arrangement.SpacedBy(4), Alignment.Vertical.CenterVertically)
            {
                Modifier.TestTag($"{id}.details.toggle")
                    .Clickable(() => Publish(() =>
                    {
                        if (!_writingExpanded.Remove(sentence.Index))
                            _writingExpanded.Add(sentence.Index);
                    }))
                    .Semantics(semantics =>
                    {
                        semantics.ContentDescription(label);
                        semantics.Role(SemanticsRole.Button);
                        semantics.Selected(expanded);
                    }),
                new Icon(expanded ? Icons.Default.KeyboardArrowUp : Icons.Default.KeyboardArrowDown, null)
                {
                    Modifier = Modifier.Size(18),
                    Tint = palette.SecondaryText,
                },
                Body(label, palette, secondary: true),
            };
            children.Add(toggle);
            if (expanded)
            {
                var details = new Column(Arrangement.SpacedBy(4))
                {
                    Modifier.FillMaxWidth().TestTag($"{id}.details"),
                };
                if (!string.IsNullOrEmpty(sentence.AccuracyExplanation))
                    details.Add(Body($"{Localize(NativeWritingText.Accuracy)}: {sentence.AccuracyExplanation}",
                        palette, secondary: true));
                if (!string.IsNullOrEmpty(sentence.FluencyExplanation))
                    details.Add(Body($"{Localize(NativeWritingText.Fluency)}: {sentence.FluencyExplanation}",
                        palette, secondary: true));
                details.Add(Body($"{Localize(NativeWritingText.Notes)} {sentence.GrammarNotes}",
                    palette, secondary: true));
                children.Add(details);
            }
        }
        return Card(palette, id, [.. children]);
    }

    /// <summary>Source GetScoreColor: >= 80 success, >= 50 warning, else danger.</summary>
    private static ComposableNode WritingScore(double value, string label, string id,
        AndroidVisualPalette palette)
    {
        var percent = $"{(int)value}%";
        var description = $"{label} {percent}";
        return new Column(Arrangement.SpacedBy(2))
        {
            Modifier.TestTag(id).Semantics(semantics => semantics.ContentDescription(description)),
            new Text(percent)
            {
                Color = value >= 80 ? palette.Success : value >= 50 ? palette.WarningText : palette.Danger,
                FontSize = new Sp(15),
                FontWeight = FontWeight.SemiBold,
            },
            Body(label, palette, secondary: true),
        };
    }

    private ComposableNode WritingBlocks(NativeWritingState state, AndroidVisualPalette palette)
    {
        var blocks = state.Blocks.IsDefault ? [] : state.Blocks;
        var cells = blocks.Select(block => WritingBlock(block)).ToList();
        var shuffleLabel = Localize(NativeWritingText.Shuffle);
        var shuffle = new IconButton(
            () => WritingCommand((presenter, token) => presenter.ShuffleAsync(token)))
        {
            Modifier = Modifier.TestTag(NativeWritingAccess.Shuffle)
                .Semantics(semantics => semantics.ContentDescription(shuffleLabel)),
        };
        shuffle.Add(new Icon(Icons.Default.Refresh, shuffleLabel)
        {
            Modifier = Modifier.Size(24),
            Tint = palette.Text,
        });
        cells.Add(shuffle);

        var section = new Column(Arrangement.SpacedBy(8))
        {
            Modifier.FillMaxWidth().TestTag(NativeWritingAccess.ChooseVocab),
            Body(Localize(NativeWritingText.ChooseVocab), palette, secondary: true),
        };
        const int columns = 3;
        for (var start = 0; start < cells.Count; start += columns)
        {
            var row = new Row(Arrangement.SpacedBy(8), Alignment.Vertical.CenterVertically)
            {
                Modifier.FillMaxWidth(),
            };
            foreach (var cell in cells.Skip(start).Take(columns))
                row.Add(cell);
            section.Add(row);
        }
        return section;
    }

    private ComposableNode WritingBlock(NativeWritingBlock block)
    {
        var button = new OutlinedButton(
            onClick: () => WritingCommand((presenter, token) => presenter.UseBlockAsync(block.Index, token)),
            enabled: true)
        {
            Modifier = Modifier.TestTag(block.AccessibilityId)
                .Semantics(semantics => semantics.ContentDescription(block.Text)),
            Shape = Shape.RoundedCorners(AndroidVisualTheme.ControlRadius),
        };
        button.Add(new Text(block.Text));
        return button;
    }

    private ComposableNode WritingInputBar(NativeWritingState state, AndroidVisualPalette palette)
    {
        var hasText = !string.IsNullOrWhiteSpace(WritingDraft.Value.Text);
        var placeholder = Localize(NativeWritingText.WriteSentence);
        var keyboard = KeyboardOptionsCompanion.Default;
        var field = new OutlinedTextField(WritingDraft, enabled: true, singleLine: true)
        {
            Modifier = Modifier.Weight(1f).TestTag(NativeWritingAccess.Input)
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
                if (!string.IsNullOrWhiteSpace(WritingDraft.Value.Text))
                    WritingCommand((presenter, token) => presenter.GradeAsync(token));
            }),
        };
        var translateLabel = Localize(NativeWritingText.Translate);
        var translate = new OutlinedButton(
            onClick: () => WritingCommand((presenter, token) => presenter.TranslateAsync(token)),
            enabled: hasText && !state.IsTranslating)
        {
            Modifier = Modifier.TestTag(NativeWritingAccess.Translate)
                .Semantics(semantics => semantics.ContentDescription(translateLabel)),
            Shape = Shape.RoundedCorners(AndroidVisualTheme.ControlRadius),
        };
        translate.Add(new Text(translateLabel));
        var gradeLabel = Localize(NativeWritingText.Grade);
        var grade = new IconButton(
            () => WritingCommand((presenter, token) => presenter.GradeAsync(token)),
            enabled: hasText && !state.IsGrading)
        {
            Modifier = Modifier.TestTag(NativeWritingAccess.Grade)
                .Semantics(semantics => semantics.ContentDescription(gradeLabel)),
        };
        if (state.IsGrading)
            grade.Add(new CircularProgressIndicator { Modifier = Modifier.Size(20) });
        else
            grade.Add(new Icon(Icons.AutoMirrored.Default.Send, gradeLabel)
            {
                Modifier = Modifier.Size(24),
                Tint = hasText ? palette.Primary : palette.SecondaryText,
            });

        var count = Body(string.Format(CultureInfo.CurrentCulture,
                Localize(NativeWritingText.SentencesWritten),
                state.Sentences.IsDefault ? 0 : state.Sentences.Length),
            palette, secondary: true, modifier: Modifier.Weight(1f).TestTag(NativeWritingAccess.Count));
        var footer = new Row(Arrangement.SpacedBy(8), Alignment.Vertical.CenterVertically)
        {
            Modifier.FillMaxWidth(),
            count,
        };
        if (state.CanEndSession)
        {
            var end = new OutlinedButton(
                onClick: () => WritingCommand((presenter, token) => presenter.EndSessionAsync(token)),
                enabled: true)
            {
                Modifier = Modifier.TestTag(NativeWritingAccess.EndSession),
                Shape = Shape.RoundedCorners(AndroidVisualTheme.ControlRadius),
            };
            end.Add(new Text(Localize(NativeWritingText.EndSession)));
            footer.Add(end);
        }
        return new Column(Arrangement.SpacedBy(8))
        {
            Modifier.FillMaxWidth()
                .Padding(horizontal: AndroidVisualTheme.PagePadding, vertical: 8)
                .TestTag("native.writing.input-bar"),
            new Row(Arrangement.SpacedBy(8), Alignment.Vertical.CenterVertically)
            {
                Modifier.FillMaxWidth(),
                field,
                translate,
                grade,
            },
            footer,
        };
    }

    private ComposableNode WritingSummary(NativeWritingState state, AndroidVisualPalette palette)
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
        return Card(palette, NativeWritingAccess.Summary,
            Heading(Localize(NativeWritingText.SessionComplete), palette, 24,
                Modifier.Semantics(semantics => semantics.Heading())),
            new Row(Arrangement.SpacedBy(24))
            {
                Stat(state.GradedCount.ToString(CultureInfo.CurrentCulture), NativeWritingText.Graded,
                    palette.Success, NativeWritingAccess.SummaryGraded),
                Stat($"{state.AverageAccuracy}%", NativeWritingText.AvgAccuracy,
                    palette.Primary, NativeWritingAccess.SummaryAccuracy),
                Stat($"{state.AverageFluency}%", NativeWritingText.AvgFluency,
                    palette.Accent, NativeWritingAccess.SummaryFluency),
            },
            Action(Localize(NativeWritingText.ContinuePractice), NativeWritingAccess.Continue,
                () => WritingCommand((presenter, token) => presenter.ContinueAsync(token)),
                !Busy.Value && state.CanContinue),
            Action(Localize(NativeWritingText.Done), NativeWritingAccess.Done,
                ExitActivity, !Busy.Value, primary: false));
    }
}
