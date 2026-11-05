using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.SwiftUI;
using SentenceStudio.Native.iOS.Styling;
using Views = Microsoft.SwiftUI.SwiftUI;

namespace SentenceStudio.Native.iOS.Features.Activities;

/// <summary>
/// SwiftUI Writing (source Writing.razor). The scrolling content holds the
/// graded sentence cards and the vocabulary blocks; the source
/// activity-input-bar (sentence field, translate, grade, count, End session)
/// is a bottom inset. Source toasts are shown inline above the content.
/// Typing never re-renders, so Korean IME composition is not interrupted.
/// </summary>
internal sealed class WritingScreen : IActivityScreen
{
    private readonly ActivityScreenHost _host;
    private readonly NativeWritingPresenter _presenter;
    private readonly HashSet<int> _expanded = [];
    private NativeWritingState _state = new(NativePhase.Loading);
    private SwiftView? _grade;
    private SwiftView? _translate;

    public WritingScreen(ActivityScreenHost host)
    {
        _host = host;
        _presenter = host.Services.GetRequiredService<NativeWritingPresenter>();
        _presenter.StateChanged += OnStateChanged;
    }

    public string TitleKey => NativeWritingText.Title;

    public NativeMessage? Toast => NativeMessages.LatestToast(_state.Notices);

    public bool HasExited => _state.Destination == NativeDestination.TodaysPlan;

    public async Task OpenAsync(NativeActivityLaunch launch, CancellationToken cancellationToken) =>
        _state = await _presenter.OpenAsync(launch, cancellationToken);

    public async Task ExitAsync(CancellationToken cancellationToken) =>
        _state = await _presenter.ExitAsync(cancellationToken);

    private void OnStateChanged(NativeWritingState state)
    {
        if (state.Sentences.IsDefaultOrEmpty)
            _expanded.Clear();
        _state = state;
        _host.Publish();
    }

    /// <summary>Commands do not take the controller's busy gate: grading can take seconds and must not disable Back; the presenter serializes them.</summary>
    private void Fire(Func<CancellationToken, Task<NativeWritingState>> action) =>
        _ = FireAsync(action);

    private async Task FireAsync(Func<CancellationToken, Task<NativeWritingState>> action)
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
            _host.Logger?.LogError(ex, "Native iOS Writing action failed.");
            _state = _state with
            {
                Notices = [new(NativeMessageKind.Error, NativeText.UnexpectedError)]
            };
        }
        _host.Publish();
    }

    public SwiftView Render(SourceUiStyle style)
    {
        var text = _host.Text;
        var state = _state;
        var items = new List<SwiftView>();
        _grade = null;
        _translate = null;
        if (!state.Notices.IsDefaultOrEmpty && state.Phase is NativePhase.Ready or NativePhase.Empty)
            if (Notices(state, style, text) is { } notices)
                items.Add(notices);
        switch (state.Phase)
        {
            case NativePhase.Idle:
            case NativePhase.Loading:
                items.Add(Views.IndeterminateProgress(text(NativeWritingText.Loading))
                    .WithAccessibilityIdentifier(NativeWritingAccess.Loading));
                items.Add(style.Text(Views.Text(text(NativeWritingText.Loading)),
                    SourceTextRole.Body1, SourceTextTone.Secondary));
                break;
            case NativePhase.Summary:
                items.Add(Summary(state, style, text));
                break;
            case NativePhase.Ready:
                if (!state.Sentences.IsDefaultOrEmpty)
                {
                    var list = Views.VStack(state.Sentences
                            .Select(sentence => SentenceCard(sentence, style, text)).ToArray())
                        .WithFlexibleWidth()
                        .WithAccessibilityIdentifier(NativeWritingAccess.Sentences);
                    list.Spacing = 8;
                    items.Add(list);
                }
                items.Add(Blocks(state, style, text));
                break;
            default:
                // Empty and Blocked offer only Go Back (source empty state).
                var empty = state.Phase == NativePhase.Empty;
                items.Add(style.Text(Views.Text(text(state.ErrorKey ?? NativeWritingText.NoVocabulary)),
                        SourceTextRole.Body1, empty ? SourceTextTone.Secondary : SourceTextTone.Danger)
                    .WithAccessibilityIdentifier(empty ? NativeWritingAccess.Empty : NativeWritingAccess.Error));
                items.Add(style.SecondaryButton(text(NativeWritingText.GoBack), _host.Exit)
                    .WithAccessibilityIdentifier(NativeWritingAccess.EmptyBack));
                break;
        }

        var gutter = SourceUiLayout.PageGutter(style.ViewportWidth);
        var content = Views.VStack(items.ToArray())
            .WithPadding(16, gutter, 16, gutter)
            .WithFlexibleWidth()
            .WithAccessibilityIdentifier("native.writing.content");
        content.Spacing = SourceUiLayout.PageGap;
        var page = Views.Scroll(content).WithAccessibilityIdentifier(NativeWritingAccess.Screen);
        return style.Page(state.Phase == NativePhase.Ready
            ? Views.BottomInset(page, InputBar(state, style, text))
            : page);
    }

    /// <summary>Errors and warnings stay inline until the next action; success and info are the scene's toast.</summary>
    private SwiftView? Notices(NativeWritingState state, SourceUiStyle style, Func<string, string> text)
    {
        return style.MessageBanners(state.Notices, text, index => $"{NativeWritingAccess.Notice}.{index}",
            () => Fire(_presenter.DismissNoticesAsync), NativeWritingAccess.DismissNotices,
            text(NativeWritingText.Dismiss));
    }

    private SwiftView SentenceCard(NativeWritingSentence sentence, SourceUiStyle style,
        Func<string, string> text)
    {
        var id = sentence.AccessibilityId;
        var left = new List<SwiftView>
        {
            style.Text(Views.Text(sentence.Answer), SourceTextRole.Body1)
                .WithAccessibilityIdentifier($"{id}.answer")
        };
        if (!string.IsNullOrEmpty(sentence.RecommendedSentence))
            left.Add(Views.HStack(
                    style.Text(Views.Text(text(NativeWritingText.Suggested)), SourceTextRole.SmallStrong,
                        SourceTextTone.Secondary),
                    style.Text(Views.Text(sentence.RecommendedSentence), SourceTextRole.Small,
                        SourceTextTone.Secondary))
                .WithAccessibilityIdentifier($"{id}.suggested"));
        var answer = Views.VStack(left.ToArray()).WithFlexibleWidth();
        answer.Spacing = 4;

        var scores = new List<SwiftView>();
        if (sentence.Accuracy > 0)
            scores.Add(Score(sentence.Accuracy, text(NativeWritingText.Accuracy), $"{id}.accuracy", style));
        if (sentence.Fluency > 0)
            scores.Add(Score(sentence.Fluency, text(NativeWritingText.Fluency), $"{id}.fluency", style));
        var scoreRow = Views.HStack(scores.ToArray());
        scoreRow.Spacing = 16;
        var top = Views.HStack(answer, scoreRow);
        top.Spacing = 16;

        var rows = new List<SwiftView> { top };
        if (sentence.HasDetails)
        {
            var expanded = _expanded.Contains(sentence.Index);
            var toggle = Views.Button(
                    Views.HStack(
                        Views.Image(expanded ? "chevron.down" : "chevron.right"),
                        style.Text(Views.Text(text(NativeWritingText.ShowDetails)),
                            SourceTextRole.Caption1, SourceTextTone.Secondary)),
                    () =>
                    {
                        if (!_expanded.Remove(sentence.Index))
                            _expanded.Add(sentence.Index);
                        _host.Publish();
                    })
                .WithButtonStyle(SwiftButtonStyle.Plain)
                .WithAccessibilitySelected(expanded)
                .WithAccessibilityIdentifier($"{id}.details.toggle");
            rows.Add(toggle);
            if (expanded)
            {
                var details = new List<SwiftView>();
                if (!string.IsNullOrEmpty(sentence.AccuracyExplanation))
                    details.Add(Detail($"{text(NativeWritingText.Accuracy)}:", sentence.AccuracyExplanation, style));
                if (!string.IsNullOrEmpty(sentence.FluencyExplanation))
                    details.Add(Detail($"{text(NativeWritingText.Fluency)}:", sentence.FluencyExplanation, style));
                details.Add(Detail(text(NativeWritingText.Notes), sentence.GrammarNotes!, style));
                var detailStack = Views.VStack(details.ToArray()).WithFlexibleWidth()
                    .WithAccessibilityIdentifier($"{id}.details");
                detailStack.Spacing = 4;
                rows.Add(detailStack);
            }
        }
        var card = Views.VStack(rows.ToArray()).WithPadding(SourceUiLayout.CardInset);
        card.Spacing = 8;
        return style.Card(card).WithAccessibilityIdentifier(id);
    }

    private static SwiftView Detail(string label, string value, SourceUiStyle style) =>
        Views.VStack(
                style.Text(Views.Text(label), SourceTextRole.SmallStrong, SourceTextTone.Secondary),
                style.Text(Views.Text(value), SourceTextRole.Small, SourceTextTone.Secondary))
            .WithFlexibleWidth();

    /// <summary>Source GetScoreColor: >= 80 success, >= 50 warning, else danger.</summary>
    private static SwiftView Score(double value, string label, string id, SourceUiStyle style)
    {
        var appearance = style.Appearance;
        var color = value >= 80 ? appearance.Success
            : value >= 50 ? appearance.Warning : appearance.Danger;
        var percent = $"{(int)value}%";
        return Views.VStack(
                style.Text(Views.Text(percent), SourceTextRole.Body2Strong)
                    .WithForegroundColor(color.Light, color.Dark),
                style.Text(Views.Text(label), SourceTextRole.Small, SourceTextTone.Secondary))
            .WithAccessibilityLabel($"{label} {percent}")
            .WithAccessibilityIdentifier(id);
    }

    private SwiftView Blocks(NativeWritingState state, SourceUiStyle style, Func<string, string> text)
    {
        var buttons = (state.Blocks.IsDefault ? [] : state.Blocks)
            .Select(block => style.OutlineButton(Views.Text(block.Text),
                    () => Fire(token => _presenter.UseBlockAsync(block.Index, token)))
                .WithAccessibilityLabel(block.Text)
                .WithAccessibilityIdentifier(block.AccessibilityId))
            .ToList();
        buttons.Add(style.LabelButton(Views.Image("shuffle"), () => Fire(_presenter.ShuffleAsync),
                SourceButtonRole.FilledSecondary)
            .WithAccessibilityLabel(text(NativeWritingText.Shuffle))
            .WithAccessibilityIdentifier(NativeWritingAccess.Shuffle));
        var columns = style.ViewportWidth < 600 ? 3 : 5;
        var grid = Views.LazyVGrid(columns, buttons.ToArray());
        grid.Spacing = 8;
        var section = Views.VStack(
                style.Text(Views.Text(text(NativeWritingText.ChooseVocab)), SourceTextRole.Body2,
                    SourceTextTone.Secondary),
                grid)
            .WithFlexibleWidth()
            .WithAccessibilityIdentifier(NativeWritingAccess.ChooseVocab);
        section.Spacing = 8;
        return section;
    }

    private SwiftView InputBar(NativeWritingState state, SourceUiStyle style, Func<string, string> text)
    {
        var translate = style.LabelButton(Views.Image("character.bubble"),
                () => Fire(_presenter.TranslateAsync), SourceButtonRole.FilledSecondary, 12)
            .WithAccessibilityLabel(text(NativeWritingText.Translate))
            .WithAccessibilityIdentifier(NativeWritingAccess.Translate);
        // Source: send icon on narrow screens, "Grade" text from md up; spinner while grading.
        SwiftView gradeLabel = state.IsGrading
            ? Views.IndeterminateProgress(text(NativeWritingText.Grade))
            : style.ViewportWidth >= 768 ? Views.Text(text(NativeWritingText.Grade))
            : Views.Image("paperplane.fill");
        var grade = style.LabelButton(gradeLabel, () => Fire(_presenter.GradeAsync),
                SourceButtonRole.Primary, 14)
            .WithAccessibilityLabel(text(NativeWritingText.Grade))
            .WithAccessibilityIdentifier(NativeWritingAccess.Grade);
        _grade = grade;
        _translate = translate;
        UpdateAvailability(style, state.Draft);

        var field = style.TextInput(Views.TextField(text(NativeWritingText.WriteSentence), state.Draft,
                value =>
                {
                    _presenter.SetDraft(value);
                    UpdateAvailability(style, value);
                }))
            .WithFlexibleWidth()
            .WithAccessibilityLabel(text(NativeWritingText.WriteSentence))
            .WithAccessibilityIdentifier(NativeWritingAccess.Input)
            .OnSubmit(() => Fire(_presenter.GradeAsync));
        var inputRow = Views.HStack(field, translate, grade).WithFlexibleWidth();
        inputRow.Spacing = 8;

        var count = style.Text(Views.Text(string.Format(CultureInfo.CurrentCulture,
                    text(NativeWritingText.SentencesWritten),
                    state.Sentences.IsDefault ? 0 : state.Sentences.Length)),
                SourceTextRole.Body2, SourceTextTone.Secondary)
            .WithAccessibilityIdentifier(NativeWritingAccess.Count);
        var footer = new List<SwiftView> { count, Views.VStack().WithFlexibleWidth() };
        if (state.CanEndSession)
            footer.Add(style.SecondaryButton(text(NativeWritingText.EndSession),
                    () => Fire(_presenter.EndSessionAsync))
                .WithAccessibilityIdentifier(NativeWritingAccess.EndSession));
        var footerRow = Views.HStack(footer.ToArray()).WithFlexibleWidth();
        var bar = Views.VStack(inputRow, footerRow);
        bar.Spacing = 8;
        return style.DockedBar(bar);
    }

    private void UpdateAvailability(SourceUiStyle style, string? draft)
    {
        var hasText = !string.IsNullOrWhiteSpace(draft);
        if (_grade is not null)
            SourceUiStyle.SetCustomButtonAvailability(_grade, style.Appearance,
                SourceButtonRole.Primary, hasText && !_state.IsGrading);
        if (_translate is not null)
            SourceUiStyle.SetCustomButtonAvailability(_translate, style.Appearance,
                SourceButtonRole.FilledSecondary, hasText && !_state.IsTranslating);
    }

    private SwiftView Summary(NativeWritingState state, SourceUiStyle style, Func<string, string> text)
    {
        var appearance = style.Appearance;
        SwiftView Stat(string value, string key, SourceColorPair color, string id) =>
            Views.VStack(
                    style.Text(Views.Text(value), SourceTextRole.Title2)
                        .WithForegroundColor(color.Light, color.Dark),
                    style.Text(Views.Text(text(key)), SourceTextRole.Small, SourceTextTone.Secondary))
                .WithAccessibilityLabel($"{text(key)} {value}")
                .WithAccessibilityIdentifier(id);
        var stats = Views.HStack(
            Stat(state.GradedCount.ToString(CultureInfo.CurrentCulture), NativeWritingText.Graded,
                appearance.Success, NativeWritingAccess.SummaryGraded),
            Stat($"{state.AverageAccuracy}%", NativeWritingText.AvgAccuracy,
                appearance.Primary, NativeWritingAccess.SummaryAccuracy),
            Stat($"{state.AverageFluency}%", NativeWritingText.AvgFluency,
                appearance.InfoAlertText, NativeWritingAccess.SummaryFluency));
        stats.Spacing = 32;
        // Source summary is a centered column.
        static SwiftView Centered(SwiftView view) =>
            view.WithFlexibleWidth(alignment: SwiftHorizontalAlignment.Center);
        var continueButton = style.PrimaryButton(text(NativeWritingText.ContinuePractice),
                () => Fire(_presenter.ContinueAsync))
            .WithAccessibilityIdentifier(NativeWritingAccess.Continue);
        // An End session during grading shows the summary first; Continue waits for that grade.
        if (!state.CanContinue)
            SourceUiStyle.SetCustomButtonAvailability(continueButton, appearance,
                SourceButtonRole.Primary, enabled: false);
        var summary = Views.VStack(
                Centered(Views.HStack(
                    Views.Image("text.book.closed"),
                    style.Text(Views.Text(text(NativeWritingText.SessionComplete)),
                        SourceTextRole.Title1, sourceHeading: true))),
                style.Card(stats.WithPadding(SourceUiLayout.CardInset), maxWidth: 340),
                Centered(Views.HStack(continueButton)),
                Centered(Views.HStack(style.SecondaryButton(text(NativeWritingText.Done), _host.Exit)
                    .WithAccessibilityIdentifier(NativeWritingAccess.Done))))
            .WithFlexibleWidth(alignment: SwiftHorizontalAlignment.Center)
            .WithAccessibilityIdentifier(NativeWritingAccess.Summary);
        summary.Spacing = SourceUiLayout.PageGap;
        return summary;
    }

    public void Dispose()
    {
        _presenter.StateChanged -= OnStateChanged;
        _presenter.Dispose();
    }
}
