using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.SwiftUI;
using SentenceStudio.Native.iOS.Styling;
using Views = Microsoft.SwiftUI.SwiftUI;

namespace SentenceStudio.Native.iOS.Features.Activities;

/// <summary>
/// SwiftUI Word Association (source WordAssociation.razor). The scrolling
/// content holds the word header (position, target term, native gloss) and
/// the clue list with grades, explanations, corrections and the word score;
/// the source activity-input-bar (clue field and add, Grade Clues, previous /
/// position / Next or Finish Round) is a bottom inset. Source toasts are shown
/// inline. Typing never re-renders, so Korean IME composition is not interrupted.
/// </summary>
internal sealed class WordAssociationScreen : IActivityScreen
{
    private readonly ActivityScreenHost _host;
    private readonly NativeWordAssociationPresenter _presenter;
    private NativeWordAssociationState _state = new(NativePhase.Loading);
    private SwiftView? _add;

    public WordAssociationScreen(ActivityScreenHost host)
    {
        _host = host;
        _presenter = host.Services.GetRequiredService<NativeWordAssociationPresenter>();
        _presenter.StateChanged += OnStateChanged;
    }

    public string TitleKey => NativeWordAssociationText.Title;

    public NativeMessage? Toast => NativeMessages.LatestToast(_state.Notices);

    public bool HasExited => _state.Destination == NativeDestination.TodaysPlan;

    public async Task OpenAsync(NativeActivityLaunch launch, CancellationToken cancellationToken) =>
        _state = await _presenter.OpenAsync(launch, cancellationToken);

    public async Task ExitAsync(CancellationToken cancellationToken) =>
        _state = await _presenter.ExitAsync(cancellationToken);

    private void OnStateChanged(NativeWordAssociationState state)
    {
        _state = state;
        _host.Publish();
    }

    /// <summary>Commands do not take the controller's busy gate: grading can take seconds and must not disable Back; the presenter serializes them.</summary>
    private void Fire(Func<CancellationToken, Task<NativeWordAssociationState>> action) =>
        _ = FireAsync(action);

    private async Task FireAsync(Func<CancellationToken, Task<NativeWordAssociationState>> action)
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
            _host.Logger?.LogError(ex, "Native iOS Word Association action failed.");
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
        _add = null;
        if (!state.Notices.IsDefaultOrEmpty && state.Phase is NativePhase.Ready
                or NativePhase.Empty or NativePhase.Summary)
            if (Notices(state, style, text) is { } notices)
                items.Add(notices);
        switch (state.Phase)
        {
            case NativePhase.Idle:
            case NativePhase.Loading:
                items.Add(Views.IndeterminateProgress(text(NativeWordAssociationText.Loading))
                    .WithAccessibilityIdentifier(NativeWordAssociationAccess.Loading));
                items.Add(style.Text(Views.Text(text(NativeWordAssociationText.Loading)),
                    SourceTextRole.Body1, SourceTextTone.Secondary));
                break;
            case NativePhase.Summary:
                items.Add(Summary(state, style, text));
                break;
            case NativePhase.Ready:
                items.Add(WordHeader(state, style, text));
                items.Add(ClueList(state, style, text));
                break;
            default:
                // Empty and Blocked offer only Go Back (source empty state).
                var empty = state.Phase == NativePhase.Empty;
                items.Add(style.Text(Views.Text(text(state.ErrorKey ?? NativeWordAssociationText.NoVocabulary)),
                        SourceTextRole.Body1, empty ? SourceTextTone.Secondary : SourceTextTone.Danger)
                    .WithFlexibleWidth(alignment: SwiftHorizontalAlignment.Center)
                    .WithAccessibilityIdentifier(empty ? NativeWordAssociationAccess.Empty
                        : NativeWordAssociationAccess.Error));
                items.Add(Views.HStack(style.SecondaryButton(text(NativeWordAssociationText.GoBack), _host.Exit)
                        .WithAccessibilityIdentifier(NativeWordAssociationAccess.EmptyBack))
                    .WithFlexibleWidth(alignment: SwiftHorizontalAlignment.Center));
                break;
        }

        var gutter = SourceUiLayout.PageGutter(style.ViewportWidth);
        var content = Views.VStack(items.ToArray())
            .WithPadding(16, gutter, 16, gutter)
            .WithFlexibleWidth()
            .WithAccessibilityIdentifier("native.word-association.content");
        content.Spacing = SourceUiLayout.PageGap;
        var page = Views.Scroll(content).WithAccessibilityIdentifier(NativeWordAssociationAccess.Screen);
        return style.Page(state.Phase == NativePhase.Ready
            ? Views.BottomInset(page, InputBar(state, style, text))
            : page);
    }

    /// <summary>Errors and warnings stay inline until the next action; success and info are the scene's toast.</summary>
    private SwiftView? Notices(NativeWordAssociationState state, SourceUiStyle style, Func<string, string> text)
    {
        return style.MessageBanners(state.Notices, text, index => $"{NativeWordAssociationAccess.Notice}.{index}",
            () => Fire(_presenter.DismissNoticesAsync), NativeWordAssociationAccess.DismissNotices,
            text(NativeWordAssociationText.Dismiss));
    }

    private static SwiftView WordHeader(NativeWordAssociationState state, SourceUiStyle style,
        Func<string, string> text)
    {
        var rows = new List<SwiftView>
        {
            style.Text(Views.Text(string.Format(CultureInfo.CurrentCulture,
                        text(NativeWordAssociationText.Word), state.Index + 1, state.WordCount)),
                    SourceTextRole.Body2, SourceTextTone.Secondary)
                .WithAccessibilityIdentifier(NativeWordAssociationAccess.Progress),
            style.Text(Views.Text(state.TargetTerm), SourceTextRole.Display, sourceHeading: true)
                .WithAccessibilityIdentifier(NativeWordAssociationAccess.Target)
        };
        if (!string.IsNullOrEmpty(state.NativeTerm))
            rows.Add(style.Text(Views.Text(state.NativeTerm), SourceTextRole.Body1, SourceTextTone.Secondary)
                .WithAccessibilityIdentifier(NativeWordAssociationAccess.NativeTerm));
        var header = Views.VStack(rows.ToArray()).WithFlexibleWidth();
        header.Spacing = 4;
        return header;
    }

    private SwiftView ClueList(NativeWordAssociationState state, SourceUiStyle style, Func<string, string> text)
    {
        var rows = new List<SwiftView>();
        if (state.ShowsInstructions)
        {
            var instructions = Views.VStack(
                    style.Text(Views.Text(text(NativeWordAssociationText.WriteClues)), SourceTextRole.Body2,
                        SourceTextTone.Secondary),
                    style.Text(Views.Text(text(NativeWordAssociationText.ClueBonus)), SourceTextRole.Small,
                        SourceTextTone.Secondary))
                .WithFlexibleWidth()
                .WithAccessibilityIdentifier(NativeWordAssociationAccess.Instructions);
            instructions.Spacing = 4;
            rows.Add(instructions);
        }
        if (!state.Clues.IsDefaultOrEmpty)
            foreach (var clue in state.Clues)
                rows.Add(ClueRow(clue, state, style, text));
        if (state.IsGrading)
        {
            var grading = Views.HStack(
                    Views.IndeterminateProgress(text(NativeWordAssociationText.GradingClues)),
                    style.Text(Views.Text(text(NativeWordAssociationText.GradingClues)), SourceTextRole.Body2,
                        SourceTextTone.Secondary))
                .WithAccessibilityIdentifier(NativeWordAssociationAccess.Grading);
            grading.Spacing = 8;
            rows.Add(grading);
        }
        if (state.HasGraded)
            rows.Add(style.Text(Views.Text(string.Format(CultureInfo.CurrentCulture,
                        text(NativeWordAssociationText.WordScore), state.WordScore, state.WordMax)),
                    SourceTextRole.Body1Strong)
                .WithAccessibilityIdentifier(NativeWordAssociationAccess.WordScore));
        var list = Views.VStack(rows.ToArray()).WithFlexibleWidth();
        list.Spacing = 8;
        return list;
    }

    private SwiftView ClueRow(NativeWordAssociationClue clue, NativeWordAssociationState state,
        SourceUiStyle style, Func<string, string> text)
    {
        var id = clue.AccessibilityId;
        var appearance = style.Appearance;
        if (!clue.Graded)
        {
            var pending = Views.Image("clock")
                .WithForegroundColor(appearance.SecondaryText.Light, appearance.SecondaryText.Dark)
                .WithAccessibilityLabel(text(NativeWordAssociationText.Pending))
                .WithAccessibilityIdentifier($"{id}.status");
            var remove = Views.Button(Views.Image("xmark"),
                    () => Fire(token => _presenter.RemoveClueAsync(clue.Index, token)))
                .WithButtonStyle(SwiftButtonStyle.Bordered)
                .WithMinimumHeight(SourceUiLayout.MinimumTouchHeight)
                .WithForegroundColor(appearance.Danger.Light, appearance.Danger.Dark)
                .WithAccessibilityLabel($"{text(NativeWordAssociationText.RemoveClue)} {clue.Text}")
                .WithAccessibilityIdentifier($"{id}.remove");
            remove.IsEnabled = !state.IsGrading;
            var row = Views.HStack(pending,
                    style.Text(Views.Text(clue.Text), SourceTextRole.Body1)
                        .WithFlexibleWidth()
                        .WithAccessibilityIdentifier($"{id}.text"),
                    remove)
                .WithFlexibleWidth()
                .WithAccessibilityIdentifier(id);
            row.Spacing = 8;
            return row;
        }

        var color = clue.Related ? appearance.Success : appearance.Danger;
        var status = Views.Image(clue.Related ? "checkmark.circle.fill" : "xmark.circle.fill")
            .WithForegroundColor(color.Light, color.Dark)
            .WithAccessibilityLabel(text(clue.Related ? NativeWordAssociationText.Related
                : NativeWordAssociationText.NotRelated))
            .WithAccessibilityIdentifier($"{id}.status");
        var line = new List<SwiftView>
        {
            style.Text(Views.Text(clue.Text), SourceTextRole.Body1).WithAccessibilityIdentifier($"{id}.text")
        };
        if (clue.ShowsDouble)
            line.Add(style.Text(Views.Text("2x"), SourceTextRole.BadgeSmall)
                .WithForegroundColor(appearance.OnPrimary.Light, appearance.OnPrimary.Dark)
                .WithPadding(2, 6, 2, 6)
                .WithRoundedSurface(appearance.Primary.Light, appearance.Primary.Dark,
                    appearance.BootstrapRadius, appearance.Primary.Light, appearance.Primary.Dark, 1)
                .WithAccessibilityLabel(text(NativeWordAssociationText.DoublePoints))
                .WithAccessibilityIdentifier($"{id}.double"));
        var top = Views.HStack(line.ToArray());
        top.Spacing = 6;
        var body = new List<SwiftView> { top };
        if (!string.IsNullOrEmpty(clue.Explanation))
            body.Add(style.Text(Views.Text(clue.Explanation), SourceTextRole.Small, SourceTextTone.Secondary)
                .WithAccessibilityIdentifier($"{id}.explanation"));
        if (clue.ShowsCorrection)
            body.Add(style.Text(Views.Text(string.Format(CultureInfo.CurrentCulture,
                        text(NativeWordAssociationText.Corrected), clue.CorrectedText)),
                    SourceTextRole.Small, SourceTextTone.Secondary)
                .WithAccessibilityIdentifier($"{id}.corrected"));
        var column = Views.VStack(body.ToArray()).WithFlexibleWidth();
        column.Spacing = 2;
        var graded = Views.HStack(status, column).WithFlexibleWidth().WithAccessibilityIdentifier(id);
        graded.Spacing = 8;
        return graded;
    }

    private SwiftView InputBar(NativeWordAssociationState state, SourceUiStyle style, Func<string, string> text)
    {
        var placeholder = text(NativeWordAssociationText.TypeClue);
        var field = style.TextInput(Views.TextField(placeholder, state.Draft,
                value =>
                {
                    _presenter.SetDraft(value);
                    UpdateAvailability(style, value);
                }))
            .WithFlexibleWidth()
            .WithAccessibilityLabel(placeholder)
            .WithAccessibilityIdentifier(NativeWordAssociationAccess.Input)
            .OnSubmit(() => Fire(_presenter.AddClueAsync));
        field.IsEnabled = !state.IsGrading;
        var add = style.LabelButton(Views.Image("plus"), () => Fire(_presenter.AddClueAsync),
                SourceButtonRole.Primary, 14)
            .WithAccessibilityLabel(text(NativeWordAssociationText.AddClue))
            .WithAccessibilityIdentifier(NativeWordAssociationAccess.Add);
        _add = add;
        UpdateAvailability(style, state.Draft);
        var inputRow = Views.HStack(field, add).WithFlexibleWidth();
        inputRow.Spacing = 8;
        var rows = new List<SwiftView> { inputRow };

        if (state.HasUngraded)
        {
            var label = Views.HStack(Views.Image("checkmark.circle"),
                Views.Text(text(NativeWordAssociationText.GradeClues)));
            label.Spacing = 4;
            var grade = style.LabelButton(label, () => Fire(_presenter.GradeAsync), SourceButtonRole.Primary)
                .WithFlexibleWidth()
                .WithAccessibilityLabel(text(NativeWordAssociationText.GradeClues))
                .WithAccessibilityIdentifier(NativeWordAssociationAccess.Grade);
            SourceUiStyle.SetCustomButtonAvailability(grade, style.Appearance, SourceButtonRole.Primary,
                state.CanGrade);
            rows.Add(grade);
        }

        var previous = Views.Button(Views.Image("chevron.left"), () => Fire(_presenter.PreviousAsync))
            .WithButtonStyle(SwiftButtonStyle.Bordered)
            .WithMinimumHeight(SourceUiLayout.MinimumTouchHeight)
            .WithAccessibilityLabel(text(NativeWordAssociationText.Previous))
            .WithAccessibilityIdentifier(NativeWordAssociationAccess.Previous);
        previous.IsEnabled = state.CanPrevious;
        var position = style.Text(Views.Text(string.Format(CultureInfo.CurrentCulture,
                    "{0} / {1}", state.Index + 1, state.WordCount)),
                SourceTextRole.Body2, SourceTextTone.Secondary)
            .WithFlexibleWidth(alignment: SwiftHorizontalAlignment.Center)
            .WithAccessibilityIdentifier(NativeWordAssociationAccess.Position);
        SwiftView advance;
        if (state.IsLastWord)
        {
            advance = style.PrimaryButton(text(NativeWordAssociationText.FinishRound),
                    () => Fire(_presenter.FinishAsync))
                .WithAccessibilityIdentifier(NativeWordAssociationAccess.Finish);
            SourceUiStyle.SetCustomButtonAvailability(advance, style.Appearance, SourceButtonRole.Primary,
                state.CanAdvance);
        }
        else
        {
            advance = style.SecondaryButton(text(NativeWordAssociationText.Next),
                    () => Fire(_presenter.NextAsync))
                .WithAccessibilityIdentifier(NativeWordAssociationAccess.Next);
            SourceUiStyle.SetCustomButtonAvailability(advance, style.Appearance,
                SourceButtonRole.FilledSecondary, state.CanAdvance);
        }
        rows.Add(Views.HStack(previous, position, advance).WithFlexibleWidth());

        var gutter = SourceUiLayout.PageGutter(style.ViewportWidth);
        var bar = Views.VStack(rows.ToArray())
            .WithPadding(8, gutter, 8, gutter)
            .WithFlexibleWidth()
            .WithBackgroundColor(style.Appearance.BodyBackground.Light, style.Appearance.BodyBackground.Dark);
        bar.Spacing = 8;
        return bar;
    }

    private void UpdateAvailability(SourceUiStyle style, string? draft)
    {
        if (_add is not null)
            SourceUiStyle.SetCustomButtonAvailability(_add, style.Appearance, SourceButtonRole.Primary,
                !string.IsNullOrWhiteSpace(draft) && !_state.IsGrading);
    }

    private SwiftView Summary(NativeWordAssociationState state, SourceUiStyle style, Func<string, string> text)
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
            Stat(state.TotalRelated.ToString(CultureInfo.CurrentCulture), NativeWordAssociationText.Related,
                appearance.Success, NativeWordAssociationAccess.SummaryRelated),
            Stat((state.TotalClues - state.TotalRelated).ToString(CultureInfo.CurrentCulture),
                NativeWordAssociationText.NotRelated, appearance.Danger,
                NativeWordAssociationAccess.SummaryNotRelated),
            Stat(state.TotalScore.ToString(CultureInfo.CurrentCulture), NativeWordAssociationText.Score,
                appearance.Primary, NativeWordAssociationAccess.SummaryScore));
        stats.Spacing = 32;
        static SwiftView Centered(SwiftView view) =>
            view.WithFlexibleWidth(alignment: SwiftHorizontalAlignment.Center);
        var rows = new List<SwiftView>
        {
            Centered(Views.HStack(
                Views.Image("trophy"),
                style.Text(Views.Text(text(NativeWordAssociationText.RoundComplete)),
                    SourceTextRole.Title1, sourceHeading: true))),
            style.Card(stats.WithPadding(SourceUiLayout.CardInset), maxWidth: 340)
        };
        if (state.HighScore == NativeWordAssociationHighScore.AllTime)
        {
            var label = Views.HStack(Views.Image("star.fill"),
                style.Text(Views.Text(text(NativeWordAssociationText.NewAllTimeHigh)), SourceTextRole.Body2));
            label.Spacing = 4;
            rows.Add(style.Alert(label, success: true)
                .WithAccessibilityIdentifier(NativeWordAssociationAccess.HighScore));
        }
        else if (state.HighScore == NativeWordAssociationHighScore.Weekly)
        {
            var label = Views.HStack(Views.Image("calendar.badge.plus"),
                style.Text(Views.Text(text(NativeWordAssociationText.BestWeek)), SourceTextRole.Body2));
            label.Spacing = 4;
            rows.Add(style.InfoAlert(label)
                .WithAccessibilityIdentifier(NativeWordAssociationAccess.HighScore));
        }
        rows.Add(Centered(Views.HStack(style.PrimaryButton(text(NativeWordAssociationText.PlayAgain),
                () => Fire(_presenter.PlayAgainAsync))
            .WithAccessibilityIdentifier(NativeWordAssociationAccess.PlayAgain))));
        rows.Add(Centered(Views.HStack(style.SecondaryButton(text(NativeWordAssociationText.Done), _host.Exit)
            .WithAccessibilityIdentifier(NativeWordAssociationAccess.Done))));
        var summary = Views.VStack(rows.ToArray())
            .WithFlexibleWidth(alignment: SwiftHorizontalAlignment.Center)
            .WithAccessibilityIdentifier(NativeWordAssociationAccess.Summary);
        summary.Spacing = SourceUiLayout.PageGap;
        return summary;
    }

    public void Dispose()
    {
        _presenter.StateChanged -= OnStateChanged;
        _presenter.Dispose();
    }
}
