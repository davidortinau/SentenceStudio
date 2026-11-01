using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.SwiftUI;
using SentenceStudio.Native.iOS.Styling;
using Views = Microsoft.SwiftUI.SwiftUI;

namespace SentenceStudio.Native.iOS.Features.Activities;

/// <summary>
/// SwiftUI Reading (source Reading.razor + InteractiveText.razor). The passage
/// flows word by word in a wrapping layout of plain SwiftUI buttons (the
/// bridge has no inline tappable text runs); the current sentence is tinted
/// like .sentence--selected and vocabulary words use the .word--vocab style.
/// The fixed footer keeps the source transport, counter, time, speed select
/// and (at 768 pt and wider, like d-md-inline-flex) the font-size slider. The
/// word lookup is a SwiftUI sheet hosted by the scene.
/// </summary>
internal sealed class ReadingScreen : IActivityScreen
{
    private static readonly char[] Whitespace = [' ', '\t', '\n', '\r', '\u00A0', '\u3000'];
    private readonly ActivityScreenHost _host;
    private readonly NativeReadingPresenter _presenter;
    private NativeReadingState _state = new(NativePhase.Loading);
    private NativeReadingNotice? _scheduledNotice;

    public ReadingScreen(ActivityScreenHost host)
    {
        _host = host;
        _presenter = host.Services.GetRequiredService<NativeReadingPresenter>();
        _presenter.StateChanged += OnStateChanged;
    }

    public string TitleKey => NativeReadingText.Title;

    public bool HasExited => _state.Destination == NativeDestination.TodaysPlan;

    public bool HasSheet => true;

    public bool IsSheetPresented => _state.WordPanel is not null
        && _state.Destination == NativeDestination.Activity
        && _state.Phase == NativePhase.Ready;

    public double SheetDetentFraction => 0.45;

    public async Task OpenAsync(NativeActivityLaunch launch, CancellationToken cancellationToken) =>
        _state = await _presenter.OpenAsync(launch, cancellationToken);

    public async Task ExitAsync(CancellationToken cancellationToken) =>
        _state = await _presenter.ExitAsync(cancellationToken);

    public void OnSheetDismissed() => Fire(_presenter.CloseWordPanelAsync);

    private void OnStateChanged(NativeReadingState state)
    {
        _state = state;
        ScheduleNoticeDismissal(state.Notice);
        _host.Publish();
    }

    /// <summary>Source toasts disappear on their own.</summary>
    private void ScheduleNoticeDismissal(NativeReadingNotice? notice)
    {
        if (notice is null || ReferenceEquals(notice, _scheduledNotice))
            return;
        _scheduledNotice = notice;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(4), _host.Lifetime);
                if (ReferenceEquals(_presenter.State.Notice, notice))
                    await _presenter.DismissNoticeAsync(_host.Lifetime);
            }
            catch (OperationCanceledException)
            {
            }
            catch (ObjectDisposedException)
            {
            }
        });
    }

    /// <summary>Commands do not take the controller's busy gate: a word lookup must not disable Back. The presenter serializes them.</summary>
    private void Fire(Func<CancellationToken, Task<NativeReadingState>> action) =>
        _ = FireAsync(action);

    private async Task FireAsync(Func<CancellationToken, Task<NativeReadingState>> action)
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
            _host.Logger?.LogError(ex, "Native iOS Reading action failed.");
            _state = _state with { Notice = new(NativeText.UnexpectedError, IsError: true) };
        }
        _host.Publish();
    }

    public SwiftView Render(SourceUiStyle style)
    {
        var text = _host.Text;
        var state = _state;
        var items = new List<SwiftView>();
        var gutter = SourceUiLayout.PageGutter(style.ViewportWidth);
        switch (state.Phase)
        {
            case NativePhase.Idle:
            case NativePhase.Loading:
                items.Add(Views.IndeterminateProgress(text(NativeReadingText.LoadingPassage))
                    .WithAccessibilityIdentifier(NativeReadingAccess.Loading));
                items.Add(style.Text(Views.Text(text(NativeReadingText.LoadingPassage)),
                    SourceTextRole.Body1, SourceTextTone.Secondary));
                break;
            case NativePhase.Ready:
                if (state.ResourceTitle is { Length: > 0 } title)
                    items.Add(style.Text(Views.Text(title), SourceTextRole.Title2, sourceHeading: true)
                        .WithAccessibilityIdentifier(NativeReadingAccess.Title));
                if (Notice(state, style, text) is { } notice)
                    items.Add(notice);
                if (AudioStatus(state, style, text) is { } audio)
                    items.Add(audio);
                items.Add(Passage(state, style, text));
                break;
            default:
                // Error, Empty and Blocked offer only Go Back (source error/empty states).
                var empty = state.Phase == NativePhase.Empty;
                var warning = Views.Image("exclamationmark.triangle")
                    .WithForegroundColor(style.Appearance.Warning.Light, style.Appearance.Warning.Dark);
                if (!empty)
                    items.Add(warning);
                items.Add(style.Text(Views.Text(text(state.ErrorKey ?? NativeReadingText.NoContent)),
                        SourceTextRole.Body1, SourceTextTone.Secondary)
                    .WithAccessibilityIdentifier(empty ? NativeReadingAccess.Empty : NativeReadingAccess.Error));
                items.Add(style.SecondaryButton(text(NativeReadingText.GoBack), _host.Exit)
                    .WithAccessibilityIdentifier(NativeReadingAccess.GoBack));
                break;
        }

        var content = Views.VStack(items.ToArray())
            .WithPadding(16, gutter, 16, gutter)
            .WithFlexibleWidth()
            .WithAccessibilityIdentifier("native.reading.content");
        content.Spacing = SourceUiLayout.PageGap;
        var scroll = Views.Scroll(content).WithAccessibilityIdentifier(NativeReadingAccess.Screen);
        // Source: the current sentence scrolls into view (block: center). Each
        // publish mounts a new scroll, so this also keeps the reader's place.
        if (state.Phase == NativePhase.Ready && state.CurrentSentenceAnchorId is { } anchor)
            scroll.ScrollTo(anchor, SwiftScrollAnchor.Center, animated: true);
        if (state.Phase != NativePhase.Ready || state.SentenceCount == 0)
            return style.Page(scroll);
        return style.Page(Views.BottomInset(scroll, Footer(state, style, text)));
    }

    private SwiftView? Notice(NativeReadingState state, SourceUiStyle style, Func<string, string> text)
    {
        if (state.Notice is not { } notice)
            return null;
        var message = notice.Argument is null ? text(notice.Key)
            : string.Format(CultureInfo.CurrentCulture, text(notice.Key), notice.Argument);
        return style.Alert(style.Text(Views.Text(message), SourceTextRole.Body2), !notice.IsError, compact: true)
            .WithAccessibilityIdentifier(NativeReadingAccess.Notice);
    }

    private static SwiftView? AudioStatus(NativeReadingState state, SourceUiStyle style, Func<string, string> text)
    {
        if (state.AudioStatusKey is not { } key)
            return null;
        var message = state.AudioStatusDetail is { } detail
            ? string.Format(CultureInfo.CurrentCulture, text(key), detail) : text(key);
        if (!state.IsGeneratingAudio)
            return style.Text(Views.Text(message), SourceTextRole.Body2, SourceTextTone.Secondary)
                .WithAccessibilityIdentifier(NativeReadingAccess.AudioStatus);
        // Source .audio-gen-banner: spinner, status and a 4 px progress bar.
        var rows = new List<SwiftView>
        {
            style.Text(Views.Text(message), SourceTextRole.Body2)
                .WithAccessibilityIdentifier(NativeReadingAccess.AudioStatus)
        };
        if (state.AudioProgress > 0)
            rows.Add(style.Progress(Views.Progress(state.AudioProgress, 1, 4, message), completion: false));
        var column = Views.VStack(rows.ToArray()).WithFlexibleWidth();
        column.Spacing = 4;
        var banner = Views.HStack(Views.IndeterminateProgress(message), column);
        banner.Spacing = 12;
        var appearance = style.Appearance;
        var fill = Mix(appearance.Primary, appearance.BodyBackground, 0.1);
        var border = Mix(appearance.Primary, appearance.BodyBackground, 0.3);
        return banner.WithPadding(12, 16, 12, 16).WithFlexibleWidth()
            .WithRoundedSurface(fill.Light, fill.Dark, appearance.BootstrapRadius, border.Light, border.Dark, 1)
            .WithAccessibilityIdentifier(NativeReadingAccess.AudioBanner);
    }

    /// <summary>
    /// InteractiveText: every token in reading order. Each word button carries
    /// the punctuation that follows it and a trailing gap for a following
    /// space, so the wrapping layout reads like running text.
    /// </summary>
    private SwiftView Passage(NativeReadingState state, SourceUiStyle style, Func<string, string> text)
    {
        var appearance = style.Appearance;
        var size = (double)state.FontSize;
        var selected = Mix(appearance.Primary, appearance.BodyBackground, 0.1);
        // VoiceOver speaks passage words in the passage language, not the UI language.
        var speech = state.PassageLanguageTag;
        var jumpAction = text(NativeReadingText.PlayFromSentence);
        var buttons = new List<SwiftView>();
        var pendingLead = string.Empty;
        var chunks = new List<(NativeReadingToken? Word, string Text, bool Space)>();
        foreach (var sentence in state.Sentences)
        {
            foreach (var token in sentence.Tokens)
            {
                if (token.IsWord)
                {
                    chunks.Add((token, pendingLead + token.Text, false));
                    pendingLead = string.Empty;
                    continue;
                }
                var rest = token.Text;
                if (chunks.Count > 0 && !chunks[^1].Space && pendingLead.Length == 0
                    && !char.IsWhiteSpace(rest[0]))
                {
                    // Punctuation directly after a word joins it.
                    var split = rest.IndexOfAny(Whitespace);
                    var attached = split < 0 ? rest : rest[..split];
                    chunks[^1] = (chunks[^1].Word, chunks[^1].Text + attached, false);
                    rest = split < 0 ? string.Empty : rest[split..];
                }
                if (rest.IndexOfAny(Whitespace) >= 0 && chunks.Count > 0 && pendingLead.Length == 0)
                    chunks[^1] = (chunks[^1].Word, chunks[^1].Text, true);
                // Punctuation before a word (an opening quote) leads it.
                pendingLead += string.Concat(rest.Where(character => !char.IsWhiteSpace(character)));
            }
            // Source .sentence { margin-right: 0.25em }.
            if (chunks.Count > 0)
                chunks[^1] = (chunks[^1].Word, chunks[^1].Text, true);
        }
        if (pendingLead.Length > 0)
            chunks.Add((null, pendingLead, false));

        foreach (var (word, label, space) in chunks)
        {
            var current = word is not null && word.SentenceIndex == state.CurrentSentenceIndex;
            var labelView = Views.Text(label)
                .WithTypography(size, word?.IsVocabulary == true ? 600 : 400, 1.0,
                    appearance.FontPostScriptName(word?.IsVocabulary == true ? 600 : 400, false, false))
                .WithPadding(size * 0.25, 2, size * 0.25, 2)
                .WithSpeechLanguage(word is null ? null : speech);
            if (word?.IsVocabulary == true)
                labelView.WithForegroundColor(appearance.Primary.Light, appearance.Primary.Dark)
                    .WithUnderline();
            else
                labelView.WithForegroundColor(appearance.Text.Light, appearance.Text.Dark);
            // .sentence--selected tints the whole sentence, gaps included.
            var wrapper = Views.HStack(labelView).WithPadding(0, space ? size * 0.15 : 0, 0, 0);
            if (current)
                wrapper.WithRoundedSurface(selected.Light, selected.Dark, 3);
            if (word is null)
            {
                var plain = Views.Button(wrapper, () => { }).WithButtonStyle(SwiftButtonStyle.Plain);
                plain.IsEnabled = false;
                buttons.Add(plain);
                continue;
            }
            var tokenId = word.Id;
            var sentenceIndex = word.SentenceIndex;
            var accessibility = word.IsVocabulary
                ? $"{word.Text}, {text(NativeReadingText.InYourVocab)}" : word.Text;
            var button = Views.Button(wrapper, () => Fire(token => _presenter.TapWordAsync(tokenId, token)))
                .WithButtonStyle(SwiftButtonStyle.Plain)
                .WithAccessibilityLabel(accessibility)
                .WithSpeechLanguage(speech)
                .WithAccessibilitySelected(current)
                .WithAccessibilityIdentifier(word.AccessibilityId);
            // Source InteractiveText: double-clicking a sentence plays from it (needs loaded audio).
            if (state.CanNavigate)
                button.OnDoubleTap(() => Fire(token => _presenter.JumpToSentenceAsync(sentenceIndex, token)),
                    jumpAction);
            buttons.Add(button);
        }
        if (buttons.Count == 0)
            return style.Text(Views.Text(text(NativeReadingText.NoContent)), SourceTextRole.Body1);
        return Views.ButtonGroup(buttons.ToArray(), wraps: true)
            .WithButtonGroupLayout(0)
            .WithAccessibilityIdentifier(NativeReadingAccess.Passage);
    }

    private SwiftView Footer(NativeReadingState state, SourceUiStyle style, Func<string, string> text)
    {
        var appearance = style.Appearance;
        SwiftView Icon(string symbol, string labelKey, string id, Action action, bool enabled)
        {
            var button = Views.Button(Views.Image(symbol).WithPadding(6), action)
                .WithButtonStyle(SwiftButtonStyle.Plain)
                .WithAccessibilityLabel(text(labelKey))
                .WithAccessibilityIdentifier(id);
            button.IsEnabled = enabled;
            return button;
        }
        var previous = Icon("backward.end.fill", NativeReadingText.PreviousSentence,
            NativeReadingAccess.Previous, () => Fire(_presenter.PreviousSentenceAsync), state.CanNavigate);
        var playLabel = state.IsPlaying ? NativeReadingText.Pause : NativeReadingText.Play;
        var play = Views.Button(Views.Image(state.IsPlaying ? "pause.fill" : "play.fill")
                    .WithForegroundColor(appearance.OnPrimary.Light, appearance.OnPrimary.Dark)
                    .WithPadding(6, 10, 6, 10),
                () => Fire(_presenter.TogglePlaybackAsync))
            .WithButtonAppearance(appearance.Primary.Light, appearance.Primary.Dark,
                null, null, appearance.BootstrapRadius)
            .WithAccessibilityLabel(text(playLabel))
            .WithAccessibilityIdentifier(NativeReadingAccess.PlayPause);
        play.IsEnabled = state.CanTogglePlayback;
        var next = Icon("forward.end.fill", NativeReadingText.NextSentence,
            NativeReadingAccess.Next, () => Fire(_presenter.NextSentenceAsync), state.CanNavigate);
        var counter = style.Text(Views.Text(state.SentenceCounter), SourceTextRole.Caption1, SourceTextTone.Secondary)
            .WithLineLimit(1)
            .WithAccessibilityIdentifier(NativeReadingAccess.SentenceCounter);
        var time = state.IsAudioLoaded
            ? style.Text(Views.Text(state.TimeDisplay), SourceTextRole.Caption1, SourceTextTone.Secondary)
                .WithLineLimit(1)
                .WithFlexibleWidth(alignment: SwiftHorizontalAlignment.Trailing)
                .WithAccessibilityIdentifier(NativeReadingAccess.Time)
            : Views.Text(string.Empty).WithFlexibleWidth();
        var speeds = NativeReadingPresenter.PlaybackSpeeds
            .Select(speed => new SwiftPickerOption(
                speed.ToString("0.0#", CultureInfo.InvariantCulture),
                speed.ToString("0.0#", CultureInfo.CurrentCulture) + "×"))
            .ToArray();
        var selectedSpeed = NativeReadingPresenter.PlaybackSpeeds.Contains(state.PlaybackSpeed)
            ? state.PlaybackSpeed : 1.0f;
        var speed = Views.Picker(text(NativeReadingText.PlaybackSpeed), speeds,
                selectedSpeed.ToString("0.0#", CultureInfo.InvariantCulture),
                value =>
                {
                    if (float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
                        Fire(token => _presenter.SetPlaybackSpeedAsync(parsed, token));
                })
            .WithPickerStyle(SwiftPickerStyle.Menu)
            .WithAccessibilityIdentifier(NativeReadingAccess.Speed);
        var transport = new List<SwiftView>
        {
            previous, play, next, counter, time, speed
        };
        // Source: the font slider is d-none d-md-inline-flex (768 px and wider).
        if (style.ViewportWidth >= 768)
        {
            var slider = Views.Slider(text(NativeReadingText.FontSize),
                NativeReadingPresenter.MinimumFontSize, NativeReadingPresenter.MaximumFontSize,
                state.FontSize,
                value => Fire(token => _presenter.SetFontSizeAsync((int)Math.Round(value), token)))
                .WithAccessibilityIdentifier(NativeReadingAccess.FontSize);
            var font = Views.HStack(
                Views.Image("textformat.size")
                    .WithForegroundColor(appearance.SecondaryText.Light, appearance.SecondaryText.Dark),
                Views.VStack(slider).WithFlexibleWidth(96));
            font.Spacing = 4;
            transport.Add(font);
        }
        var row = Views.HStack(transport.ToArray()).WithFlexibleWidth();
        row.Spacing = 4;
        var gutter = SourceUiLayout.PageGutter(style.ViewportWidth);
        var tertiary = appearance.SecondarySurface;
        return Views.VStack(Views.Divider(), row.WithPadding(8, gutter, 8, gutter))
            .WithFlexibleWidth()
            .WithBackgroundColor(tertiary.Light, tertiary.Dark)
            .WithAccessibilityIdentifier(NativeReadingAccess.Footer);
    }

    /// <summary>Source offcanvas-bottom: word, definition or spinner, "In your vocab" or "Remember this word".</summary>
    public SwiftView RenderSheet(SourceUiStyle style)
    {
        var text = _host.Text;
        var appearance = style.Appearance;
        if (_state.WordPanel is not { } panel)
            return Views.VStack();
        var close = Views.Button(Views.Image("xmark").WithPadding(8), () => Fire(_presenter.CloseWordPanelAsync))
            .WithButtonStyle(SwiftButtonStyle.Plain)
            .WithAccessibilityLabel(text(NativeReadingText.Close))
            .WithAccessibilityIdentifier(NativeReadingAccess.WordPanelClose);
        var header = Views.HStack(
            style.Text(Views.Text(panel.Word), SourceTextRole.Title2, SourceTextTone.Primary, sourceHeading: true)
                .WithAccessibilityIdentifier(NativeReadingAccess.WordPanelWord),
            Views.VStack().WithFlexibleWidth(),
            close);
        var rows = new List<SwiftView> { header };
        if (panel.IsTranslating)
        {
            var looking = Views.HStack(
                Views.IndeterminateProgress(text(NativeReadingText.LookingUp)),
                style.Text(Views.Text(text(NativeReadingText.LookingUp)), SourceTextRole.Body2, SourceTextTone.Secondary))
                .WithAccessibilityIdentifier(NativeReadingAccess.WordPanelLookingUp);
            looking.Spacing = 8;
            rows.Add(looking);
        }
        else if (!string.IsNullOrEmpty(panel.Definition))
        {
            var definition = panel.Definition == NativeReadingPresenter.TranslationUnavailableMarker
                ? text(NativeReadingText.TranslationUnavailable) : panel.Definition;
            rows.Add(style.Text(Views.Text(definition), SourceTextRole.Body1)
                .WithAccessibilityIdentifier(NativeReadingAccess.WordPanelDefinition));
        }
        if (panel.IsInVocabulary)
        {
            var badge = Views.HStack(
                    Views.Image("checkmark.circle").WithForegroundColor("#FFFFFF", "#FFFFFF"),
                    style.Text(Views.Text(text(NativeReadingText.InYourVocab)), SourceTextRole.Badge)
                        .WithForegroundColor("#FFFFFF", "#FFFFFF"))
                .WithPadding(4, 8, 4, 8)
                .WithRoundedSurface(appearance.Success.Light, appearance.Success.Dark, appearance.BootstrapRadius)
                .WithAccessibilityLabel(text(NativeReadingText.InYourVocab))
                .WithAccessibilityIdentifier(NativeReadingAccess.WordPanelInVocabulary);
            badge.Spacing = 4;
            rows.Add(badge);
        }
        else if (panel.IsSaving)
        {
            var saving = Views.HStack(
                Views.IndeterminateProgress(text(NativeReadingText.Saving)),
                style.Text(Views.Text(text(NativeReadingText.Saving)), SourceTextRole.Body2))
                .WithAccessibilityIdentifier(NativeReadingAccess.WordPanelSaving);
            saving.Spacing = 8;
            rows.Add(saving);
        }
        else if (panel.CanSave)
            rows.Add(style.PrimaryButton(text(NativeReadingText.RememberThisWord),
                    () => Fire(_presenter.SaveWordAsync))
                .WithAccessibilityIdentifier(NativeReadingAccess.WordPanelSave));
        var gutter = SourceUiLayout.PageGutter(style.ViewportWidth);
        var column = Views.VStack(rows.ToArray())
            .WithPadding(16, gutter, 16, gutter)
            .WithFlexibleWidth()
            .WithAccessibilityIdentifier(NativeReadingAccess.WordPanel);
        column.Spacing = 8;
        return style.Page(Views.Scroll(column));
    }

    private static SourceColorPair Mix(SourceColorPair top, SourceColorPair bottom, double alpha) =>
        new(MixHex(top.Light, bottom.Light, alpha), MixHex(top.Dark, bottom.Dark, alpha));

    private static string MixHex(string top, string bottom, double alpha)
    {
        static int Channel(string hex, int index) =>
            int.Parse(hex.AsSpan(1 + index * 2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        var channels = Enumerable.Range(0, 3).Select(index =>
            (int)Math.Round(Channel(top, index) * alpha + Channel(bottom, index) * (1 - alpha)));
        return "#" + string.Concat(channels.Select(value => value.ToString("X2", CultureInfo.InvariantCulture)));
    }

    public void Dispose()
    {
        _presenter.StateChanged -= OnStateChanged;
        _presenter.Dispose();
    }
}
