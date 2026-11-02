using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.SwiftUI;
using SentenceStudio.Native.iOS.Styling;
using Views = Microsoft.SwiftUI.SwiftUI;

namespace SentenceStudio.Native.iOS.Features.Activities;

/// <summary>
/// SwiftUI Number Drill (source NumberDrill.razor): setup card, item card
/// per sub-mode with the feedback panel, progress footer, and summary card.
/// </summary>
internal sealed class NumberDrillScreen : IActivityScreen
{
    private readonly ActivityScreenHost _host;
    private readonly NativeNumberDrillPresenter _presenter;
    private NativeNumberDrillState _state = new(NativePhase.Loading);
    private bool _hintsOpen;
    private int _focusedItem = -1;
    private string? _itemKey;

    public NumberDrillScreen(ActivityScreenHost host)
    {
        _host = host;
        _presenter = host.Services.GetRequiredService<NativeNumberDrillPresenter>();
        _presenter.StateChanged += OnStateChanged;
    }

    public string TitleKey => NativeNumberDrillText.Title;

    public bool HasExited => _state.Destination == NativeDestination.TodaysPlan;

    public async Task OpenAsync(NativeActivityLaunch launch, CancellationToken cancellationToken) =>
        _state = await _presenter.OpenAsync(launch, cancellationToken);

    public async Task ExitAsync(CancellationToken cancellationToken) =>
        _state = await _presenter.ExitAsync(cancellationToken);

    private void OnStateChanged(NativeNumberDrillState state)
    {
        _state = state;
        _host.Publish();
    }

    /// <summary>Drill actions do not take the controller's busy gate: the presenter serializes them and auto-advance pauses must not disable Back.</summary>
    private void Fire(Func<CancellationToken, Task<NativeNumberDrillState>> action) =>
        _ = FireAsync(action);

    private async Task FireAsync(Func<CancellationToken, Task<NativeNumberDrillState>> action)
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
            _host.Logger?.LogError(ex, "Native iOS Number Drill action failed.");
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
                items.Add(Views.IndeterminateProgress(text(NativeNumberDrillText.Title))
                    .WithAccessibilityIdentifier(NativeNumberDrillAccess.Loading));
                break;
            case NativePhase.Empty:
            case NativePhase.Blocked:
            case NativePhase.Error:
                items.Add(style.Text(Views.Text(text(state.ErrorKey ?? NativeNumberDrillText.Unavailable)),
                        SourceTextRole.Body1,
                        state.Phase == NativePhase.Empty ? SourceTextTone.Secondary : SourceTextTone.Danger)
                    .WithAccessibilityIdentifier(state.Phase == NativePhase.Empty
                        ? NativeNumberDrillAccess.Gate : NativeNumberDrillAccess.Error));
                items.Add(style.SecondaryButton(text(NativeNumberDrillText.GoBack), _host.Exit)
                    .WithAccessibilityIdentifier(NativeNumberDrillAccess.GateBack));
                break;
            default:
                if (state.Stage == NativeNumberDrillStage.Setup)
                    items.Add(Setup(state, style, text));
                else if (state.Stage == NativeNumberDrillStage.Summary && state.Summary is { } summary)
                    items.Add(Summary(summary, style, text));
                else if (state.Item is { } item)
                    items.AddRange(Session(state, item, style, text));
                break;
        }

        var gutter = SourceUiLayout.PageGutter(style.ViewportWidth);
        var content = Views.VStack(items.ToArray())
            .WithPadding(16, gutter, 16, gutter)
            .WithFlexibleWidth(640, SwiftHorizontalAlignment.Center)
            .WithAccessibilityIdentifier("native.numberdrill.content");
        content.Spacing = SourceUiLayout.PageGap;
        var scroll = Views.Scroll(Views.HStack(content).WithFlexibleWidth(
                alignment: SwiftHorizontalAlignment.Center))
            .WithAccessibilityIdentifier(NativeNumberDrillAccess.Screen);
        if (state.Stage == NativeNumberDrillStage.InSession && state.Item is not null
            && state.Phase is NativePhase.Ready or NativePhase.Feedback)
            return style.Page(Views.VStack(scroll, Footer(state, style, text)));
        return style.Page(scroll);
    }

    // ============ SETUP CARD ============
    private SwiftView Setup(NativeNumberDrillState state, SourceUiStyle style, Func<string, string> text)
    {
        var rows = new List<SwiftView>
        {
            style.Text(Views.Text(text(NativeNumberDrillText.ChooseDrill)),
                SourceTextRole.Title3, sourceHeading: true),
            Label(text(NativeNumberDrillText.Context), style),
        };
        var contexts = new List<SwiftView>();
        foreach (var context in state.Contexts.IsDefault ? [] : state.Contexts)
        {
            var code = context.Code;
            contexts.Add(Chip(style, ContextSymbol(code), context.DisplayName,
                state.SelectedContext == code, !state.IsBusy,
                () => Fire(token => _presenter.SelectContextAsync(code, token)),
                $"{NativeNumberDrillAccess.Context}.{code}"));
        }
        contexts.Add(Chip(style, "asterisk", text(NativeNumberDrillText.AnyContext),
            state.SelectedContext is null, !state.IsBusy,
            () => Fire(token => _presenter.SelectContextAsync(null, token)),
            NativeNumberDrillAccess.ContextAny));
        rows.Add(Views.ButtonGroup(contexts.ToArray(), wraps: true).WithFlexibleWidth());

        rows.Add(Label(text(NativeNumberDrillText.Mode), style));
        var modes = (state.SubModes.IsDefault ? [] : state.SubModes).Select(mode =>
        {
            var code = mode.Code;
            return Chip(style, null, mode.DisplayName, state.SelectedSubMode == code, !state.IsBusy,
                () => Fire(token => _presenter.SelectSubModeAsync(code, token)),
                $"{NativeNumberDrillAccess.Mode}.{code}");
        }).ToArray();
        if (modes.Length > 0)
            rows.Add(Views.ButtonGroup(modes, wraps: true).WithFlexibleWidth());

        var count = string.Format(CultureInfo.CurrentCulture, text(NativeNumberDrillText.Items), state.ItemCount);
        rows.Add(Label(count, style).WithAccessibilityIdentifier(NativeNumberDrillAccess.ItemCount));
        var decrease = style.OutlineButton(Views.Image("minus"),
                () => Fire(token => _presenter.ChangeItemCountAsync(-1, token)))
            .WithAccessibilityLabel(text(NativeNumberDrillText.FewerItems))
            .WithAccessibilityIdentifier(NativeNumberDrillAccess.ItemsDecrease);
        decrease.IsEnabled = !state.IsBusy && state.ItemCount > NativeNumberDrillPresenter.MinItems;
        var increase = style.OutlineButton(Views.Image("plus"),
                () => Fire(token => _presenter.ChangeItemCountAsync(+1, token)))
            .WithAccessibilityLabel(text(NativeNumberDrillText.MoreItems))
            .WithAccessibilityIdentifier(NativeNumberDrillAccess.ItemsIncrease);
        increase.IsEnabled = !state.IsBusy && state.ItemCount < NativeNumberDrillPresenter.MaxItems;
        var stepper = Views.HStack(decrease,
            style.Text(Views.Text(state.ItemCount.ToString(CultureInfo.CurrentCulture)),
                SourceTextRole.Body1),
            increase);
        stepper.Spacing = SourceUiLayout.ItemGap;
        rows.Add(stepper);

        var dueOnly = Views.Toggle(text(NativeNumberDrillText.DueOnly), state.DueOnly,
                value => Fire(token => _presenter.SetDueOnlyAsync(value, token)))
            .WithAccessibilityIdentifier(NativeNumberDrillAccess.DueOnly);
        dueOnly.IsEnabled = !state.IsBusy;
        rows.Add(style.Text(dueOnly, SourceTextRole.Body2));

        var start = style.PrimaryButton(text(NativeNumberDrillText.StartSession),
                () => Fire(_presenter.StartAsync), fullWidth: true)
            .WithAccessibilityIdentifier(NativeNumberDrillAccess.Start);
        SourceUiStyle.SetCustomButtonAvailability(start, style.Appearance,
            SourceButtonRole.Primary, !state.IsBusy && !string.IsNullOrEmpty(state.SelectedSubMode));
        rows.Add(start);

        if (state.ErrorKey is { } error)
            rows.Add(style.Alert(Views.HStack(Views.Image("exclamationmark.triangle"),
                        style.Text(Views.Text(text(error)), SourceTextRole.Body2)),
                    success: false, compact: true)
                .WithAccessibilityIdentifier(NativeNumberDrillAccess.Error));

        var card = Views.VStack(rows.ToArray()).WithPadding(SourceUiLayout.LargeCardInset);
        card.Spacing = SourceUiLayout.PageGap;
        return style.Card(card).WithAccessibilityIdentifier(NativeNumberDrillAccess.Setup);
    }

    // ============ ITEM CARD ============
    private IEnumerable<SwiftView> Session(NativeNumberDrillState state, NativeNumberDrillItem item,
        SourceUiStyle style, Func<string, string> text)
    {
        var key = $"{state.ItemIndex}:{item.DisplayPrompt}:{item.PromptA}";
        if (_itemKey != key)
        {
            _itemKey = key;
            _hintsOpen = false;
        }
        var header = Views.HStack(
            style.Text(Views.Text(item.System), SourceTextRole.SmallStrong)
                .WithForegroundColor("#FFFFFF", "#FFFFFF")
                .WithPadding(4, 12, 4, 12)
                .WithRoundedSurface(SystemColor(style, item.System).Light,
                    SystemColor(style, item.System).Dark, 999)
                .WithAccessibilityIdentifier(NativeNumberDrillAccess.System),
            Views.VStack().WithFlexibleWidth(),
            Views.HStack(Views.Image(ContextSymbol(item.ContextCode)),
                    style.Text(Views.Text(item.ContextCode), SourceTextRole.Small, SourceTextTone.Secondary))
                .WithAccessibilityIdentifier(NativeNumberDrillAccess.ItemContext));
        yield return header;

        if (state.ErrorKey is { } error)
            yield return style.Alert(style.Text(Views.Text(text(error)), SourceTextRole.Body2),
                    success: false, compact: true)
                .WithAccessibilityIdentifier(NativeNumberDrillAccess.Error);
        if (state.NoticeKey is { } notice)
            yield return style.MessageBanner(NativeNumberDrillMessages.Notice(notice), text,
                NativeNumberDrillAccess.Notice);

        if (state.Phase == NativePhase.Feedback && state.Feedback is { } feedback)
        {
            if (item.SubModeCode == NativeNumberSubMode.Disambiguate)
            {
                yield return Paired(state, item, true, style, text);
                yield return Paired(state, item, false, style, text);
            }
            foreach (var view in Feedback(state, feedback, style, text))
                yield return view;
            yield break;
        }

        switch (item.SubModeCode)
        {
            case NativeNumberSubMode.ListenAndType:
                yield return Center(PlayButton(state, style, text, NativeNumberDrillText.Play,
                    NativeNumberDrillAccess.Play));
                break;
            case NativeNumberSubMode.ReadAndProduce:
                yield return Center(style.Text(Views.Text(item.DisplayPrompt), SourceTextRole.Display,
                        SourceTextTone.Primary)
                    .WithAccessibilityIdentifier(NativeNumberDrillAccess.Prompt));
                break;
            case NativeNumberSubMode.TapTheCounter:
                if (!string.IsNullOrEmpty(item.NounCue))
                    yield return Center(Views.HStack(Views.Image("tag"),
                            style.Text(Views.Text(item.NounCue), SourceTextRole.Body2, SourceTextTone.Secondary))
                        .WithAccessibilityIdentifier(NativeNumberDrillAccess.NounCue));
                yield return Center(style.Text(Views.Text(item.DisplayPrompt), SourceTextRole.Display)
                    .WithAccessibilityIdentifier(NativeNumberDrillAccess.Prompt));
                if (!item.Choices.IsDefaultOrEmpty)
                    yield return ChoiceGroup(state, item, style);
                break;
            case NativeNumberSubMode.ListenAndPlace:
                yield return Center(PlayButton(state, style, text, NativeNumberDrillText.PlayAudio,
                    NativeNumberDrillAccess.Play));
                yield return Center(style.Text(Views.Text(text(NativeNumberDrillText.ListenAndPlaceInstructions)),
                    SourceTextRole.Body1, SourceTextTone.Secondary));
                if (!item.Choices.IsDefaultOrEmpty)
                    yield return ChoiceGroup(state, item, style);
                var replayLabel = Views.HStack(Views.Image("arrow.clockwise"),
                    Views.Text(text(NativeNumberDrillText.Replay)));
                replayLabel.Spacing = 4;
                var replay = style.OutlineButton(replayLabel, () => Fire(_presenter.PlayAudioAsync))
                    .WithAccessibilityIdentifier(NativeNumberDrillAccess.Replay);
                replay.IsEnabled = !state.IsBusy && !state.IsPlayingAudio;
                yield return Center(replay);
                break;
            case NativeNumberSubMode.Disambiguate:
                yield return Paired(state, item, true, style, text);
                yield return Paired(state, item, false, style, text);
                var submitBoth = style.PrimaryButton(text(NativeNumberDrillText.SubmitBoth),
                        () => Fire(_presenter.SubmitBothAsync), fullWidth: true)
                    .WithAccessibilityIdentifier(NativeNumberDrillAccess.SubmitBoth);
                SourceUiStyle.SetCustomButtonAvailability(submitBoth, style.Appearance,
                    SourceButtonRole.Primary, state.CanSubmitBoth);
                yield return submitBoth;
                break;
        }

        if (!NativeNumberSubMode.IsTextEntry(item.SubModeCode))
            yield break;

        if (!item.Hints.IsDefaultOrEmpty)
        {
            var hintLabel = Views.HStack(Views.Image(_hintsOpen ? "chevron.down" : "chevron.right"),
                Views.Image("lightbulb"),
                style.Text(Views.Text(text(NativeNumberDrillText.ShowHint)), SourceTextRole.Body2,
                    SourceTextTone.Secondary));
            hintLabel.Spacing = 4;
            yield return Views.Button(hintLabel, () =>
                {
                    _hintsOpen = !_hintsOpen;
                    _host.Publish();
                })
                .WithButtonStyle(SwiftButtonStyle.Plain)
                .WithMinimumHeight(SourceUiLayout.MinimumTouchHeight)
                .WithAccessibilitySelected(_hintsOpen)
                .WithAccessibilityIdentifier(NativeNumberDrillAccess.HintToggle);
            if (_hintsOpen)
            {
                var hints = Views.VStack(item.Hints.Select(hint =>
                        style.Text(Views.Text($"• {hint}"), SourceTextRole.Body2, SourceTextTone.Secondary))
                    .ToArray()).WithAccessibilityIdentifier(NativeNumberDrillAccess.Hints);
                hints.Spacing = 4;
                yield return hints.WithPadding(0, 16, 0, 0);
            }
        }

        var submit = style.PrimaryButton(text(NativeNumberDrillText.Submit),
                () => Fire(token => _presenter.SubmitAnswerAsync(null, token)), fullWidth: true)
            .WithAccessibilityIdentifier(NativeNumberDrillAccess.Submit);
        SourceUiStyle.SetCustomButtonAvailability(submit, style.Appearance,
            SourceButtonRole.Primary, state.CanSubmitText);
        var placeholder = string.Format(CultureInfo.CurrentCulture,
            text(NativeNumberDrillText.ExamplePattern), item.SpacingHint);
        var field = style.TextInput(Views.TextField(placeholder, state.Answer, value =>
            {
                // No publish: rebuilding the field would interrupt Korean IME composition.
                var updated = _presenter.UpdateAnswer(value);
                SourceUiStyle.SetCustomButtonAvailability(submit, style.Appearance,
                    SourceButtonRole.Primary, updated.CanSubmitText);
            }))
            .WithFlexibleWidth()
            .WithAccessibilityLabel(placeholder)
            .WithAccessibilityIdentifier(NativeNumberDrillAccess.Answer)
            .OnSubmit(() => Fire(token => _presenter.SubmitAnswerAsync(null, token)));
        field.IsEnabled = !state.IsBusy;
        if (_focusedItem != state.ItemIndex && field.IsEnabled)
        {
            _focusedItem = state.ItemIndex;
            field.FocusRequested = true;
        }
        yield return field;
        yield return submit;
    }

    private SwiftView PlayButton(NativeNumberDrillState state, SourceUiStyle style,
        Func<string, string> text, string idleKey, string id)
    {
        var playing = state.IsPlayingAudio;
        var label = Views.HStack(Views.Image(playing ? "speaker.slash" : "speaker.wave.2"),
            Views.Text(text(playing ? NativeNumberDrillText.Playing : idleKey)));
        label.Spacing = 8;
        var button = style.OutlineButton(label, () => Fire(_presenter.PlayAudioAsync))
            .WithAccessibilityIdentifier(id);
        // Source btn-lg btn-ss-primary.
        var primary = style.Appearance.Primary;
        var onPrimary = style.Appearance.OnPrimary;
        label.WithForegroundColor(onPrimary.Light, onPrimary.Dark).WithPadding(10, 24, 10, 24);
        button.WithButtonAppearance(primary.Light, primary.Dark, primary.Light, primary.Dark,
            style.Appearance.BootstrapRadius, 0.65);
        button.IsEnabled = !playing && !state.IsBusy && state.Item?.HasAudio == true;
        return button;
    }

    private SwiftView ChoiceGroup(NativeNumberDrillState state, NativeNumberDrillItem item, SourceUiStyle style)
    {
        var buttons = item.Choices.Select((choice, index) =>
        {
            var label = style.Text(Views.Text(choice), SourceTextRole.Title2)
                .WithPadding(12, 20, 12, 20).WithMinimumHeight(64);
            var button = style.OutlineButton(label,
                    () => Fire(token => _presenter.TapChoiceAsync(choice, token)))
                .WithAccessibilityLabel(choice)
                .WithAccessibilityIdentifier($"{NativeNumberDrillAccess.Choice}.{index}");
            button.IsEnabled = !state.IsBusy;
            return button;
        }).ToArray();
        return Views.HStack(Views.ButtonGroup(buttons, wraps: true))
            .WithFlexibleWidth(alignment: SwiftHorizontalAlignment.Center);
    }

    private SwiftView Paired(NativeNumberDrillState state, NativeNumberDrillItem item, bool a,
        SourceUiStyle style, Func<string, string> text)
    {
        var feedback = state.Phase == NativePhase.Feedback ? state.Feedback : null;
        var prompt = a ? item.PromptA : item.PromptB;
        var choices = a ? item.ChoicesA : item.ChoicesB;
        var selected = a ? state.SelectedAnswerA : state.SelectedAnswerB;
        var rows = new List<SwiftView>
        {
            style.Text(Views.Text(text(a ? NativeNumberDrillText.PromptA : NativeNumberDrillText.PromptB)),
                SourceTextRole.Body2, SourceTextTone.Secondary),
            style.Text(Views.Text(prompt ?? string.Empty), SourceTextRole.Title3)
                .WithAccessibilityIdentifier(a ? NativeNumberDrillAccess.PromptA : NativeNumberDrillAccess.PromptB),
        };
        if (!choices.IsDefaultOrEmpty)
        {
            var buttons = choices.Select((choice, index) => Chip(style, null, choice,
                selected == choice, feedback is null && !state.IsBusy,
                () => Fire(token => _presenter.SelectPairedAnswerAsync(a, choice, token)),
                $"{(a ? NativeNumberDrillAccess.ChoiceA : NativeNumberDrillAccess.ChoiceB)}.{index}")).ToArray();
            rows.Add(Views.ButtonGroup(buttons, wraps: true).WithFlexibleWidth());
        }
        var hint = a ? item.HintA : item.HintB;
        if (feedback is not null && !string.IsNullOrEmpty(hint))
            rows.Add(style.InfoAlert(Views.HStack(Views.Image("info.circle"),
                    style.Text(Views.Text(hint), SourceTextRole.Body2)), compact: true)
                .WithAccessibilityIdentifier(a ? NativeNumberDrillAccess.HintA : NativeNumberDrillAccess.HintB));
        var stack = Views.VStack(rows.ToArray()).WithPadding(SourceUiLayout.CardInset);
        stack.Spacing = SourceUiLayout.ItemGap;
        var correct = a ? feedback?.CorrectA : feedback?.CorrectB;
        var border = correct is null ? style.Appearance.Border
            : correct.Value ? style.Appearance.Success : style.Appearance.Danger;
        var surface = style.Appearance.CardSurface;
        return stack.WithFlexibleWidth()
            .WithRoundedSurface(surface.Light, surface.Dark, 12, border.Light, border.Dark,
                correct is null ? 1 : 2);
    }

    private IEnumerable<SwiftView> Feedback(NativeNumberDrillState state, NativeNumberDrillFeedback feedback,
        SourceUiStyle style, Func<string, string> text)
    {
        var verdict = feedback.VerdictKey is { } key ? text(key) : feedback.Verdict ?? string.Empty;
        var rows = new List<SwiftView>
        {
            Views.HStack(Views.Image(feedback.IsCorrect ? "checkmark.circle.fill" : "xmark.circle.fill"),
                    style.Text(Views.Text(verdict), SourceTextRole.Title3))
                .WithAccessibilityIdentifier(NativeNumberDrillAccess.Verdict),
        };
        if (!string.IsNullOrEmpty(feedback.UserAnswer))
            rows.Add(LabeledLine(text(NativeNumberDrillText.YouTyped), feedback.UserAnswer, style,
                NativeNumberDrillAccess.YouTyped));
        if (!string.IsNullOrEmpty(feedback.DigitDisplay))
            rows.Add(LabeledLine(text(NativeNumberDrillText.Number), feedback.DigitDisplay, style,
                NativeNumberDrillAccess.DigitAnswer));
        rows.Add(LabeledLine(text(NativeNumberDrillText.Korean), feedback.CanonicalAnswer, style,
            NativeNumberDrillAccess.KoreanAnswer));
        if (!feedback.IsCorrect && !string.IsNullOrEmpty(feedback.ErrorClass))
        {
            var tip = feedback.TipKey is { } tipKey ? text(tipKey) : feedback.Tip ?? string.Empty;
            rows.Add(Views.HStack(Views.Image("info.circle"),
                    LabeledLine($"{feedback.ErrorClass}:", tip, style, NativeNumberDrillAccess.ErrorClass)));
        }
        if (feedback.ProgressTotal is int total && feedback.NextReviewUtc is { } due)
            rows.Add(style.Text(Views.Text(string.Format(CultureInfo.CurrentCulture,
                        text(NativeNumberDrillText.MasteryProgress), feedback.ProgressCorrect ?? 0, total,
                        due.ToLocalTime().ToString("MMM d", CultureInfo.CurrentCulture))),
                    SourceTextRole.Small)
                .WithOpacity(0.75)
                .WithAccessibilityIdentifier(NativeNumberDrillAccess.MasteryProgress));
        var panel = Views.VStack(rows.ToArray());
        panel.Spacing = 6;
        yield return style.Alert(panel, success: feedback.IsCorrect)
            .WithAccessibilityIdentifier(NativeNumberDrillAccess.Feedback);

        if (!feedback.IsCorrect)
        {
            var overrideLabel = Views.HStack(Views.Image("checkmark"),
                Views.Text(text(NativeNumberDrillText.IWasCorrect)));
            overrideLabel.Spacing = 4;
            var overrideButton = style.SuccessOutlineSmallButton(overrideLabel,
                    () => Fire(_presenter.OverrideAsync))
                .WithAccessibilityIdentifier(NativeNumberDrillAccess.Override);
            overrideButton.IsEnabled = !state.IsOverriding;
            yield return Center(overrideButton);
        }

        var next = style.PrimaryButton(
                text(state.IsLastItem ? NativeNumberDrillText.Finish : NativeNumberDrillText.Next),
                () => Fire(_presenter.NextAsync), fullWidth: true)
            .WithAccessibilityIdentifier(NativeNumberDrillAccess.Next);
        yield return next;
    }

    private static SwiftView LabeledLine(string label, string value, SourceUiStyle style, string id)
    {
        var line = Views.HStack(style.Text(Views.Text(label), SourceTextRole.Body2Strong),
                style.Text(Views.Text(value), SourceTextRole.Body2))
            .WithAccessibilityLabel($"{label} {value}")
            .WithAccessibilityIdentifier(id);
        line.Spacing = 4;
        return line;
    }

    private static SwiftView Footer(NativeNumberDrillState state, SourceUiStyle style, Func<string, string> text)
    {
        var progress = $"{state.ItemIndex + 1} / {state.ItemTotal}";
        var correct = string.Format(CultureInfo.CurrentCulture, text(NativeNumberDrillText.CorrectCount),
            state.CorrectCount);
        var success = style.Appearance.Success;
        var badge = Views.HStack(Views.Image("checkmark"),
                style.Text(Views.Text(correct), SourceTextRole.SmallStrong))
            .WithForegroundColor("#FFFFFF", "#FFFFFF")
            .WithPadding(4, 12, 4, 12)
            .WithRoundedSurface(success.Light, success.Dark, 999)
            .WithAccessibilityLabel(correct)
            .WithAccessibilityIdentifier(NativeNumberDrillAccess.CorrectCount);
        var gutter = SourceUiLayout.PageGutter(style.ViewportWidth);
        var surface = style.Appearance.CardSurface;
        return Views.HStack(
                style.Text(Views.Text(progress), SourceTextRole.Body1, SourceTextTone.Secondary)
                    .WithAccessibilityIdentifier(NativeNumberDrillAccess.Progress),
                Views.VStack().WithFlexibleWidth(),
                badge)
            .WithPadding(12, gutter, 12, gutter)
            .WithFlexibleWidth()
            .WithBackgroundColor(surface.Light, surface.Dark);
    }

    // ============ SUMMARY SCREEN ============
    private SwiftView Summary(NativeNumberDrillSummary summary, SourceUiStyle style, Func<string, string> text)
    {
        var stats = Views.HStack(
            Stat($"{summary.CorrectCount}/{summary.TotalItems}", text(NativeNumberDrillText.Correct),
                SourceTextTone.Success, NativeNumberDrillAccess.SummaryCorrect, style),
            Stat(string.Format(CultureInfo.CurrentCulture, text(NativeNumberDrillText.Milliseconds),
                    summary.MedianLatencyMs),
                text(NativeNumberDrillText.MedianTime), SourceTextTone.Body,
                NativeNumberDrillAccess.SummaryMedian, style));
        stats.Spacing = 32;
        var rows = new List<SwiftView>
        {
            Views.HStack(Views.Image("checkmark.circle").WithForegroundColor(
                    style.Appearance.Success.Light, style.Appearance.Success.Dark),
                style.Text(Views.Text(text(NativeNumberDrillText.SessionComplete)), SourceTextRole.Title1,
                    sourceHeading: true)),
            stats,
        };
        if (!summary.ErrorClassBreakdown.IsDefaultOrEmpty)
        {
            var patterns = new List<SwiftView>
            {
                style.Text(Views.Text(text(NativeNumberDrillText.PatternsToReview)), SourceTextRole.Title3,
                    sourceHeading: true),
            };
            var warning = style.Appearance.Warning;
            foreach (var (error, count) in summary.ErrorClassBreakdown)
                patterns.Add(Views.HStack(
                        style.Text(Views.Text(error), SourceTextRole.Body2),
                        Views.VStack().WithFlexibleWidth(),
                        style.Text(Views.Text(count.ToString(CultureInfo.CurrentCulture)), SourceTextRole.SmallStrong)
                            .WithForegroundColor("#212529", "#212529")
                            .WithPadding(2, 8, 2, 8)
                            .WithRoundedSurface(warning.Light, warning.Dark, 6))
                    .WithAccessibilityLabel($"{error} {count}"));
            var list = Views.VStack(patterns.ToArray()).WithFlexibleWidth()
                .WithAccessibilityIdentifier(NativeNumberDrillAccess.Patterns);
            list.Spacing = 6;
            rows.Add(list);
        }
        var again = style.PrimaryButton(text(NativeNumberDrillText.AnotherRound),
                () => Fire(_presenter.TryAgainAsync), fullWidth: true)
            .WithAccessibilityIdentifier(NativeNumberDrillAccess.AnotherRound);
        var done = style.SecondaryButton(text(NativeNumberDrillText.Done), _host.Exit, fullWidth: true)
            .WithAccessibilityIdentifier(NativeNumberDrillAccess.Done);
        var buttons = Views.HStack(again, done);
        buttons.Spacing = SourceUiLayout.ItemGap;
        rows.Add(buttons);
        var card = Views.VStack(rows.ToArray()).WithPadding(SourceUiLayout.LargeCardInset)
            .WithFlexibleWidth(alignment: SwiftHorizontalAlignment.Center);
        card.Spacing = SourceUiLayout.PageGap;
        return style.Card(card).WithAccessibilityIdentifier(NativeNumberDrillAccess.Summary);
    }

    private static SwiftView Stat(string value, string label, SourceTextTone tone, string id, SourceUiStyle style)
    {
        var stat = Views.VStack(style.Text(Views.Text(value), SourceTextRole.Title2, tone),
                style.Text(Views.Text(label), SourceTextRole.Small, SourceTextTone.Secondary))
            .WithAccessibilityLabel($"{label} {value}")
            .WithAccessibilityIdentifier(id);
        return stat;
    }

    private static SwiftView Chip(SourceUiStyle style, string? symbol, string title, bool selected,
        bool enabled, Action action, string id)
    {
        var appearance = style.Appearance;
        var label = symbol is null ? Views.Text(title) : Views.HStack(Views.Image(symbol), Views.Text(title));
        if (symbol is not null)
            label.Spacing = 4;
        var button = style.OutlineButton(label, action)
            .WithAccessibilityLabel(title)
            .WithAccessibilitySelected(selected)
            .WithAccessibilityIdentifier(id);
        if (selected)
        {
            label.WithForegroundColor(appearance.OnPrimary.Light, appearance.OnPrimary.Dark);
            button.WithButtonAppearance(appearance.Primary.Light, appearance.Primary.Dark,
                appearance.Primary.Light, appearance.Primary.Dark, appearance.BootstrapRadius, 0.65);
        }
        button.IsEnabled = enabled;
        return button;
    }

    private static SwiftView Label(string title, SourceUiStyle style) =>
        style.Text(Views.Text(title), SourceTextRole.Body2Strong);

    private static SwiftView Center(SwiftView view) =>
        Views.HStack(view).WithFlexibleWidth(alignment: SwiftHorizontalAlignment.Center);

    /// <summary>Source context icons (bi-cup, bi-clock, bi-cake, bi-currency-dollar, bi-calendar, bi-trophy, bi-123).</summary>
    private static string ContextSymbol(string code) => code switch
    {
        "Counting" => "cup.and.saucer",
        "Time" => "clock",
        "Age" => "birthday.cake",
        "Money" => "dollarsign.circle",
        "Date" => "calendar",
        "Ordinal" => "trophy",
        _ => "number",
    };

    /// <summary>Source chip-system-* (NumberDrill.razor.css): Native purple, Sino teal, Mixed purple-to-teal (first stop; no gradient fill in the bridge), white text.</summary>
    private static SourceColorPair SystemColor(SourceUiStyle style, string system) => system switch
    {
        "Sino" => new("#20C997", "#20C997"),
        _ => new("#6F42C1", "#6F42C1"),
    };

    public void Dispose()
    {
        _presenter.StateChanged -= OnStateChanged;
        _presenter.Dispose();
    }
}
