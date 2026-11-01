using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.SwiftUI;
using SentenceStudio.Native.iOS.Styling;
using Views = Microsoft.SwiftUI.SwiftUI;

namespace SentenceStudio.Native.iOS.Features.Activities;

/// <summary>
/// SwiftUI Vocabulary Matching (source VocabMatching.razor). The page header
/// keeps the source actions: "New game" as a toolbar button and the
/// "Show all / Hide native" toggle in the overflow menu.
/// </summary>
internal sealed class VocabularyMatchingScreen : IActivityScreen
{
    private readonly ActivityScreenHost _host;
    private readonly NativeMatchingPresenter _presenter;
    private NativeMatchingState _state = new(NativePhase.Loading);

    public VocabularyMatchingScreen(ActivityScreenHost host)
    {
        _host = host;
        _presenter = host.Services.GetRequiredService<NativeMatchingPresenter>();
        _presenter.StateChanged += OnStateChanged;
    }

    public string TitleKey => NativeMatchingText.Title;

    public bool HasExited => _state.Destination == NativeDestination.TodaysPlan;

    public async Task OpenAsync(NativeActivityLaunch launch, CancellationToken cancellationToken) =>
        _state = await _presenter.OpenAsync(launch, cancellationToken);

    public async Task ExitAsync(CancellationToken cancellationToken) =>
        _state = await _presenter.ExitAsync(cancellationToken);

    private void OnStateChanged(NativeMatchingState state)
    {
        _state = state;
        _host.Publish();
    }

    /// <summary>Tile taps do not take the controller's busy gate: the presenter serializes them and the 600 ms check must not disable navigation.</summary>
    private void Fire(Func<CancellationToken, Task<NativeMatchingState>> action) =>
        _ = FireAsync(action);

    private async Task FireAsync(Func<CancellationToken, Task<NativeMatchingState>> action)
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
            _host.Logger?.LogError(ex, "Native iOS Vocabulary Matching action failed.");
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
                items.Add(Views.IndeterminateProgress(text(NativeMatchingText.Loading))
                    .WithAccessibilityIdentifier(NativeMatchingAccess.Loading));
                items.Add(style.Text(Views.Text(text(NativeMatchingText.Loading)),
                    SourceTextRole.Body1, SourceTextTone.Secondary));
                break;
            case NativePhase.Summary:
                items.Add(Complete(state, style, text));
                break;
            case NativePhase.Ready:
                if (state.ErrorKey is { } error)
                    items.Add(style.Text(Views.Text(text(error)),
                            SourceTextRole.Body2, SourceTextTone.Danger)
                        .WithAccessibilityIdentifier(NativeMatchingAccess.Error));
                items.Add(Status(state, style, text));
                items.Add(Grid(state, style, text));
                break;
            default:
                // Empty, Blocked and Error offer only Go Back (source empty state).
                items.Add(style.Text(Views.Text(text(state.ErrorKey ?? NativeMatchingText.NoVocabulary)),
                        SourceTextRole.Body1,
                        state.Phase == NativePhase.Empty ? SourceTextTone.Secondary : SourceTextTone.Danger)
                    .WithAccessibilityIdentifier(state.Phase == NativePhase.Empty
                        ? NativeMatchingAccess.Empty : NativeMatchingAccess.Error));
                items.Add(style.SecondaryButton(text(NativeMatchingText.GoBack), _host.Exit)
                    .WithAccessibilityIdentifier(NativeMatchingAccess.EmptyBack));
                break;
        }

        var content = Views.VStack(items.ToArray())
            .WithPadding(16, SourceUiLayout.PageGutter(style.ViewportWidth), 16,
                SourceUiLayout.PageGutter(style.ViewportWidth))
            .WithFlexibleWidth()
            .WithAccessibilityIdentifier("native.matching.content");
        content.Spacing = SourceUiLayout.PageGap;
        var page = style.Page(Views.Scroll(content)
            .WithAccessibilityIdentifier(NativeMatchingAccess.Screen));
        if (state.Phase == NativePhase.Blocked)
            return page;

        var newGame = Views.Button(Views.Image("arrow.clockwise"),
                () => Fire(_presenter.RestartAsync))
            .WithAccessibilityLabel(text(NativeMatchingText.NewGame))
            .WithAccessibilityIdentifier(NativeMatchingAccess.NewGame);
        var toggle = Views.Button(text(state.HideNativeWords
                    ? NativeMatchingText.ShowAll : NativeMatchingText.HideNative),
                () => Fire(_presenter.ToggleHideNativeAsync))
            .WithAccessibilityIdentifier(NativeMatchingAccess.HideToggle);
        return Views.Toolbar(page, newGame,
            Views.Menu(Views.Image("ellipsis.circle"), toggle)
                .WithAccessibilityLabel(text("More"))
                .WithAccessibilityIdentifier(NativeMatchingAccess.Menu));
    }

    private static SwiftView Status(NativeMatchingState state, SourceUiStyle style,
        Func<string, string> text)
    {
        var matched = Views.HStack(
                Views.Image("checkmark.circle.fill").WithForegroundColor(
                    style.Appearance.Success.Light, style.Appearance.Success.Dark),
                style.Text(Views.Text($"{state.MatchedPairs}/{state.TotalPairs}"),
                    SourceTextRole.Small))
            .WithAccessibilityLabel(string.Format(CultureInfo.CurrentCulture, "{0} {1}/{2}",
                text(NativeMatchingText.Matched), state.MatchedPairs, state.TotalPairs))
            .WithAccessibilityIdentifier(NativeMatchingAccess.MatchedCount);
        var misses = Views.HStack(
                Views.Image("xmark.circle.fill").WithForegroundColor(
                    style.Appearance.Danger.Light, style.Appearance.Danger.Dark),
                style.Text(Views.Text(state.Misses.ToString(CultureInfo.CurrentCulture)),
                    SourceTextRole.Small))
            .WithAccessibilityLabel(string.Format(CultureInfo.CurrentCulture, "{0} {1}",
                text(NativeMatchingText.Misses), state.Misses))
            .WithAccessibilityIdentifier(NativeMatchingAccess.MissCount);
        var bar = Views.HStack(matched, misses)
            .WithAccessibilityIdentifier(NativeMatchingAccess.Status);
        bar.Spacing = 20;
        return bar;
    }

    /// <summary>Source CSS: 2 columns up to 768 pt wide, 4 columns above.</summary>
    internal static int Columns(double viewportWidth) => viewportWidth <= 768 ? 2 : 4;

    private SwiftView Grid(NativeMatchingState state, SourceUiStyle style,
        Func<string, string> text)
    {
        var appearance = style.Appearance;
        var tiles = state.Tiles.IsDefault ? [] : state.Tiles;
        var grid = Views.LazyVGrid(Columns(style.ViewportWidth), tiles.Select(tile =>
        {
            if (!tile.IsVisible && !tile.IsMatched)
                // Hidden native tile keeps its grid cell but exposes no text.
                return Views.VStack()
                    .WithMinimumHeight(56)
                    .WithFlexibleWidth();
            var foreground = tile.IsSelected ? appearance.OnPrimary
                : tile.IsMatched ? appearance.SecondaryText : appearance.Text;
            var fill = tile.IsSelected ? appearance.Primary : appearance.BodyBackground;
            var border = tile.IsSelected ? appearance.Primary
                : tile.IsTarget && !tile.IsMatched ? appearance.InfoAlertText
                    : appearance.Border;
            var label = style.Text(Views.Text(tile.Text), SourceTextRole.Body1,
                    strongElement: tile.IsTarget)
                .WithForegroundColor(foreground.Light, foreground.Dark)
                .WithPadding(6)
                .WithFlexibleWidth(alignment: SwiftHorizontalAlignment.Center)
                .WithMinimumHeight(56)
                .WithRoundedSurface(fill.Light, fill.Dark, appearance.BootstrapRadius,
                    border.Light, border.Dark, 2);
            var button = Views.Button(label, () => Fire(token =>
                    _presenter.TapAsync(tile.Id, token)))
                .WithButtonStyle(SwiftButtonStyle.Plain)
                .WithAccessibilityLabel(tile.IsMatched
                    ? $"{tile.Text}, {text(NativeMatchingText.Matched)}" : tile.Text)
                .WithAccessibilitySelected(tile.IsSelected)
                .WithAccessibilityIdentifier(tile.AccessibilityId)
                .WithOpacity(tile.IsMatched ? 0.3 : 1);
            button.IsEnabled = !tile.IsMatched && !state.IsChecking;
            return button;
        }).ToArray()).WithAccessibilityIdentifier(NativeMatchingAccess.Grid);
        grid.Spacing = style.ViewportWidth <= 768 ? 4 : SourceUiLayout.ItemGap;
        return grid;
    }

    private SwiftView Complete(NativeMatchingState state, SourceUiStyle style,
        Func<string, string> text)
    {
        var stats = Views.HStack(
            Stat(state.MatchedPairs, NativeMatchingText.Matched, SourceTextTone.Success,
                NativeMatchingAccess.CompleteMatched, style, text),
            Stat(state.Misses, NativeMatchingText.Misses, SourceTextTone.Danger,
                NativeMatchingAccess.CompleteMisses, style, text));
        stats.Spacing = 32;
        var card = Views.VStack(
                Views.HStack(
                    Views.Image("trophy.fill").WithForegroundColor(
                        style.Appearance.Warning.Light, style.Appearance.Warning.Dark),
                    style.Text(Views.Text(text(NativeMatchingText.Congratulations)),
                        SourceTextRole.Title1, sourceHeading: true)),
                style.Text(Views.Text(text(NativeMatchingText.AllPairsMatched)),
                    SourceTextRole.Body1, SourceTextTone.Secondary),
                style.Card(stats.WithPadding(SourceUiLayout.CardInset), maxWidth: 320),
                style.PrimaryButton(text(NativeMatchingText.PlayAgain),
                        () => Fire(_presenter.RestartAsync))
                    .WithAccessibilityIdentifier(NativeMatchingAccess.PlayAgain))
            .WithFlexibleWidth(alignment: SwiftHorizontalAlignment.Center)
            .WithAccessibilityIdentifier(NativeMatchingAccess.Complete);
        card.Spacing = SourceUiLayout.PageGap;
        return card;
    }

    private static SwiftView Stat(int value, string labelKey, SourceTextTone tone,
        string id, SourceUiStyle style, Func<string, string> text)
    {
        var stat = Views.VStack(
                style.Text(Views.Text(value.ToString(CultureInfo.CurrentCulture)),
                    SourceTextRole.Title2, tone),
                style.Text(Views.Text(text(labelKey)), SourceTextRole.Small,
                    SourceTextTone.Secondary))
            .WithAccessibilityLabel($"{text(labelKey)} {value}")
            .WithAccessibilityIdentifier(id);
        return stat;
    }

    public void Dispose()
    {
        _presenter.StateChanged -= OnStateChanged;
        _presenter.Dispose();
    }
}
