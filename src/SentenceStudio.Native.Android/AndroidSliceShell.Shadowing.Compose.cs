using AndroidX.Compose;
using Microsoft.Extensions.DependencyInjection;
using SentenceStudio.Native;
using ComposeButton = AndroidX.Compose.Button;

namespace SentenceStudio.Native.Android;

/// <summary>
/// Compose Shadowing (source Shadowing.razor; also plan type Listening): the
/// sentence card with the translation link and pronunciation notes, the
/// seeded placeholder waveform with elapsed/total time, and the footer
/// (Prev, Slow, Play/Pause, Normal, Next, n / total).
/// </summary>
internal sealed partial class AndroidSliceShell
{
    private NativeShadowingPresenter? _shadowing;

    internal NativeShadowingState Shadowing { get; private set; } = new(NativePhase.Loading);

    private async Task OpenShadowingAsync(NativeActivityLaunch launch, CancellationToken cancellationToken)
    {
        if (_scope is null)
            return;
        var presenter = _scope.ServiceProvider.GetRequiredService<NativeShadowingPresenter>();
        presenter.StateChanged += OnShadowingChanged;
        _shadowing = presenter;
        Publish(() => Shadowing = new(NativePhase.Loading));
        var state = await presenter.OpenAsync(launch, cancellationToken);
        Publish(() => Shadowing = state);
    }

    private async Task ExitShadowingAsync(CancellationToken cancellationToken)
    {
        if (_shadowing is { } presenter)
            await presenter.ExitAsync(cancellationToken);
    }

    private void ReleaseShadowing()
    {
        if (_shadowing is { } presenter)
        {
            presenter.StateChanged -= OnShadowingChanged;
            presenter.Dispose();
        }
        _shadowing = null;
    }

    private void OnShadowingChanged(NativeShadowingState state) =>
        Publish(() => Shadowing = state);

    /// <summary>Playback commands bypass the shell's busy gate so Back stays available while audio buffers.</summary>
    private void ShadowingCommand(
        Func<NativeShadowingPresenter, CancellationToken, Task<NativeShadowingState>> command)
    {
        if (_shadowing is not { } presenter)
            return;
        _ = RunShadowingAsync(presenter, command);
    }

    private async Task RunShadowingAsync(NativeShadowingPresenter presenter,
        Func<NativeShadowingPresenter, CancellationToken, Task<NativeShadowingState>> command)
    {
        try
        {
            var state = await command(presenter, _lifetime.Token);
            Publish(() =>
            {
                if (ReferenceEquals(_shadowing, presenter))
                    Shadowing = state;
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

    private ComposableNode RenderShadowing(AndroidVisualPalette palette)
    {
        var state = Shadowing;
        var viewport = new Column(Arrangement.SpacedBy(12))
        {
            Modifier = Modifier.FillMaxWidth().Weight(1f)
                .VerticalScroll(_quizScroll)
                .Padding(horizontal: AndroidVisualTheme.PagePadding, vertical: 16)
                .TestTag(NativeShadowingAccess.Screen),
        };
        var ready = state.Phase == NativePhase.Ready && state.Current is not null;
        switch (state.Phase)
        {
            case NativePhase.Idle:
            case NativePhase.Loading:
                viewport.Add(new CircularProgressIndicator
                {
                    Modifier = Modifier.Size(28).TestTag(NativeShadowingAccess.Loading)
                });
                viewport.Add(Body(Localize(NativeShadowingText.Generating), palette, secondary: true));
                break;
            case NativePhase.Ready when state.Current is { } sentence:
                if (state.ErrorKey is { } error)
                    viewport.Add(StatusAlert(Localize(error), palette, error: true,
                        automationId: NativeShadowingAccess.Error));
                viewport.Add(ShadowingCard(sentence, state, palette));
                if (state.ShowsWaveform)
                    viewport.Add(ShadowingWaveform(state, palette));
                if (state.AudioIssue != NativeShadowingAudioIssue.None)
                    viewport.Add(StatusAlert(Localize(
                            state.AudioIssue == NativeShadowingAudioIssue.Offline
                                ? NativeShadowingText.AudioOffline : NativeShadowingText.AudioFailed),
                        palette, error: true, automationId: NativeShadowingAccess.AudioStatus));
                break;
            default:
                var empty = state.Phase == NativePhase.Empty;
                viewport.Add(StatusAlert(
                    Localize(state.ErrorKey ?? NativeShadowingText.NoSentences), palette,
                    error: !empty,
                    automationId: empty ? NativeShadowingAccess.Empty : NativeShadowingAccess.Error));
                viewport.Add(Action(Localize(NativeShadowingText.GoBack),
                    NativeShadowingAccess.EmptyBack, ExitActivity, !Busy.Value, primary: false));
                break;
        }

        var page = new Column
        {
            Modifier.FillMaxSize().TestTag("native.shadowing.page"),
            ScreenHeader(Localize(NativeShadowingText.Title), palette,
                back: true, backAutomationId: NativeShadowingAccess.Back),
            viewport,
        };
        if (ready)
            page.Add(ShadowingFooter(state, palette));
        return page;
    }

    private ComposableNode ShadowingCard(NativeShadowingSentence sentence,
        NativeShadowingState state, AndroidVisualPalette palette)
    {
        var children = new List<ComposableNode>
        {
            new Text(sentence.TargetText)
            {
                Modifier = Modifier.FillMaxWidth().TestTag(NativeShadowingAccess.Target),
                Color = palette.Text,
                FontSize = new Sp(30),
                FontWeight = FontWeight.SemiBold,
                Align = TextAlign.Center,
            }
        };
        if (state.ShowTranslation && sentence.NativeText is { } translation)
            children.Add(new Text(translation)
            {
                Modifier = Modifier.FillMaxWidth().TestTag(NativeShadowingAccess.Translation),
                Color = palette.SecondaryText,
                FontSize = new Sp(16),
                Align = TextAlign.Center,
            });
        if (!string.IsNullOrWhiteSpace(sentence.PronunciationNotes))
            children.Add(new Text(sentence.PronunciationNotes)
            {
                Modifier = Modifier.FillMaxWidth().TestTag(NativeShadowingAccess.Pronunciation),
                Color = palette.SecondaryText,
                FontSize = new Sp(13),
                Align = TextAlign.Center,
            });
        if (sentence.HasTranslation)
        {
            var label = $"{Localize(state.ShowTranslation ? NativeShadowingText.Hide : NativeShadowingText.Show)} {Localize(NativeShadowingText.Translation)}";
            var link = new TextButton(() => ShadowingCommand(
                (presenter, token) => presenter.ToggleTranslationAsync(token)))
            {
                Modifier = Modifier.TestTag(NativeShadowingAccess.TranslationToggle)
                    .Semantics(semantics => semantics.ContentDescription(label)),
            };
            link.Add(new Text(label) { Color = palette.SecondaryText, FontSize = new Sp(13) });
            // Centered without a scope-specific align modifier (no new R8 keep rule).
            children.Add(new Row(Arrangement.Center) { Modifier.FillMaxWidth(), link });
        }
        return Card(palette, NativeShadowingAccess.Card, [.. children]);
    }

    private static ComposableNode ShadowingWaveform(NativeShadowingState state,
        AndroidVisualPalette palette)
    {
        var bars = new Row(Arrangement.SpacedBy(2), Alignment.Vertical.CenterVertically)
        {
            Modifier.FillMaxWidth().Height(60).Padding(horizontal: 4),
        };
        for (var i = 0; i < NativeShadowingWaveform.BarCount; i++)
            bars.Add(new Box
            {
                Modifier = Modifier.Weight(1f)
                    .Height((int)Math.Max(2, NativeShadowingWaveform.Bars[i] * 60))
                    .Clip(1)
                    .Background(NativeShadowingWaveform.IsPlayed(i, state.Position)
                        ? palette.Accent : palette.Border)
            });
        var elapsed = NativeShadowingWaveform.FormatTime(state.Position * state.DurationSeconds);
        var total = NativeShadowingWaveform.FormatTime(state.DurationSeconds);
        var surface = new Surface
        {
            Modifier = Modifier.FillMaxWidth().TestTag(NativeShadowingAccess.Waveform)
                .Semantics(semantics => semantics.ContentDescription($"{elapsed} / {total}")),
            Shape = Shape.RoundedCorners(AndroidVisualTheme.ControlRadius),
            Color = palette.Elevated,
        };
        surface.Add(new Column(Arrangement.SpacedBy(4))
        {
            Modifier.FillMaxWidth().Padding(12),
            bars,
            new Row(Arrangement.SpaceBetween, Alignment.Vertical.CenterVertically)
            {
                Modifier.FillMaxWidth(),
                new Text(elapsed)
                {
                    Modifier = Modifier.TestTag(NativeShadowingAccess.Elapsed),
                    Color = palette.SecondaryText,
                    FontSize = new Sp(AndroidVisualTheme.CaptionSize),
                },
                new Text(total)
                {
                    Modifier = Modifier.TestTag(NativeShadowingAccess.Duration),
                    Color = palette.SecondaryText,
                    FontSize = new Sp(AndroidVisualTheme.CaptionSize),
                },
            },
        });
        return surface;
    }

    private ComposableNode ShadowingFooter(NativeShadowingState state, AndroidVisualPalette palette)
    {
        ComposableNode Small(string label, string tag, bool primary, bool enabled, Action command)
        {
            var modifier = Modifier.Padding(horizontal: 3).HeightIn(min: AndroidVisualTheme.ControlHeight)
                .TestTag(tag)
                .Semantics(semantics =>
                {
                    semantics.ContentDescription(label);
                    semantics.Selected(primary);
                });
            ComposableNode button = primary
                ? new ComposeButton(onClick: command, enabled: enabled)
                {
                    Modifier = modifier,
                    Shape = Shape.RoundedCorners(AndroidVisualTheme.ControlRadius),
                }
                : new OutlinedButton(onClick: command, enabled: enabled)
                {
                    Modifier = modifier,
                    Shape = Shape.RoundedCorners(AndroidVisualTheme.ControlRadius),
                };
            ((ComposableContainer)button).Add(new Text(label) { MaxLines = 1 });
            return button;
        }

        var playLabel = Localize(state.IsPlaying ? NativeShadowingText.Pause : NativeShadowingText.Play);
        var play = new ComposeButton(
            onClick: () => ShadowingCommand((presenter, token) => presenter.TogglePlaybackAsync(token)),
            enabled: !state.IsBuffering)
        {
            Modifier = Modifier.Padding(horizontal: 3).HeightIn(min: AndroidVisualTheme.ControlHeight)
                .TestTag(NativeShadowingAccess.Play)
                .Semantics(semantics => semantics.ContentDescription(playLabel)),
            Shape = Shape.RoundedCorners(AndroidVisualTheme.ControlRadius),
        };
        if (state.IsBuffering)
            play.Add(new CircularProgressIndicator { Modifier = Modifier.Size(20) });
        else if (state.IsPlaying)
            // Material core icons have no Pause glyph: two bars.
            play.Add(new Row(Arrangement.SpacedBy(4), Alignment.Vertical.CenterVertically)
            {
                new Box { Modifier = Modifier.Width(5).Height(18).Background(palette.OnPrimary) },
                new Box { Modifier = Modifier.Width(5).Height(18).Background(palette.OnPrimary) },
            });
        else
            play.Add(new Icon(Icons.Default.PlayArrow, null)
            {
                Modifier = Modifier.Size(24),
                Tint = palette.OnPrimary,
            });

        var row = new Row(Arrangement.Center,
            Alignment.Vertical.CenterVertically)
        {
            Modifier.FillMaxWidth(),
            Small(Localize(NativeShadowingText.Previous), NativeShadowingAccess.Previous, false,
                state.CanGoPrevious, () => ShadowingCommand((p, t) => p.PreviousAsync(t))),
            Small(Localize(NativeShadowingText.Slow), NativeShadowingAccess.Slow,
                state.Speed == NativeShadowingSpeed.Slow, !state.IsBuffering,
                () => ShadowingCommand((p, t) => p.SetSpeedAsync(NativeShadowingSpeed.Slow, t))),
            play,
            Small(Localize(NativeShadowingText.Normal), NativeShadowingAccess.Normal,
                state.Speed == NativeShadowingSpeed.Normal, !state.IsBuffering,
                () => ShadowingCommand((p, t) => p.SetSpeedAsync(NativeShadowingSpeed.Normal, t))),
            Small(Localize(NativeShadowingText.Next), NativeShadowingAccess.Next, false,
                state.CanGoNext, () => ShadowingCommand((p, t) => p.NextAsync(t))),
        };
        return new Column(Arrangement.SpacedBy(6), Alignment.Horizontal.CenterHorizontally)
        {
            Modifier.FillMaxWidth().Background(palette.Background)
                .Padding(horizontal: AndroidVisualTheme.PagePadding, vertical: 10),
            row,
            new Text($"{state.Index + 1} / {state.Count}")
            {
                Modifier = Modifier.TestTag(NativeShadowingAccess.Counter),
                Color = palette.SecondaryText,
                FontSize = new Sp(AndroidVisualTheme.CaptionSize),
            },
        };
    }
}
