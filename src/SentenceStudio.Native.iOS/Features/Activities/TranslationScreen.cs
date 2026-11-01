using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.SwiftUI;
using SentenceStudio.Native.iOS.Styling;
using Views = Microsoft.SwiftUI.SwiftUI;

namespace SentenceStudio.Native.iOS.Features.Activities;

/// <summary>
/// SwiftUI Translation (source Translation.razor). The feedback card scrolls;
/// the prompt, vocabulary blocks, answer row (field, Blocks/Type toggle,
/// Grade) and the sentence navigation row stay pinned at the bottom, as the
/// source activity-input-bar keeps them visible above the keyboard.
/// </summary>
internal sealed class TranslationScreen : IActivityScreen
{
    private readonly ActivityScreenHost _host;
    private readonly NativeTranslationPresenter _presenter;

    public TranslationScreen(ActivityScreenHost host)
    {
        _host = host;
        _presenter = host.Services.GetRequiredService<NativeTranslationPresenter>();
        _presenter.StateChanged += OnStateChanged;
    }

    public string TitleKey => NativeTranslationText.Title;

    public bool HasExited => _presenter.State.Destination == NativeDestination.TodaysPlan;

    public Task OpenAsync(NativeActivityLaunch launch, CancellationToken cancellationToken) =>
        _presenter.OpenAsync(launch, cancellationToken);

    public Task ExitAsync(CancellationToken cancellationToken) =>
        _presenter.ExitAsync(cancellationToken);

    private void OnStateChanged(NativeTranslationState state) => _host.Publish();

    /// <summary>
    /// Commands do not take the controller's busy gate: grading can take
    /// seconds and must not disable Back; the presenter serializes them.
    /// </summary>
    private void Fire(Func<CancellationToken, Task<NativeTranslationState>> action) =>
        _ = FireAsync(action);

    private async Task FireAsync(Func<CancellationToken, Task<NativeTranslationState>> action)
    {
        try
        {
            await action(_host.Lifetime);
        }
        catch (OperationCanceledException) when (_host.Lifetime.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            _host.Logger?.LogError(ex, "Native iOS Translation action failed.");
        }
        _host.Publish();
    }

    public SwiftView Render(SourceUiStyle style)
    {
        var text = _host.Text;
        var state = _presenter.State;
        var items = new List<SwiftView>();
        SwiftView? footer = null;
        if (state.Notice is { } notice && state.Phase != NativePhase.Blocked)
            items.Add(Notice(notice, style, text));
        switch (state.Phase)
        {
            case NativePhase.Idle:
            case NativePhase.Loading:
                items.Add(Views.IndeterminateProgress(text(NativeTranslationText.Loading))
                    .WithAccessibilityIdentifier(NativeTranslationAccess.Loading));
                items.Add(style.Text(Views.Text(text(NativeTranslationText.Loading)),
                    SourceTextRole.Body1, SourceTextTone.Secondary));
                break;
            case NativePhase.Summary:
                items.Add(Summary(state, style, text));
                break;
            case NativePhase.Ready when state.ShowIntro:
                items.Add(Intro(state, style, text));
                break;
            case NativePhase.Ready:
                if (state.Feedback is { } feedback)
                    items.Add(Feedback(state, feedback, style, text));
                footer = InputBar(state, style, text);
                break;
            default:
                // Empty, Blocked and Error offer only Go Back (source empty state).
                var empty = state.Phase == NativePhase.Empty;
                items.Add(style.Text(Views.Text(text(state.ErrorKey ?? NativeTranslationText.NoSentences)),
                        SourceTextRole.Body1, empty ? SourceTextTone.Secondary : SourceTextTone.Danger)
                    .WithAccessibilityIdentifier(empty
                        ? NativeTranslationAccess.Empty : NativeTranslationAccess.Error));
                items.Add(style.SecondaryButton(text(NativeTranslationText.GoBack), _host.Exit)
                    .WithAccessibilityIdentifier(NativeTranslationAccess.EmptyBack));
                break;
        }

        var gutter = SourceUiLayout.PageGutter(style.ViewportWidth);
        var content = Views.VStack(items.ToArray())
            .WithPadding(16, gutter, 16, gutter)
            .WithFlexibleWidth()
            .WithAccessibilityIdentifier("native.translation.content");
        content.Spacing = SourceUiLayout.PageGap;
        var page = Views.Scroll(content)
            .WithAccessibilityIdentifier(NativeTranslationAccess.Screen);
        return style.Page(footer is null ? page : Views.BottomInset(page, footer));
    }

    private SwiftView Notice(NativeTranslationNotice notice, SourceUiStyle style,
        Func<string, string> text)
    {
        var message = notice.Argument is null ? text(notice.Key)
            : string.Format(CultureInfo.CurrentCulture, text(notice.Key), notice.Argument);
        var label = style.Text(Views.Text(message), SourceTextRole.Body2);
        return (notice.IsWarning ? style.InfoAlert(label, compact: true)
                : style.Alert(label, success: !notice.IsError, compact: true))
            .WithAccessibilityIdentifier(NativeTranslationAccess.Notice);
    }

    private SwiftView Intro(NativeTranslationState state, SourceUiStyle style,
        Func<string, string> text)
    {
        var rows = new List<SwiftView>();
        if (!string.IsNullOrWhiteSpace(state.IntroResourceTitle))
            rows.Add(style.Text(Views.Text(
                    $"{text(NativeTranslationText.IntroResource)}: {state.IntroResourceTitle}"),
                    SourceTextRole.Caption1, SourceTextTone.Secondary)
                .WithAccessibilityIdentifier(NativeTranslationAccess.IntroResource));
        if (!state.IntroVocabulary.IsDefaultOrEmpty)
        {
            rows.Add(Heading("list.bullet.rectangle", NativeTranslationText.IntroVocabulary, style, text));
            var words = Views.VStack(state.IntroVocabulary.Select(word =>
            {
                var line = string.IsNullOrEmpty(word.Native)
                    ? word.Target : $"{word.Target} — {word.Native}";
                return Bullet(style.Text(Views.Text(line), SourceTextRole.Body2,
                    strongElement: true), style);
            }).ToArray()).WithFlexibleWidth()
                .WithAccessibilityIdentifier(NativeTranslationAccess.IntroVocabulary);
            words.Spacing = 4;
            rows.Add(words);
        }
        if (!state.IntroGrammar.IsDefaultOrEmpty)
        {
            rows.Add(Heading("graduationcap", NativeTranslationText.IntroGrammar, style, text));
            var bullets = Views.VStack(state.IntroGrammar.Select(line =>
                    Bullet(style.Text(Views.Text(line), SourceTextRole.Body2), style)).ToArray())
                .WithFlexibleWidth()
                .WithAccessibilityIdentifier(NativeTranslationAccess.IntroGrammar);
            bullets.Spacing = 4;
            rows.Add(bullets);
        }
        var card = Views.VStack(rows.ToArray()).WithPadding(SourceUiLayout.CardInset);
        card.Spacing = SourceUiLayout.ItemGap;
        var intro = Views.VStack(
                Views.HStack(
                    Views.Image("bookmark.fill").WithForegroundColor(
                        style.Appearance.Primary.Light, style.Appearance.Primary.Dark),
                    style.Text(Views.Text(text(NativeTranslationText.IntroTitle)),
                        SourceTextRole.Title1, sourceHeading: true)),
                style.Card(card, maxWidth: 560),
                Centered(style.PrimaryButton(text(NativeTranslationText.Start),
                        () => Fire(_presenter.StartAsync))
                    .WithAccessibilityIdentifier(NativeTranslationAccess.Start)))
            .WithFlexibleWidth(alignment: SwiftHorizontalAlignment.Center)
            .WithAccessibilityIdentifier(NativeTranslationAccess.Intro);
        intro.Spacing = 24;
        return intro;
    }

    /// <summary>Source pages center their action column (align-items-center).</summary>
    private static SwiftView Centered(SwiftView view) =>
        Views.HStack(view).WithFlexibleWidth(alignment: SwiftHorizontalAlignment.Center);

    private static SwiftView Heading(string symbol, string key, SourceUiStyle style,
        Func<string, string> text) =>
        Views.HStack(Views.Image(symbol),
            style.Text(Views.Text(text(key)), SourceTextRole.Title3, sourceHeading: true));

    private static SwiftView Bullet(SwiftView label, SourceUiStyle style)
    {
        var row = Views.HStack(
            style.Text(Views.Text("•"), SourceTextRole.Body2, SourceTextTone.Secondary),
            label).WithFlexibleWidth();
        row.Spacing = 6;
        return row;
    }

    private SwiftView Feedback(NativeTranslationState state, NativeTranslationFeedback feedback,
        SourceUiStyle style, Func<string, string> text)
    {
        var rows = new List<SwiftView>();
        if (!string.IsNullOrWhiteSpace(feedback.SubmittedInput))
            rows.Add(Views.HStack(
                    Views.Image("quote.bubble").WithForegroundColor(
                        style.Appearance.SecondaryText.Light, style.Appearance.SecondaryText.Dark),
                    style.Text(Views.Text(feedback.SubmittedInput), SourceTextRole.Body1))
                .WithFlexibleWidth()
                .WithAccessibilityIdentifier(NativeTranslationAccess.FeedbackInput));
        var scores = new List<SwiftView>();
        if (feedback.Accuracy > 0)
            scores.Add(Score(feedback.Accuracy, NativeTranslationText.Accuracy,
                NativeTranslationAccess.Accuracy, style, text));
        if (feedback.Fluency > 0)
            scores.Add(Score(feedback.Fluency, NativeTranslationText.Fluency,
                NativeTranslationAccess.Fluency, style, text));
        if (scores.Count > 0)
        {
            var scoreRow = Views.HStack(scores.ToArray())
                .WithFlexibleWidth(alignment: SwiftHorizontalAlignment.Trailing);
            scoreRow.Spacing = 16;
            rows.Add(scoreRow);
        }
        if (!string.IsNullOrEmpty(feedback.RecommendedTranslation))
            rows.Add(style.Text(Views.Text(
                    $"{text(NativeTranslationText.Suggested)} {feedback.RecommendedTranslation}"),
                    SourceTextRole.Small, SourceTextTone.Secondary)
                .WithFlexibleWidth()
                .WithAccessibilityIdentifier(NativeTranslationAccess.Suggested));
        if (feedback.HasDetails)
        {
            var toggle = Views.Button(Views.HStack(
                        Views.Image(state.DetailsExpanded ? "chevron.down" : "chevron.right"),
                        style.Text(Views.Text(text(NativeTranslationText.ShowDetails)),
                            SourceTextRole.Caption1, SourceTextTone.Secondary)),
                    () => Fire(_presenter.ToggleDetailsAsync))
                .WithButtonStyle(SwiftButtonStyle.Plain)
                .WithAccessibilityLabel(text(NativeTranslationText.ShowDetails))
                .WithAccessibilitySelected(state.DetailsExpanded)
                .WithAccessibilityIdentifier(NativeTranslationAccess.DetailsToggle);
            rows.Add(toggle);
            if (state.DetailsExpanded)
            {
                var details = new List<SwiftView>();
                void Detail(string label, string value)
                {
                    if (!string.IsNullOrEmpty(value))
                        details.Add(style.Text(Views.Text($"{label} {value}"),
                            SourceTextRole.Small, SourceTextTone.Secondary).WithFlexibleWidth());
                }
                Detail($"{text(NativeTranslationText.Accuracy)}:", feedback.AccuracyExplanation);
                Detail($"{text(NativeTranslationText.Fluency)}:", feedback.FluencyExplanation);
                Detail(text(NativeTranslationText.Notes), feedback.GrammarNotes);
                var stack = Views.VStack(details.ToArray()).WithFlexibleWidth()
                    .WithAccessibilityIdentifier(NativeTranslationAccess.Details);
                stack.Spacing = 4;
                rows.Add(stack);
            }
        }
        var card = Views.VStack(rows.ToArray()).WithPadding(12);
        card.Spacing = SourceUiLayout.ItemGap;
        return style.Card(card).WithAccessibilityIdentifier(NativeTranslationAccess.Feedback);
    }

    private static SwiftView Score(double value, string key, string id,
        SourceUiStyle style, Func<string, string> text)
    {
        var color = NativeTranslationFeedback.Tone(value) switch
        {
            NativeScoreTone.Success => style.Appearance.Success,
            NativeScoreTone.Warning => style.Appearance.Warning,
            _ => style.Appearance.Danger
        };
        var percent = $"{(int)value}%";
        var score = Views.VStack(
                style.Text(Views.Text(percent), SourceTextRole.Body2Strong)
                    .WithForegroundColor(color.Light, color.Dark),
                style.Text(Views.Text(text(key)), SourceTextRole.Small, SourceTextTone.Secondary))
            .WithAccessibilityLabel($"{text(key)} {percent}")
            .WithAccessibilityIdentifier(id);
        score.Spacing = 2;
        return score;
    }

    private SwiftView InputBar(NativeTranslationState state, SourceUiStyle style,
        Func<string, string> text)
    {
        var rows = new List<SwiftView>
        {
            style.Text(Views.Text(state.Prompt), SourceTextRole.Title3)
                .WithFlexibleWidth()
                .WithAccessibilityIdentifier(NativeTranslationAccess.Prompt)
        };
        if (state.ShowsBlocks)
        {
            var blocks = state.Blocks.Select((block, index) =>
                    style.OutlineButton(style.Text(Views.Text(block), SourceTextRole.Small),
                            () => Fire(token => _presenter.UseBlockAsync(index, token)))
                        .WithAccessibilityLabel(block)
                        .WithAccessibilityIdentifier($"{NativeTranslationAccess.Block}.{index}"))
                .ToArray();
            rows.Add(Views.ButtonGroup(blocks, wraps: true)
                .WithAccessibilityIdentifier(NativeTranslationAccess.Blocks));
        }

        var grade = style.PrimaryButton(
                text(state.IsGrading ? NativeTranslationText.Loading : NativeTranslationText.Grade),
                () => Fire(_presenter.GradeAsync))
            .WithAccessibilityIdentifier(state.IsGrading
                ? NativeTranslationAccess.Grading : NativeTranslationAccess.Grade);
        SwiftView? gradeButton = grade;
        if (state.IsGrading)
            gradeButton = Views.HStack(Views.IndeterminateProgress(text(NativeTranslationText.Grade))
                .WithAccessibilityIdentifier(NativeTranslationAccess.Grading));
        else
            SourceUiStyle.SetCustomButtonAvailability(grade, style.Appearance,
                SourceButtonRole.Primary, state.CanGrade);

        var placeholder = string.Format(CultureInfo.CurrentCulture,
            text(NativeTranslationText.TranslateTo), state.TargetLanguage);
        var field = style.TextInput(Views.TextField(placeholder, state.Input, value =>
            {
                // Store only: re-rendering here would replace the field mid-composition.
                _presenter.SetInput(value);
                if (!state.IsGrading)
                    SourceUiStyle.SetCustomButtonAvailability(grade, style.Appearance,
                        SourceButtonRole.Primary, !string.IsNullOrWhiteSpace(value));
            }))
            .WithFlexibleWidth()
            .WithAccessibilityLabel(placeholder)
            .WithAccessibilityIdentifier(NativeTranslationAccess.Answer)
            .OnSubmit(() => Fire(_presenter.GradeAsync));
        var toggle = style.SecondaryButton(
                text(state.BlocksMode ? NativeTranslationText.Type : NativeTranslationText.Blocks),
                () => Fire(_presenter.ToggleInputModeAsync))
            .WithAccessibilityIdentifier(NativeTranslationAccess.ModeToggle);
        var answerRow = Views.HStack(field, toggle, gradeButton).WithFlexibleWidth();
        answerRow.Spacing = SourceUiLayout.ItemGap;
        rows.Add(answerRow);

        var previous = Views.Button(Views.Image("chevron.left"), () => Fire(_presenter.PreviousAsync))
            .WithAccessibilityLabel(text(NativeTranslationText.Previous))
            .WithAccessibilityIdentifier(NativeTranslationAccess.Previous);
        previous.IsEnabled = state.CanGoPrevious;
        var next = Views.Button(Views.Image("chevron.right"), () => Fire(_presenter.NextAsync))
            .WithAccessibilityLabel(text(NativeTranslationText.Next))
            .WithAccessibilityIdentifier(NativeTranslationAccess.Next);
        next.IsEnabled = !state.IsGrading;
        var position = style.Text(Views.Text(string.Format(CultureInfo.CurrentCulture,
                    "{0} / {1}", state.Index + 1, state.Count)),
                SourceTextRole.Body2, SourceTextTone.Secondary)
            .WithFlexibleWidth(alignment: SwiftHorizontalAlignment.Center)
            .WithAccessibilityIdentifier(NativeTranslationAccess.Position);
        rows.Add(Views.HStack(previous, position, next).WithFlexibleWidth());

        var gutter = SourceUiLayout.PageGutter(style.ViewportWidth);
        var bar = Views.VStack(rows.ToArray())
            .WithPadding(10, gutter, 10, gutter)
            .WithFlexibleWidth();
        bar.Spacing = SourceUiLayout.ItemGap;
        var surface = style.Appearance.CardSurface;
        return bar.WithBackgroundColor(surface.Light, surface.Dark)
            .WithAccessibilityIdentifier("native.translation.input-bar");
    }

    private SwiftView Summary(NativeTranslationState state, SourceUiStyle style,
        Func<string, string> text)
    {
        var stats = Views.HStack(
            Stat(state.GradedCount.ToString(CultureInfo.CurrentCulture), NativeTranslationText.Graded,
                style.Appearance.Success, NativeTranslationAccess.SummaryGraded, style, text),
            Stat($"{state.AverageAccuracy}%", NativeTranslationText.AvgAccuracy,
                style.Appearance.Primary, NativeTranslationAccess.SummaryAccuracy, style, text),
            Stat($"{state.AverageFluency}%", NativeTranslationText.AvgFluency,
                style.Appearance.InfoAlertText, NativeTranslationAccess.SummaryFluency, style, text));
        stats.Spacing = 24;
        var summary = Views.VStack(
                Views.HStack(
                    Views.Image("checkmark.rectangle.stack"),
                    style.Text(Views.Text(text(NativeTranslationText.SessionComplete)),
                        SourceTextRole.Title1, sourceHeading: true)),
                style.Card(stats.WithPadding(SourceUiLayout.LargeCardInset), maxWidth: 340),
                Centered(style.PrimaryButton(text(NativeTranslationText.ContinuePractice),
                        () => Fire(_presenter.ContinueAsync))
                    .WithAccessibilityIdentifier(NativeTranslationAccess.Continue)),
                Centered(style.SecondaryButton(text(NativeTranslationText.Done), _host.Exit)
                    .WithAccessibilityIdentifier(NativeTranslationAccess.Done)))
            .WithFlexibleWidth(alignment: SwiftHorizontalAlignment.Center)
            .WithAccessibilityIdentifier(NativeTranslationAccess.Summary);
        summary.Spacing = SourceUiLayout.PageGap;
        return summary;
    }

    private static SwiftView Stat(string value, string labelKey, SourceColorPair color,
        string id, SourceUiStyle style, Func<string, string> text)
    {
        var stat = Views.VStack(
                style.Text(Views.Text(value), SourceTextRole.Title2)
                    .WithForegroundColor(color.Light, color.Dark),
                style.Text(Views.Text(text(labelKey)), SourceTextRole.Small,
                    SourceTextTone.Secondary))
            .WithAccessibilityLabel($"{text(labelKey)} {value}")
            .WithAccessibilityIdentifier(id);
        stat.Spacing = 2;
        return stat;
    }

    public void Dispose()
    {
        _presenter.StateChanged -= OnStateChanged;
        _presenter.Dispose();
    }
}
