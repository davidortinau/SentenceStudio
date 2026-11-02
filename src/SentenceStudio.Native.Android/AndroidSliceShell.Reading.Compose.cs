using System.Globalization;
using AndroidX.Compose;
using Microsoft.Extensions.DependencyInjection;
using SentenceStudio.Native;

namespace SentenceStudio.Native.Android;

/// <summary>
/// Compose Reading (source Reading.razor + InteractiveText.razor): the passage
/// as a FlowRow of clickable word Texts (tinted while their sentence is
/// current, vocabulary words in the primary color, semi-bold and underlined),
/// the source footer transport, counter, time, speed menu and (wider than
/// 768 dp, like d-md-inline-flex) the font-size slider, and the word lookup
/// as a Material 3 ModalBottomSheet.
/// </summary>
internal sealed partial class AndroidSliceShell
{
    private NativeReadingPresenter? _reading;
    private bool _readingSpeedMenuOpen;
    private NativeReadingNotice? _readingScheduledNotice;

    internal NativeReadingState Reading { get; private set; } = new(NativePhase.Loading);

    private async Task OpenReadingAsync(NativeActivityLaunch launch, CancellationToken cancellationToken)
    {
        if (ActivityServices is not { } services)
            return;
        var presenter = services.GetRequiredService<NativeReadingPresenter>();
        presenter.StateChanged += OnReadingChanged;
        _reading = presenter;
        Publish(() => Reading = new(NativePhase.Loading));
        var state = await presenter.OpenAsync(launch, cancellationToken);
        Publish(() =>
        {
            if (ReferenceEquals(_reading, presenter))
                Reading = state;
        });
    }

    private async Task ExitReadingAsync(CancellationToken cancellationToken)
    {
        if (_reading is { } presenter)
            await presenter.ExitAsync(cancellationToken);
    }

    private void ReleaseReading()
    {
        if (_reading is { } presenter)
        {
            presenter.StateChanged -= OnReadingChanged;
            presenter.Dispose();
        }
        _reading = null;
        _readingSpeedMenuOpen = false;
        _readingScheduledNotice = null;
    }

    private void OnReadingChanged(NativeReadingState state)
    {
        Publish(() => Reading = state);
        if (state.Notice is { } notice && !ReferenceEquals(notice, _readingScheduledNotice)
            && _reading is { } presenter)
        {
            // Source toasts disappear on their own.
            _readingScheduledNotice = notice;
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(4), _lifetime.Token);
                    if (ReferenceEquals(presenter.State.Notice, notice))
                        await presenter.DismissNoticeAsync(_lifetime.Token);
                }
                catch (OperationCanceledException)
                {
                }
                catch (ObjectDisposedException)
                {
                }
            });
        }
    }

    /// <summary>Reading commands bypass the shell's busy gate so a word lookup does not disable Back; the presenter serializes them.</summary>
    private void ReadingCommand(Func<NativeReadingPresenter, CancellationToken, Task<NativeReadingState>> command)
    {
        if (_reading is not { } presenter)
            return;
        _ = RunReadingAsync(presenter, command);
    }

    private async Task RunReadingAsync(NativeReadingPresenter presenter,
        Func<NativeReadingPresenter, CancellationToken, Task<NativeReadingState>> command)
    {
        try
        {
            var state = await command(presenter, _lifetime.Token);
            Publish(() =>
            {
                if (ReferenceEquals(_reading, presenter))
                    Reading = state;
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

    private ComposableNode RenderReading(AndroidVisualPalette palette)
    {
        var state = Reading;
        var viewport = new Column(Arrangement.SpacedBy(12))
        {
            Modifier = Modifier.FillMaxWidth().Weight(1f)
                .VerticalScroll(_quizScroll)
                .Padding(horizontal: AndroidVisualTheme.PagePadding, vertical: 16)
                .TestTag(NativeReadingAccess.Screen),
        };
        switch (state.Phase)
        {
            case NativePhase.Idle:
            case NativePhase.Loading:
                viewport.Add(new CircularProgressIndicator
                {
                    Modifier = Modifier.Size(28).TestTag(NativeReadingAccess.Loading)
                });
                viewport.Add(Body(Localize(NativeReadingText.LoadingPassage), palette, secondary: true));
                break;
            case NativePhase.Ready:
                if (state.ResourceTitle is { Length: > 0 } title)
                    viewport.Add(Heading(title, palette, 22,
                        Modifier.TestTag(NativeReadingAccess.Title)
                            .Semantics(semantics => semantics.Heading())));
                if (state.Notice is { } notice)
                {
                    var message = notice.Argument is null ? Localize(notice.Key)
                        : string.Format(CultureInfo.CurrentCulture, Localize(notice.Key), notice.Argument);
                    viewport.Add(StatusAlert(message, palette, error: notice.IsError,
                        automationId: NativeReadingAccess.Notice, success: !notice.IsError));
                }
                if (ReadingAudioStatus(state, palette) is { } audio)
                    viewport.Add(audio);
                viewport.Add(ReadingPassage(state, palette));
                break;
            default:
                var empty = state.Phase == NativePhase.Empty;
                viewport.Add(StatusAlert(
                    Localize(state.ErrorKey ?? NativeReadingText.NoContent), palette,
                    error: !empty,
                    automationId: empty ? NativeReadingAccess.Empty : NativeReadingAccess.Error));
                viewport.Add(Action(Localize(NativeReadingText.GoBack),
                    NativeReadingAccess.GoBack, ExitActivity, !Busy.Value, primary: false));
                break;
        }

        var page = new Column
        {
            Modifier.FillMaxSize().TestTag("native.reading.page"),
            ScreenHeader(Localize(NativeReadingText.Title), palette,
                back: true, backAutomationId: NativeReadingAccess.Back),
            viewport,
        };
        if (state.Phase == NativePhase.Ready && state.SentenceCount > 0)
            page.Add(ReadingFooter(state, palette));
        if (state.Phase == NativePhase.Ready && state.WordPanel is { } panel)
            page.Add(ReadingWordSheet(panel, palette));
        return page;
    }

    private ComposableNode? ReadingAudioStatus(NativeReadingState state, AndroidVisualPalette palette)
    {
        if (state.AudioStatusKey is not { } key)
            return null;
        var message = state.AudioStatusDetail is { } detail
            ? string.Format(CultureInfo.CurrentCulture, Localize(key), detail) : Localize(key);
        if (!state.IsGeneratingAudio)
            return Body(message, palette, secondary: true,
                Modifier.TestTag(NativeReadingAccess.AudioStatus));
        // Source .audio-gen-banner: spinner, status and a 4 px progress bar.
        var column = new Column(Arrangement.SpacedBy(4))
        {
            Modifier.Weight(1f),
            Body(message, palette, modifier: Modifier.TestTag(NativeReadingAccess.AudioStatus)),
        };
        if (state.AudioProgress > 0)
            column.Add(new LinearProgressIndicator
            {
                Progress = (float)state.AudioProgress,
                Modifier = Modifier.FillMaxWidth().Height(4),
            });
        var surface = new Surface
        {
            Modifier = Modifier.FillMaxWidth()
                .Border(1, palette.Primary.WithOpacity(0.3f), AndroidVisualTheme.ControlRadius)
                .TestTag(NativeReadingAccess.AudioBanner),
            Shape = Shape.RoundedCorners(AndroidVisualTheme.ControlRadius),
            Color = palette.Primary.WithOpacity(0.1f),
        };
        surface.Add(new Row(Arrangement.SpacedBy(12), Alignment.Vertical.CenterVertically)
        {
            Modifier.FillMaxWidth().Padding(horizontal: 16, vertical: 12),
            new CircularProgressIndicator { Modifier = Modifier.Size(18) },
            column,
        });
        return surface;
    }

    /// <summary>
    /// InteractiveText: each word carries the punctuation that follows it,
    /// with a small gap for a following space, so the FlowRow reads as
    /// running text. Words are buttons for TalkBack.
    /// </summary>
    private ComposableNode ReadingPassage(NativeReadingState state, AndroidVisualPalette palette)
    {
        var size = state.FontSize;
        var row = new FlowRow { Modifier.FillMaxWidth().TestTag(NativeReadingAccess.Passage) };
        var lead = string.Empty;
        var chunks = new List<(NativeReadingToken? Word, string Text, bool Space)>();
        foreach (var sentence in state.Sentences)
        {
            foreach (var token in sentence.Tokens)
            {
                if (token.IsWord)
                {
                    chunks.Add((token, lead + token.Text, false));
                    lead = string.Empty;
                    continue;
                }
                var rest = token.Text;
                if (chunks.Count > 0 && !chunks[^1].Space && lead.Length == 0
                    && !char.IsWhiteSpace(rest[0]))
                {
                    var split = rest.IndexOfAny([' ', '\t', '\n', '\r', ' ', '　']);
                    var attached = split < 0 ? rest : rest[..split];
                    chunks[^1] = (chunks[^1].Word, chunks[^1].Text + attached, false);
                    rest = split < 0 ? string.Empty : rest[split..];
                }
                if (rest.Any(char.IsWhiteSpace) && chunks.Count > 0 && lead.Length == 0)
                    chunks[^1] = (chunks[^1].Word, chunks[^1].Text, true);
                lead += string.Concat(rest.Where(character => !char.IsWhiteSpace(character)));
            }
            if (chunks.Count > 0)
                chunks[^1] = (chunks[^1].Word, chunks[^1].Text, true);
        }
        if (lead.Length > 0)
            chunks.Add((null, lead, false));

        var tint = palette.Primary.WithOpacity(0.1f);
        foreach (var (word, label, space) in chunks)
        {
            var current = word is not null && word.SentenceIndex == state.CurrentSentenceIndex;
            var text = new Text(label)
            {
                Color = word?.IsVocabulary == true ? palette.Primary : palette.Text,
                FontSize = new Sp(size),
                LineHeight = new Sp(size * 2),
            };
            if (word?.IsVocabulary == true)
            {
                text.FontWeight = FontWeight.SemiBold;
                text.Decoration = TextDecoration.Underline;
            }
            var modifier = Modifier.Padding(end: space ? size * 0.25f : 0f);
            if (current)
                modifier = modifier.Background(tint, Shape.RoundedCorners(3));
            if (word is not null)
            {
                var tokenId = word.Id;
                var description = word.IsVocabulary
                    ? $"{word.Text}, {Localize(NativeReadingText.InYourVocab)}" : word.Text;
                var selected = current;
                modifier = modifier.TestTag(word.AccessibilityId)
                    .Clickable(() => ReadingCommand((presenter, token) => presenter.TapWordAsync(tokenId, token)))
                    .Semantics(semantics =>
                    {
                        semantics.ContentDescription(description);
                        semantics.Role(SemanticsRole.Button);
                        semantics.Selected(selected);
                    });
            }
            text.Modifier = modifier;
            row.Add(text);
        }
        return row;
    }

    private ComposableNode ReadingFooter(NativeReadingState state, AndroidVisualPalette palette)
    {
        ComposableNode Icon(AndroidX.Compose.UI.Graphics.Vector.ImageVector icon, string labelKey, string tag, Action action, bool enabled)
        {
            var label = Localize(labelKey);
            var button = new IconButton(action, enabled: enabled)
            {
                Modifier = Modifier.TestTag(tag).Semantics(semantics => semantics.ContentDescription(label)),
            };
            button.Add(new AndroidX.Compose.Icon(icon, label) { Modifier = Modifier.Size(24), Tint = palette.Text });
            return button;
        }
        var playLabel = Localize(state.IsPlaying ? NativeReadingText.Pause : NativeReadingText.Play);
        var play = new FilledIconButton(() => ReadingCommand((presenter, token) => presenter.TogglePlaybackAsync(token)),
            enabled: state.CanTogglePlayback)
        {
            Modifier = Modifier.TestTag(NativeReadingAccess.PlayPause)
                .Semantics(semantics => semantics.ContentDescription(playLabel)),
        };
        // Material Icons Core has no Pause glyph (Extended is not bound): two bars as text.
        if (state.IsPlaying)
            play.Add(new Text("II") { FontWeight = FontWeight.Bold, FontSize = new Sp(16) });
        else
            play.Add(new AndroidX.Compose.Icon(Icons.Default.PlayArrow, playLabel)
            {
                Modifier = Modifier.Size(24),
            });
        var speedLabel = state.PlaybackSpeed.ToString("0.0#", CultureInfo.CurrentCulture) + "×";
        var speedButton = new TextButton(() => Publish(() => _readingSpeedMenuOpen = true))
        {
            Modifier = Modifier.TestTag(NativeReadingAccess.Speed).Semantics(semantics =>
                semantics.ContentDescription($"{Localize(NativeReadingText.PlaybackSpeed)}, {speedLabel}")),
        };
        speedButton.Add(new Text(speedLabel));
        var menu = PopupMenu(expanded: _readingSpeedMenuOpen,
            onDismissRequest: () => Publish(() => _readingSpeedMenuOpen = false));
        foreach (var speed in NativeReadingPresenter.PlaybackSpeeds)
        {
            var value = speed;
            menu.Add(new DropdownMenuItem(
                text: new Text(value.ToString("0.0#", CultureInfo.CurrentCulture) + "×"),
                onClick: () =>
                {
                    Publish(() => _readingSpeedMenuOpen = false);
                    ReadingCommand((presenter, token) => presenter.SetPlaybackSpeedAsync(value, token));
                })
            {
                Modifier = Modifier.TestTag($"{NativeReadingAccess.Speed}.{value.ToString("0.0#", CultureInfo.InvariantCulture)}"),
            });
        }
        var row = new Row(Arrangement.SpacedBy(4), Alignment.Vertical.CenterVertically)
        {
            Modifier.FillMaxWidth().Padding(horizontal: 12, vertical: 8),
            Icon(Icons.AutoMirrored.Default.KeyboardArrowLeft, NativeReadingText.PreviousSentence, NativeReadingAccess.Previous,
                () => ReadingCommand((presenter, token) => presenter.PreviousSentenceAsync(token)), state.CanNavigate),
            play,
            Icon(Icons.AutoMirrored.Default.KeyboardArrowRight, NativeReadingText.NextSentence, NativeReadingAccess.Next,
                () => ReadingCommand((presenter, token) => presenter.NextSentenceAsync(token)), state.CanNavigate),
            Body(state.SentenceCounter, palette, secondary: true,
                Modifier.TestTag(NativeReadingAccess.SentenceCounter)),
            new Box { Modifier.Weight(1f) },
        };
        if (state.IsAudioLoaded)
            row.Add(Body(state.TimeDisplay, palette, secondary: true, Modifier.TestTag(NativeReadingAccess.Time)));
        row.Add(new Box { speedButton, menu });
        // Source: the font slider is d-none d-md-inline-flex (768 px and wider).
        if (WindowWidthDp > 768)
        {
            var fontLabel = Localize(NativeReadingText.FontSize);
            row.Add(new Slider(state.FontSize,
                value => ReadingCommand((presenter, token) =>
                    presenter.SetFontSizeAsync((int)Math.Round(value), token)),
                enabled: true,
                steps: (NativeReadingPresenter.MaximumFontSize - NativeReadingPresenter.MinimumFontSize) / 2 - 1)
            {
                ValueRange = new FloatRange(NativeReadingPresenter.MinimumFontSize, NativeReadingPresenter.MaximumFontSize),
                Modifier = Modifier.Width(96).TestTag(NativeReadingAccess.FontSize)
                    .Semantics(semantics => semantics.ContentDescription(fontLabel)),
            });
        }
        var surface = new Surface
        {
            Modifier = Modifier.FillMaxWidth().TestTag(NativeReadingAccess.Footer),
            Color = palette.Elevated,
        };
        surface.Add(row);
        return surface;
    }

    private ComposableNode ReadingWordSheet(NativeReadingWordPanel panel, AndroidVisualPalette palette)
    {
        var sheet = new ModalBottomSheet(
            onDismissRequest: () => ReadingCommand((presenter, token) => presenter.CloseWordPanelAsync(token)))
        {
            Modifier = PopupWindowSemantics(Modifier.TestTag(NativeReadingAccess.WordPanel)),
        };
        var content = new Column(Arrangement.SpacedBy(8))
        {
            Modifier.FillMaxWidth().Padding(horizontal: AndroidVisualTheme.PagePadding,
                vertical: AndroidVisualTheme.CardPadding),
        };
        var closeLabel = Localize(NativeReadingText.Close);
        var close = new IconButton(() => ReadingCommand((presenter, token) => presenter.CloseWordPanelAsync(token)))
        {
            Modifier = Modifier.TestTag(NativeReadingAccess.WordPanelClose)
                .Semantics(semantics => semantics.ContentDescription(closeLabel)),
        };
        close.Add(new AndroidX.Compose.Icon(Icons.Default.Close, closeLabel) { Modifier = Modifier.Size(24), Tint = palette.Text });
        var title = new Text(panel.Word)
        {
            Color = palette.Primary,
            FontSize = new Sp(22),
            FontWeight = FontWeight.SemiBold,
            Modifier = Modifier.Weight(1f).TestTag(NativeReadingAccess.WordPanelWord)
                .Semantics(semantics => semantics.Heading()),
        };
        content.Add(new Row(Arrangement.SpacedBy(8), Alignment.Vertical.CenterVertically)
        {
            Modifier.FillMaxWidth(), title, close,
        });
        if (panel.IsTranslating)
            content.Add(new Row(Arrangement.SpacedBy(8), Alignment.Vertical.CenterVertically)
            {
                Modifier.TestTag(NativeReadingAccess.WordPanelLookingUp),
                new CircularProgressIndicator { Modifier = Modifier.Size(18) },
                Body(Localize(NativeReadingText.LookingUp), palette, secondary: true),
            });
        else if (!string.IsNullOrEmpty(panel.Definition))
            content.Add(Body(panel.Definition == NativeReadingPresenter.TranslationUnavailableMarker
                    ? Localize(NativeReadingText.TranslationUnavailable) : panel.Definition,
                palette, modifier: Modifier.TestTag(NativeReadingAccess.WordPanelDefinition)));
        if (panel.IsInVocabulary)
        {
            var badge = new Surface
            {
                Modifier = Modifier.TestTag(NativeReadingAccess.WordPanelInVocabulary),
                Shape = Shape.RoundedCorners(AndroidVisualTheme.ControlRadius),
                Color = palette.Success,
            };
            badge.Add(new Row(Arrangement.SpacedBy(4), Alignment.Vertical.CenterVertically)
            {
                Modifier.Padding(horizontal: 8, vertical: 4),
                new AndroidX.Compose.Icon(Icons.Default.CheckCircle, null) { Modifier = Modifier.Size(16), Tint = Color.White },
                new Text(Localize(NativeReadingText.InYourVocab)) { Color = Color.White, FontSize = new Sp(13) },
            });
            content.Add(badge);
        }
        else if (panel.IsSaving)
            content.Add(new Row(Arrangement.SpacedBy(8), Alignment.Vertical.CenterVertically)
            {
                Modifier.TestTag(NativeReadingAccess.WordPanelSaving),
                new CircularProgressIndicator { Modifier = Modifier.Size(18) },
                Body(Localize(NativeReadingText.Saving), palette),
            });
        else if (panel.CanSave)
            content.Add(Action(Localize(NativeReadingText.RememberThisWord), NativeReadingAccess.WordPanelSave,
                () => ReadingCommand((presenter, token) => presenter.SaveWordAsync(token)), enabled: true));
        sheet.Add(content);
        return sheet;
    }
}
