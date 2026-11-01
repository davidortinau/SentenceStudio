using CoreFoundation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.SwiftUI;
using SentenceStudio.Native.iOS.Styling;
using Views = Microsoft.SwiftUI.SwiftUI;

namespace SentenceStudio.Native.iOS.Features.Activities;

/// <summary>
/// SwiftUI Shadowing (source Shadowing.razor): the sentence card (target text,
/// optional translation, pronunciation notes, Show/Hide translation link), the
/// placeholder waveform with elapsed/total time once audio is prepared, and the
/// pinned footer: Prev, Slow, Play/Pause, Normal, Next and "n / total".
/// Playback ticks update the waveform and elapsed time in place rather than
/// rebuilding the screen ten times a second.
/// </summary>
internal sealed class ShadowingScreen : IActivityScreen
{
    private readonly ActivityScreenHost _host;
    private readonly NativeShadowingPresenter _presenter;
    private NativeShadowingState _state = new(NativePhase.Loading);
    private NativeShadowingState? _rendered;
    private SourceUiStyle? _style;
    private List<SwiftView>? _bars;
    private SwiftView? _elapsed;

    public ShadowingScreen(ActivityScreenHost host)
    {
        _host = host;
        _presenter = host.Services.GetRequiredService<NativeShadowingPresenter>();
        _presenter.StateChanged += OnStateChanged;
    }

    public string TitleKey => NativeShadowingText.Title;

    public bool HasExited => _state.Destination == NativeDestination.TodaysPlan;

    public async Task OpenAsync(NativeActivityLaunch launch, CancellationToken cancellationToken) =>
        _state = await _presenter.OpenAsync(launch, cancellationToken);

    public async Task ExitAsync(CancellationToken cancellationToken) =>
        _state = await _presenter.ExitAsync(cancellationToken);

    private void OnStateChanged(NativeShadowingState state)
    {
        _state = state;
        if (_rendered is { } rendered && _bars is not null
            && state with { Position = rendered.Position } == rendered)
        {
            DispatchQueue.MainQueue.DispatchAsync(() => UpdatePosition(state));
            return;
        }
        _host.Publish();
    }

    private void UpdatePosition(NativeShadowingState state)
    {
        if (_rendered is not { } rendered || _bars is not { } bars || _style is not { } style
            || state with { Position = rendered.Position } != rendered)
            return;
        try
        {
            var appearance = style.Appearance;
            for (var i = 0; i < bars.Count; i++)
            {
                var was = NativeShadowingWaveform.IsPlayed(i, rendered.Position);
                var now = NativeShadowingWaveform.IsPlayed(i, state.Position);
                if (was == now)
                    continue;
                var color = now ? appearance.Accent : appearance.TertiaryText;
                bars[i].WithRoundedSurface(color.Light, color.Dark, 1);
            }
            if (_elapsed is { } elapsed)
                elapsed.Text = NativeShadowingWaveform.FormatTime(
                    state.Position * state.DurationSeconds);
            _rendered = state;
        }
        catch (ObjectDisposedException)
        {
            // The screen was replaced or popped; the next render rebuilds it.
            _bars = null;
            _elapsed = null;
        }
    }

    private void Fire(Func<CancellationToken, Task<NativeShadowingState>> action) =>
        _ = FireAsync(action);

    private async Task FireAsync(Func<CancellationToken, Task<NativeShadowingState>> action)
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
            _host.Logger?.LogError(ex, "Native iOS Shadowing action failed.");
            _state = _state with { ErrorKey = NativeText.UnexpectedError };
        }
        _host.Publish();
    }

    public SwiftView Render(SourceUiStyle style)
    {
        var text = _host.Text;
        var state = _state;
        _style = style;
        _bars = null;
        _elapsed = null;
        _rendered = null;
        var items = new List<SwiftView>();
        switch (state.Phase)
        {
            case NativePhase.Idle:
            case NativePhase.Loading:
                items.Add(Views.IndeterminateProgress(text(NativeShadowingText.Generating))
                    .WithAccessibilityIdentifier(NativeShadowingAccess.Loading));
                items.Add(style.Text(Views.Text(text(NativeShadowingText.Generating)),
                    SourceTextRole.Body1, SourceTextTone.Secondary));
                break;
            case NativePhase.Ready when state.Current is { } sentence:
                if (state.ErrorKey is { } error)
                    items.Add(style.Alert(style.Text(Views.Text(text(error)), SourceTextRole.Body2),
                            success: false, compact: true)
                        .WithAccessibilityIdentifier(NativeShadowingAccess.Error));
                items.Add(SentenceCard(sentence, state, style, text));
                if (state.ShowsWaveform)
                    items.Add(Waveform(state, style));
                if (state.AudioIssue != NativeShadowingAudioIssue.None)
                    items.Add(style.Alert(style.Text(Views.Text(text(
                                state.AudioIssue == NativeShadowingAudioIssue.Offline
                                    ? NativeShadowingText.AudioOffline
                                    : NativeShadowingText.AudioFailed)), SourceTextRole.Body2),
                            success: false, compact: true)
                        .WithAccessibilityIdentifier(NativeShadowingAccess.AudioStatus));
                break;
            default:
                // Empty, Blocked and Error offer only Go Back (source empty state).
                var empty = state.Phase == NativePhase.Empty;
                items.Add(style.Text(Views.Text(text(state.ErrorKey ?? NativeShadowingText.NoSentences)),
                        SourceTextRole.Body1, empty ? SourceTextTone.Secondary : SourceTextTone.Danger)
                    .WithAccessibilityIdentifier(empty ? NativeShadowingAccess.Empty : NativeShadowingAccess.Error));
                items.Add(style.SecondaryButton(text(NativeShadowingText.GoBack), _host.Exit)
                    .WithAccessibilityIdentifier(NativeShadowingAccess.EmptyBack));
                break;
        }

        var gutter = SourceUiLayout.PageGutter(style.ViewportWidth);
        var content = Views.VStack(items.ToArray())
            .WithPadding(16, gutter, 16, gutter)
            .WithFlexibleWidth(alignment: state.Phase == NativePhase.Ready
                ? SwiftHorizontalAlignment.Leading : SwiftHorizontalAlignment.Center)
            .WithAccessibilityIdentifier("native.shadowing.content");
        content.Spacing = SourceUiLayout.PageGap;
        var scroll = Views.Scroll(content).WithAccessibilityIdentifier(NativeShadowingAccess.Screen);
        if (state.Phase != NativePhase.Ready || state.Current is null)
            return style.Page(scroll);
        _rendered = state;
        return style.Page(Views.BottomInset(style.Page(scroll), Footer(state, style, text)));
    }

    private SwiftView SentenceCard(NativeShadowingSentence sentence, NativeShadowingState state,
        SourceUiStyle style, Func<string, string> text)
    {
        var lines = new List<SwiftView>
        {
            style.Text(Views.Text(sentence.TargetText), SourceTextRole.Display)
                .WithAccessibilityIdentifier(NativeShadowingAccess.Target)
        };
        if (state.ShowTranslation && sentence.NativeText is { } translation)
            lines.Add(style.Text(Views.Text(translation), SourceTextRole.Body1, SourceTextTone.Secondary)
                .WithAccessibilityIdentifier(NativeShadowingAccess.Translation));
        if (!string.IsNullOrWhiteSpace(sentence.PronunciationNotes))
            lines.Add(style.Text(Views.Text(sentence.PronunciationNotes), SourceTextRole.Small,
                    SourceTextTone.Secondary)
                .WithAccessibilityIdentifier(NativeShadowingAccess.Pronunciation));
        if (sentence.HasTranslation)
        {
            var label = $"{text(state.ShowTranslation ? NativeShadowingText.Hide : NativeShadowingText.Show)} {text(NativeShadowingText.Translation)}";
            var link = Views.Button(style.Text(Views.Text(label), SourceTextRole.Small,
                        SourceTextTone.Secondary),
                    () => Fire(_presenter.ToggleTranslationAsync))
                .WithButtonStyle(SwiftButtonStyle.Plain)
                .WithMinimumHeight(SourceUiLayout.MinimumTouchHeight)
                .WithAccessibilityIdentifier(NativeShadowingAccess.TranslationToggle);
            lines.Add(link);
        }
        var card = Views.VStack(lines.ToArray())
            .WithPadding(24)
            .WithAccessibilityIdentifier(NativeShadowingAccess.Card);
        card.Spacing = 8;
        return style.Card(Views.VStack(card).WithFlexibleWidth(alignment: SwiftHorizontalAlignment.Center));
    }

    private SwiftView Waveform(NativeShadowingState state, SourceUiStyle style)
    {
        var appearance = style.Appearance;
        var bars = new List<SwiftView>(NativeShadowingWaveform.BarCount);
        for (var i = 0; i < NativeShadowingWaveform.BarCount; i++)
        {
            var color = NativeShadowingWaveform.IsPlayed(i, state.Position)
                ? appearance.Accent : appearance.TertiaryText;
            bars.Add(Views.VStack()
                .WithMinimumHeight(Math.Max(0.04, NativeShadowingWaveform.Bars[i]) * 60)
                .WithFlexibleWidth()
                .WithRoundedSurface(color.Light, color.Dark, 1));
        }
        var row = Views.HStack(bars.ToArray()).WithMinimumHeight(60).WithFlexibleWidth();
        row.Spacing = 2;
        var elapsed = style.Text(Views.Text(NativeShadowingWaveform.FormatTime(
                state.Position * state.DurationSeconds)), SourceTextRole.Caption2)
            .WithAccessibilityIdentifier(NativeShadowingAccess.Elapsed);
        var total = style.Text(Views.Text(NativeShadowingWaveform.FormatTime(state.DurationSeconds)),
                SourceTextRole.Caption2)
            .WithAccessibilityIdentifier(NativeShadowingAccess.Duration);
        var times = Views.HStack(elapsed, Views.VStack().WithFlexibleWidth(), total).WithFlexibleWidth();
        var waveform = Views.VStack(row, times)
            .WithPadding(12)
            .WithFlexibleWidth()
            .WithRoundedSurface(appearance.SecondarySurface.Light, appearance.SecondarySurface.Dark, 8)
            .WithAccessibilityIdentifier(NativeShadowingAccess.Waveform);
        waveform.Spacing = 4;
        _bars = bars;
        _elapsed = elapsed;
        return waveform;
    }

    private SwiftView Footer(NativeShadowingState state, SourceUiStyle style, Func<string, string> text)
    {
        var appearance = style.Appearance;
        var previous = style.SecondaryButton(text(NativeShadowingText.Previous),
                () => Fire(_presenter.PreviousAsync))
            .WithAccessibilityIdentifier(NativeShadowingAccess.Previous);
        SourceUiStyle.SetCustomButtonAvailability(previous, appearance,
            SourceButtonRole.FilledSecondary, state.CanGoPrevious);
        var next = style.SecondaryButton(text(NativeShadowingText.Next),
                () => Fire(_presenter.NextAsync))
            .WithAccessibilityIdentifier(NativeShadowingAccess.Next);
        SourceUiStyle.SetCustomButtonAvailability(next, appearance,
            SourceButtonRole.FilledSecondary, state.CanGoNext);

        SwiftView Speed(float speed, string key, string id)
        {
            var selected = state.Speed == speed;
            var button = selected
                ? style.PrimaryButton(text(key), () => Fire(token => _presenter.SetSpeedAsync(speed, token)))
                : style.SecondaryButton(text(key), () => Fire(token => _presenter.SetSpeedAsync(speed, token)));
            SourceUiStyle.SetCustomButtonAvailability(button, appearance,
                selected ? SourceButtonRole.Primary : SourceButtonRole.FilledSecondary,
                !state.IsBuffering);
            return button.WithAccessibilitySelected(selected).WithAccessibilityIdentifier(id);
        }

        var playName = text(state.IsPlaying ? NativeShadowingText.Pause : NativeShadowingText.Play);
        SwiftView playLabel = state.IsBuffering
            ? Views.IndeterminateProgress(text(NativeShadowingText.Generating))
            : Views.Image(state.IsPlaying ? "pause.fill" : "play.fill")
                .WithForegroundColor(appearance.OnPrimary.Light, appearance.OnPrimary.Dark);
        var play = Views.Button(playLabel
                    .WithPadding(10, 18, 10, 18)
                    .WithMinimumHeight(SourceUiLayout.MinimumTouchHeight),
                () => Fire(_presenter.TogglePlaybackAsync))
            .WithButtonStyle(SwiftButtonStyle.Plain)
            .WithButtonAppearance(appearance.Primary.Light, appearance.Primary.Dark,
                appearance.Primary.Light, appearance.Primary.Dark, appearance.BootstrapRadius)
            .WithAccessibilityLabel(playName)
            .WithAccessibilityIdentifier(NativeShadowingAccess.Play);
        play.IsEnabled = !state.IsBuffering;

        var center = Views.HStack(
            Speed(NativeShadowingSpeed.Slow, NativeShadowingText.Slow, NativeShadowingAccess.Slow),
            play,
            Speed(NativeShadowingSpeed.Normal, NativeShadowingText.Normal, NativeShadowingAccess.Normal));
        center.Spacing = 8;
        foreach (var button in new[] { previous, next, center.Children[0], center.Children[2] })
            Compact(button);
        // No flexible spacers: they would take width from the speed labels on a phone.
        var row = Views.HStack(previous, center, next)
            .WithFlexibleWidth(alignment: SwiftHorizontalAlignment.Center);
        row.Spacing = 8;
        var counter = style.Text(Views.Text($"{state.Index + 1} / {state.Count}"),
                SourceTextRole.Caption1, SourceTextTone.Secondary)
            .WithFlexibleWidth(alignment: SwiftHorizontalAlignment.Center)
            .WithAccessibilityIdentifier(NativeShadowingAccess.Counter);
        var gutter = SourceUiLayout.PageGutter(style.ViewportWidth);
        var footer = Views.VStack(row, counter)
            .WithPadding(12, gutter, 12, gutter)
            .WithFlexibleWidth(alignment: SwiftHorizontalAlignment.Center)
            .WithBackgroundColor(appearance.BodyBackground.Light, appearance.BodyBackground.Dark);
        footer.Spacing = 8;
        return footer;
    }

    /// <summary>Source footer uses .btn-sm: one-line labels with small horizontal padding.</summary>
    private static void Compact(SwiftView button)
    {
        var label = button.Children[0];
        label.WithPadding(8, 10, 8, 10);
        if (label.Kind == "text")
            label.WithLineLimit(1);
    }

    public void Dispose()
    {
        _presenter.StateChanged -= OnStateChanged;
        _presenter.Dispose();
    }
}
