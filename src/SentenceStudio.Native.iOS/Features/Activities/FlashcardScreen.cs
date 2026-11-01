using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.SwiftUI;
using SentenceStudio.Native.iOS.Styling;
using Views = Microsoft.SwiftUI.SwiftUI;

namespace SentenceStudio.Native.iOS.Features.Activities;

/// <summary>
/// SwiftUI Flashcard Preview (source FlashcardActivity.razor). Tapping the card
/// flips it; Incorrect/Correct answer it. The installed SwiftUI facade has no
/// drag gesture or offset modifier, so the source horizontal swipe is reached
/// through the buttons only (reported bridge gap); the 160 ms throw still
/// disables both buttons.
/// </summary>
internal sealed class FlashcardScreen : IActivityScreen
{
    private readonly ActivityScreenHost _host;
    private readonly NativeFlashcardPresenter _presenter;
    private NativeFlashcardState _state = new(NativePhase.Loading);

    public FlashcardScreen(ActivityScreenHost host)
    {
        _host = host;
        _presenter = host.Services.GetRequiredService<NativeFlashcardPresenter>();
        _presenter.StateChanged += OnStateChanged;
    }

    public string TitleKey => NativeFlashcardText.Title;

    public bool HasExited => _state.Destination == NativeDestination.TodaysPlan;

    public async Task OpenAsync(NativeActivityLaunch launch, CancellationToken cancellationToken) =>
        _state = await _presenter.OpenAsync(launch, cancellationToken);

    public async Task ExitAsync(CancellationToken cancellationToken) =>
        _state = await _presenter.ExitAsync(cancellationToken);

    private void OnStateChanged(NativeFlashcardState state)
    {
        _state = state;
        _host.Publish();
    }

    /// <summary>Card actions bypass the controller's busy gate so the 160 ms throw never disables Back.</summary>
    private void Fire(Func<CancellationToken, Task<NativeFlashcardState>> action) =>
        _ = FireAsync(action);

    private async Task FireAsync(Func<CancellationToken, Task<NativeFlashcardState>> action)
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
            _host.Logger?.LogError(ex, "Native iOS Flashcard Preview action failed.");
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
                items.Add(Views.IndeterminateProgress(text(NativeFlashcardText.Loading))
                    .WithFlexibleWidth(alignment: SwiftHorizontalAlignment.Center)
                    .WithAccessibilityIdentifier(NativeFlashcardAccess.Loading));
                break;
            case NativePhase.Summary:
                items.Add(Summary(state, style, text));
                break;
            case NativePhase.Ready when state.Current is { } card:
                items.Add(Header(state, style, text));
                items.Add(Card(card, state, style, text));
                items.Add(Answers(state, style, text));
                break;
            default:
                // Empty, Error and Blocked offer only Go Back (source load error).
                var empty = state.Phase == NativePhase.Empty;
                items.Add(style.Text(Views.Text(text(state.ErrorKey ?? NativeFlashcardText.NoVocabulary)),
                        SourceTextRole.Body1,
                        empty ? SourceTextTone.Secondary : SourceTextTone.Danger)
                    .WithFlexibleWidth(alignment: SwiftHorizontalAlignment.Center)
                    .WithAccessibilityIdentifier(empty ? NativeFlashcardAccess.Empty : NativeFlashcardAccess.Error));
                items.Add(style.SecondaryButton(text(NativeFlashcardText.GoBack), _host.Exit)
                    .WithFlexibleWidth(alignment: SwiftHorizontalAlignment.Center)
                    .WithAccessibilityIdentifier(NativeFlashcardAccess.EmptyBack));
                break;
        }

        var content = Views.VStack(items.ToArray())
            .WithFlexibleWidth(560, SwiftHorizontalAlignment.Center)
            .WithPadding(16, SourceUiLayout.PageGutter(style.ViewportWidth), 16,
                SourceUiLayout.PageGutter(style.ViewportWidth))
            .WithAccessibilityIdentifier("native.flashcards.content");
        content.Spacing = SourceUiLayout.PageGap;
        return style.Page(Views.Scroll(content)
            .WithAccessibilityIdentifier(NativeFlashcardAccess.Screen));
    }

    private static SwiftView Header(NativeFlashcardState state, SourceUiStyle style,
        Func<string, string> text)
    {
        var counter = style.Text(Views.Text(string.Format(CultureInfo.CurrentCulture,
                text(NativeFlashcardText.CardCounter), state.CurrentIndex + 1, state.Total)),
                SourceTextRole.Body2, SourceTextTone.Secondary)
            .WithFlexibleWidth()
            .WithAccessibilityIdentifier(NativeFlashcardAccess.Counter);
        var correct = style.Text(Views.Text(
                $"{state.CorrectCount.ToString(CultureInfo.CurrentCulture)} {text(NativeFlashcardText.Correct)}"),
                SourceTextRole.Body2, SourceTextTone.Success)
            .WithAccessibilityIdentifier(NativeFlashcardAccess.CorrectTally);
        var incorrect = style.Text(Views.Text(
                $"{state.IncorrectCount.ToString(CultureInfo.CurrentCulture)} {text(NativeFlashcardText.Incorrect)}"),
                SourceTextRole.Body2, SourceTextTone.Danger)
            .WithAccessibilityIdentifier(NativeFlashcardAccess.IncorrectTally);
        var separator = style.Text(Views.Text("·"), SourceTextRole.Body2,
                SourceTextTone.Secondary);
        var tallies = Views.HStack(correct, separator, incorrect);
        tallies.Spacing = 4;
        var row = Views.HStack(counter, tallies).WithFlexibleWidth();
        row.Spacing = SourceUiLayout.ItemGap;
        return row;
    }

    private SwiftView Card(NativeFlashcard card, NativeFlashcardState state,
        SourceUiStyle style, Func<string, string> text)
    {
        var term = state.IsFlipped ? card.NativeTerm : card.TargetTerm;
        var label = style.Text(Views.Text(term), SourceTextRole.Display, SourceTextTone.Primary)
            .WithSpeechLanguage(state.IsFlipped ? null : NativeFlashcardText.TargetSpeechLanguage)
            .WithPadding(24)
            .WithFlexibleWidth(alignment: SwiftHorizontalAlignment.Center)
            .WithMinimumHeight(280);
        // style.Card re-frames its view leading-aligned; wrap so the term stays centered.
        var surface = style.Card(Views.VStack(label), radius: 16);
        var button = Views.Button(surface, () => Fire(_presenter.FlipAsync))
            .WithButtonStyle(SwiftButtonStyle.Plain)
            .WithFlexibleWidth()
            .WithAccessibilityLabel(term)
            .WithSpeechLanguage(state.IsFlipped ? null : NativeFlashcardText.TargetSpeechLanguage)
            .WithAccessibilityIdentifier(NativeFlashcardAccess.Card)
            .WithOpacity(state.IsAnimating ? 0.4 : 1);
        button.IsEnabled = !state.IsAnimating;
        return button;
    }

    private SwiftView Answers(NativeFlashcardState state, SourceUiStyle style,
        Func<string, string> text)
    {
        var appearance = style.Appearance;
        var incorrectLabel = Views.HStack(Views.Image("arrow.left"),
                Views.Text(text(NativeFlashcardText.Incorrect)))
            .WithForegroundColor(appearance.Danger.Light, appearance.Danger.Dark);
        incorrectLabel.Spacing = 4;
        var incorrect = style.OutlineButton(incorrectLabel,
                () => Fire(token => _presenter.AnswerAsync(false, token)), fullWidth: true)
            .WithAccessibilityLabel(text(NativeFlashcardText.Incorrect))
            .WithAccessibilityIdentifier(NativeFlashcardAccess.Incorrect);
        incorrect.IsEnabled = !state.IsAnimating;
        var correctLabel = Views.HStack(Views.Text(text(NativeFlashcardText.Correct)),
                Views.Image("arrow.right"))
            .WithForegroundColor(appearance.Success.Light, appearance.Success.Dark);
        var correct = style.OutlineButton(correctLabel,
                () => Fire(token => _presenter.AnswerAsync(true, token)), fullWidth: true)
            .WithAccessibilityLabel(text(NativeFlashcardText.Correct))
            .WithAccessibilityIdentifier(NativeFlashcardAccess.Correct);
        correct.IsEnabled = !state.IsAnimating;
        var row = Views.HStack(incorrect, correct).WithFlexibleWidth();
        row.Spacing = SourceUiLayout.ItemGap;
        return row;
    }

    private SwiftView Summary(NativeFlashcardState state, SourceUiStyle style,
        Func<string, string> text)
    {
        var stats = Views.HStack(
            Stat(state.CorrectCount, NativeFlashcardText.Correct, SourceTextTone.Success,
                NativeFlashcardAccess.SummaryCorrect, style, text),
            Stat(state.IncorrectCount, NativeFlashcardText.Incorrect, SourceTextTone.Danger,
                NativeFlashcardAccess.SummaryIncorrect, style, text),
            Stat(state.Results.IsDefault ? 0 : state.Results.Length, NativeFlashcardText.Total,
                SourceTextTone.Body, NativeFlashcardAccess.SummaryTotal, style, text))
            .WithFlexibleWidth(alignment: SwiftHorizontalAlignment.Center);
        stats.Spacing = 32;
        var rows = new List<SwiftView> { stats, Views.Divider() };
        var results = state.Results.IsDefault ? [] : state.Results;
        foreach (var result in results)
        {
            var mark = Views.Image(result.WasCorrect ? "checkmark.circle.fill" : "xmark.circle.fill")
                .WithForegroundColor(
                    result.WasCorrect ? style.Appearance.Success.Light : style.Appearance.Danger.Light,
                    result.WasCorrect ? style.Appearance.Success.Dark : style.Appearance.Danger.Dark);
            var target = style.Text(Views.Text(result.Card.TargetTerm), SourceTextRole.Body1Bold,
                    SourceTextTone.Primary)
                .WithSpeechLanguage(NativeFlashcardText.TargetSpeechLanguage);
            var arrow = style.Text(Views.Text("→"), SourceTextRole.Body1, SourceTextTone.Secondary);
            var native = style.Text(Views.Text(result.Card.NativeTerm), SourceTextRole.Body1);
            var row = Views.HStack(mark, target, arrow, native)
                .WithFlexibleWidth()
                .WithAccessibilityLabel(string.Format(CultureInfo.CurrentCulture, "{0}, {1}, {2}",
                    result.Card.TargetTerm, result.Card.NativeTerm,
                    text(result.WasCorrect ? NativeFlashcardText.Correct : NativeFlashcardText.Incorrect)))
                .WithAccessibilityIdentifier($"{NativeFlashcardAccess.SummaryRow}.{result.Card.Index}");
            row.Spacing = SourceUiLayout.ItemGap;
            rows.Add(row);
        }
        var list = Views.VStack(rows.ToArray()).WithPadding(SourceUiLayout.CardInset);
        list.Spacing = SourceUiLayout.ItemGap;

        var heading = Views.HStack(
                Views.Image("checklist"),
                style.Text(Views.Text(text(NativeFlashcardText.Summary)),
                    SourceTextRole.Title1, sourceHeading: true))
            .WithFlexibleWidth(alignment: SwiftHorizontalAlignment.Center);
        heading.Spacing = SourceUiLayout.ItemGap;
        var page = Views.VStack(
                heading,
                style.Card(list, maxWidth: 560),
                Views.VStack(style.SecondaryButton(text(NativeFlashcardText.Done), _host.Exit)
                        .WithAccessibilityIdentifier(NativeFlashcardAccess.Done))
                    .WithFlexibleWidth(alignment: SwiftHorizontalAlignment.Center))
            .WithFlexibleWidth(alignment: SwiftHorizontalAlignment.Center)
            .WithAccessibilityIdentifier(NativeFlashcardAccess.Summary);
        page.Spacing = SourceUiLayout.PageGap;
        return page;
    }

    private static SwiftView Stat(int value, string labelKey, SourceTextTone tone,
        string id, SourceUiStyle style, Func<string, string> text) =>
        Views.VStack(
                style.Text(Views.Text(value.ToString(CultureInfo.CurrentCulture)),
                    SourceTextRole.Title2, tone),
                style.Text(Views.Text(text(labelKey)), SourceTextRole.Small,
                    SourceTextTone.Secondary))
            .WithAccessibilityLabel($"{text(labelKey)} {value}")
            .WithAccessibilityIdentifier(id);

    public void Dispose()
    {
        _presenter.StateChanged -= OnStateChanged;
        _presenter.Dispose();
    }
}
