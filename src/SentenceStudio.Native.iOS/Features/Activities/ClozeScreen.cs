using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.SwiftUI;
using SentenceStudio.Native.iOS.Styling;
using Views = Microsoft.SwiftUI.SwiftUI;

namespace SentenceStudio.Native.iOS.Features.Activities;

/// <summary>
/// SwiftUI Cloze (source Cloze.razor): the sentence with its blank and the
/// native translation, inline feedback, then the input bar with the five
/// choices (Choices mode), the typed answer field (Type mode), the
/// Type/Choices toggle, Go, and the previous / n of total / next row.
/// </summary>
internal sealed class ClozeScreen : IActivityScreen
{
    private readonly ActivityScreenHost _host;
    private readonly NativeClozePresenter _presenter;
    private NativeClozeState _state = new(NativePhase.Loading);

    public ClozeScreen(ActivityScreenHost host)
    {
        _host = host;
        _presenter = host.Services.GetRequiredService<NativeClozePresenter>();
        _presenter.StateChanged += OnStateChanged;
    }

    public string TitleKey => NativeClozeText.Title;

    public NativeMessage? Toast => NativeMessages.LatestToast(_state.Notice);

    public bool HasExited => _state.Destination == NativeDestination.TodaysPlan;

    public async Task OpenAsync(NativeActivityLaunch launch, CancellationToken cancellationToken) =>
        _state = await _presenter.OpenAsync(launch, cancellationToken);

    public async Task ExitAsync(CancellationToken cancellationToken) =>
        _state = await _presenter.ExitAsync(cancellationToken);

    private void OnStateChanged(NativeClozeState state)
    {
        _state = state;
        _host.Publish();
    }

    /// <summary>Answers and navigation bypass the controller's busy gate: the presenter serializes them and the 1.2 s advance must not disable Back.</summary>
    private void Fire(Func<CancellationToken, Task<NativeClozeState>> action) =>
        _ = FireAsync(action);

    private async Task FireAsync(Func<CancellationToken, Task<NativeClozeState>> action)
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
            _host.Logger?.LogError(ex, "Native iOS Cloze action failed.");
            _state = _state with { ErrorKey = NativeText.UnexpectedError };
        }
        _host.Publish();
    }

    public SwiftView Render(SourceUiStyle style)
    {
        var text = _host.Text;
        var state = _state;
        var items = new List<SwiftView>();
        switch (state.Phase)
        {
            case NativePhase.Idle:
            case NativePhase.Loading:
                items.Add(Views.IndeterminateProgress(text(NativeClozeText.Loading))
                    .WithAccessibilityIdentifier(NativeClozeAccess.Loading));
                items.Add(style.Text(Views.Text(text(NativeClozeText.Loading)),
                    SourceTextRole.Body1, SourceTextTone.Secondary));
                break;
            case NativePhase.Summary:
                items.Add(Summary(state, style, text));
                break;
            case NativePhase.Ready:
                items.AddRange(Sentence(state, style, text));
                break;
            default:
                // Empty, Error and Blocked offer only Go Back (source empty state).
                var empty = state.Phase == NativePhase.Empty;
                items.Add(style.Text(Views.Text(text(state.ErrorKey ?? NativeClozeText.NoSentences)),
                        SourceTextRole.Body1, empty ? SourceTextTone.Secondary : SourceTextTone.Danger)
                    .WithAccessibilityIdentifier(empty ? NativeClozeAccess.Empty : NativeClozeAccess.Error));
                items.Add(style.SecondaryButton(text(NativeClozeText.GoBack), _host.Exit)
                    .WithAccessibilityIdentifier(NativeClozeAccess.EmptyBack));
                break;
        }

        var gutter = SourceUiLayout.PageGutter(style.ViewportWidth);
        var content = Views.VStack(items.ToArray())
            .WithPadding(16, gutter, 16, gutter)
            .WithFlexibleWidth()
            .WithAccessibilityIdentifier("native.cloze.content");
        content.Spacing = SourceUiLayout.PageGap;
        // Source: the activity-input-bar sits outside activity-content, docked at the bottom.
        var page = Views.Scroll(content).WithAccessibilityIdentifier(NativeClozeAccess.Screen);
        return style.Page(state.Phase == NativePhase.Ready
            ? Views.BottomInset(page, InputBar(state, style, text))
            : page);
    }

    private static IEnumerable<SwiftView> Sentence(NativeClozeState state, SourceUiStyle style,
        Func<string, string> text)
    {
        // VoiceOver reads the blank as a word, never as underscores or the answer.
        var spoken = state.DisplaySentence.Replace(NativeClozeChallenge.BlankMarker,
            text(NativeClozeText.Blank), StringComparison.Ordinal);
        yield return style.Text(Views.Text(state.DisplaySentence), SourceTextRole.Display)
            .WithFlexibleWidth()
            .WithAccessibilityLabel(spoken)
            .WithAccessibilityIdentifier(NativeClozeAccess.Sentence);
        if (state.Translation.Length > 0)
            yield return style.Text(Views.Text(state.Translation), SourceTextRole.Body2,
                    SourceTextTone.Secondary)
                .WithFlexibleWidth()
                .WithAccessibilityIdentifier(NativeClozeAccess.Translation);
        if (state.IsGraded)
        {
            var message = state.LastWasCorrect == true
                ? text(NativeClozeText.Correct)
                : string.Format(CultureInfo.CurrentCulture, text(NativeClozeText.AnswerWas),
                    state.CorrectAnswer);
            yield return style.Alert(style.Text(Views.Text(message), SourceTextRole.DefaultBody),
                    success: state.LastWasCorrect == true, compact: true)
                .WithAccessibilityIdentifier(NativeClozeAccess.Feedback);
        }
        // The probe outcome is a success/info toast (scene overlay), never inline.
        if (style.MessageBanners(state.Notice, text, NativeClozeAccess.Notice) is { } banner)
            yield return banner;
        if (state.ErrorKey is { } error)
            yield return style.Text(Views.Text(text(error)), SourceTextRole.Body2,
                    SourceTextTone.Danger)
                .WithAccessibilityIdentifier(NativeClozeAccess.Error);
    }

    private SwiftView InputBar(NativeClozeState state, SourceUiStyle style, Func<string, string> text)
    {
        var rows = new List<SwiftView>();
        var choices = state.InputMode == NativeClozeInputMode.MultipleChoice;
        if (choices && !state.Options.IsDefaultOrEmpty)
        {
            var columns = style.ViewportWidth <= 768 ? 2 : 5;
            var grid = Views.LazyVGrid(columns, state.Options
                .Select((option, index) => Choice(state, style, option, index))
                .ToArray());
            grid.Spacing = 8;
            rows.Add(grid);
        }

        var actions = new List<SwiftView>();
        if (!choices)
        {
            var field = style.TextInput(Views.TextField(text(NativeClozeText.TypeMissingWord),
                    state.TypedAnswer, _presenter.SetTypedAnswer))
                .WithFlexibleWidth()
                .WithAccessibilityIdentifier(NativeClozeAccess.Answer)
                .OnSubmit(() => Fire(_presenter.SubmitAsync));
            field.IsEnabled = !state.IsGraded;
            actions.Add(field);
        }
        actions.Add(style.SecondaryButton(text(choices ? NativeClozeText.Type : NativeClozeText.Choices),
                () => Fire(_presenter.ToggleModeAsync))
            .WithAccessibilityIdentifier(NativeClozeAccess.ModeToggle));
        var go = style.PrimaryButton(text(NativeClozeText.Go), () => Fire(_presenter.SubmitAsync))
            .WithAccessibilityIdentifier(NativeClozeAccess.Submit);
        SourceUiStyle.SetCustomButtonAvailability(go, style.Appearance, SourceButtonRole.Primary,
            !state.IsGraded);
        actions.Add(go);
        var actionRow = Views.HStack(actions.ToArray()).WithFlexibleWidth();
        actionRow.Spacing = 8;
        rows.Add(actionRow);

        var previous = Views.Button(Views.Image("chevron.left"), () => Fire(_presenter.PreviousAsync))
            .WithButtonStyle(SwiftButtonStyle.Bordered)
            .WithMinimumHeight(SourceUiLayout.MinimumTouchHeight)
            .WithAccessibilityLabel(text(NativeClozeText.Previous))
            .WithAccessibilityIdentifier(NativeClozeAccess.Previous);
        previous.IsEnabled = state.CanGoPrevious;
        var next = Views.Button(Views.Image("chevron.right"), () => Fire(_presenter.NextAsync))
            .WithButtonStyle(SwiftButtonStyle.Bordered)
            .WithMinimumHeight(SourceUiLayout.MinimumTouchHeight)
            .WithAccessibilityLabel(text(NativeClozeText.Next))
            .WithAccessibilityIdentifier(NativeClozeAccess.Next);
        var position = style.Text(Views.Text(string.Format(CultureInfo.CurrentCulture,
                    "{0} / {1}", state.Index + 1, state.Total)),
                SourceTextRole.Body2, SourceTextTone.Secondary)
            .WithFlexibleWidth(alignment: SwiftHorizontalAlignment.Center)
            .WithAccessibilityIdentifier(NativeClozeAccess.Position);
        rows.Add(Views.HStack(previous, position, next).WithFlexibleWidth());

        var bar = Views.VStack(rows.ToArray());
        bar.Spacing = 8;
        return style.DockedBar(bar).WithAccessibilityIdentifier(NativeClozeAccess.InputBar);
    }

    private SwiftView Choice(NativeClozeState state, SourceUiStyle style, string option, int index)
    {
        var appearance = style.Appearance;
        var correct = state.IsGraded && option == state.CorrectAnswer;
        var wrong = state.IsGraded && !correct && option == state.SelectedOption;
        var fill = correct ? appearance.Success : wrong ? appearance.Danger : appearance.BodyBackground;
        var border = correct ? appearance.Success : wrong ? appearance.Danger
            : option == state.SelectedOption ? appearance.Primary : appearance.BootstrapButtonBorder;
        var label = style.Text(Views.Text(option), SourceTextRole.Body1)
            .WithPadding(8, 12, 8, 12)
            .WithFlexibleWidth(alignment: SwiftHorizontalAlignment.Center)
            .WithMinimumHeight(SourceUiLayout.MinimumTouchHeight)
            .WithRoundedSurface(fill.Light, fill.Dark, appearance.BootstrapRadius,
                border.Light, border.Dark, 1);
        if (correct || wrong)
            label.WithForegroundColor("#FFFFFF", "#FFFFFF");
        var button = Views.Button(label, () => Fire(token => _presenter.ChooseAsync(option, token)))
            .WithButtonStyle(SwiftButtonStyle.Plain)
            .WithAccessibilityLabel(option)
            .WithAccessibilitySelected(option == state.SelectedOption)
            .WithAccessibilityIdentifier($"{NativeClozeAccess.Choice}.{index}");
        button.IsEnabled = !state.IsGraded;
        return button;
    }

    private SwiftView Summary(NativeClozeState state, SourceUiStyle style, Func<string, string> text)
    {
        var stats = Views.HStack(
            Stat(state.SessionCorrect.ToString(CultureInfo.CurrentCulture),
                NativeClozeText.CorrectCount, SourceTextTone.Success,
                NativeClozeAccess.SummaryCorrect, style, text),
            Stat(state.SessionIncorrect.ToString(CultureInfo.CurrentCulture),
                NativeClozeText.IncorrectCount, SourceTextTone.Danger,
                NativeClozeAccess.SummaryIncorrect, style, text),
            Stat(string.Format(CultureInfo.CurrentCulture, "{0}%", state.AccuracyPercent),
                NativeClozeText.Accuracy, SourceTextTone.Primary,
                NativeClozeAccess.SummaryAccuracy, style, text));
        stats.Spacing = 32;
        var summary = Views.VStack(
                Views.HStack(
                    Views.Image("checklist"),
                    style.Text(Views.Text(text(NativeClozeText.SessionComplete)),
                        SourceTextRole.Title1, sourceHeading: true))
                    .WithFlexibleWidth(alignment: SwiftHorizontalAlignment.Center),
                style.Card(stats.WithPadding(SourceUiLayout.CardInset), maxWidth: 340),
                style.PrimaryButton(text(NativeClozeText.ContinuePractice),
                        () => Fire(_presenter.ContinuePracticeAsync))
                    .WithFlexibleWidth(alignment: SwiftHorizontalAlignment.Center)
                    .WithAccessibilityIdentifier(NativeClozeAccess.ContinuePractice),
                style.SecondaryButton(text(NativeClozeText.Done), _host.Exit)
                    .WithFlexibleWidth(alignment: SwiftHorizontalAlignment.Center)
                    .WithAccessibilityIdentifier(NativeClozeAccess.Done))
            .WithFlexibleWidth(alignment: SwiftHorizontalAlignment.Center)
            .WithAccessibilityIdentifier(NativeClozeAccess.Summary);
        summary.Spacing = SourceUiLayout.PageGap;
        return summary;
    }

    private static SwiftView Stat(string value, string labelKey, SourceTextTone tone,
        string id, SourceUiStyle style, Func<string, string> text) =>
        Views.VStack(
                style.Text(Views.Text(value), SourceTextRole.Title2, tone),
                style.Text(Views.Text(text(labelKey)), SourceTextRole.Small, SourceTextTone.Secondary))
            .WithAccessibilityLabel($"{text(labelKey)} {value}")
            .WithAccessibilityIdentifier(id);

    public void Dispose()
    {
        _presenter.StateChanged -= OnStateChanged;
        _presenter.Dispose();
    }
}
