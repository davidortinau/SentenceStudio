using System.Globalization;
using AndroidX.Compose;
using AndroidX.Compose.Runtime;
using AndroidX.Compose.UI.Graphics;
using AndroidX.Compose.UI.Graphics.Painter;
using AndroidX.Compose.UI.Graphics.Vector;
using SentenceStudio.Native;

namespace SentenceStudio.Native.Android;

/// <summary>
/// Compose parts of the Vocabulary Quiz question (source VocabQuiz.razor):
/// the header "More" menu, the prompt tool row (play, learning details,
/// Show/Hide image, sample sentences), the photo thumbnail, the sample
/// sentence panel and the multiple-choice options, including lettered options
/// with play buttons for target-language answer audio.
/// </summary>
internal sealed partial class AndroidSliceShell
{
    /// <summary>Quiz effects that run after a composition: photo render reports and focus.</summary>
    private void RememberQuizEffects(IComposer composer)
    {
        _correctionFocus = composer.Remember(() => new FocusRequester());
        _detailsFocus = composer.Remember(() => new FocusRequester());

        var quizOwner = Destination == NativeDestination.VocabularyQuiz
            ? QuizPhotoOwner() : null;
        (string Owner, string Turn, NativeQuizPhotoAsset Asset)? drawn = null;
        if (quizOwner is not null && _media is { } media
            && AndroidQuizPhotoPolicy.NeedsRenderReport(Quiz, quizOwner)
            && Quiz.Prompt?.Photo is { } asset
            && media.TryGetPhoto(asset, out _))
            drawn = (quizOwner, Quiz.TurnId!, asset);
        composer.Effect(AndroidEffectKey.Of(drawn?.Owner, drawn?.Turn, drawn?.Asset),
            cancellationToken =>
        {
            if (drawn is { } photo)
                _ = ReportQuizPhotoRenderedAsync(photo.Owner, photo.Turn, photo.Asset);
            return Task.CompletedTask;
        });

        // The turn's question is composed on screen: report it. The presenter decides whether it
        // autoplays (AutoPlayPending: once, after a requested photo is drawn, no overlay or
        // sentence shortcut); the key changes when that becomes true for the same turn.
        var shownTurn = quizOwner is not null
            && Quiz is { Prompt: not null, Sentence: null,
                Phase: NativePhase.Ready or NativePhase.Feedback }
            ? Quiz.TurnId : null;
        var autoPlay = shownTurn is not null && Quiz.AutoPlayPending;
        composer.Effect(AndroidEffectKey.Of(shownTurn, autoPlay), _ =>
        {
            if (shownTurn is not null)
                OnQuizPromptShown(shownTurn, autoPlay);
            return Task.CompletedTask;
        });

        // The one response slot becomes "Type correct" after a wrong answer; it
        // takes focus once it is enabled so the keyboard (and IME) stay with it.
        var correctionTurn = Destination == NativeDestination.VocabularyQuiz
            && Quiz is { Phase: NativePhase.Feedback, RequiresCorrection: true,
                Mode: "Text", Sentence: null }
            && !Busy.Value
                ? Quiz.TurnId : null;
        composer.Effect(correctionTurn, _ =>
        {
            if (correctionTurn is not null && correctionTurn == Quiz.TurnId)
                RequestFocusQuietly(_correctionFocus);
            return Task.CompletedTask;
        });

        var detailsVersion = _detailsFocusRestoreVersion;
        composer.Effect(detailsVersion, _ =>
        {
            if (detailsVersion > 0 && detailsVersion == _detailsFocusRestoreVersion
                && Destination == NativeDestination.VocabularyQuiz
                && Quiz.Overlay == NativeQuizOverlay.None
                && Quiz.Phase is NativePhase.Ready or NativePhase.Feedback)
                RequestFocusQuietly(_detailsFocus);
            return Task.CompletedTask;
        });
    }

    private static void RequestFocusQuietly(FocusRequester? requester)
    {
        try
        {
            requester?.RequestFocus();
        }
        catch (Java.Lang.IllegalStateException)
        {
            // The target left the composition before the effect ran.
        }
    }

    /// <summary>Source PageHeader: Back, title, and "Use in a sentence" / Hide Text under More.</summary>
    private ComposableNode QuizHeader(NativeQuizState state, AndroidVisualPalette palette)
    {
        var bar = (TopAppBar)ScreenHeader(Localize(NativeText.Quiz), palette, back: true);
        var canSentence = AndroidQuizTurnPolicy.CanOpenSentence(state, busy: false);
        var canPhotoText = AndroidQuizTurnPolicy.CanTogglePhotoText(state, busy: false);
        if (!canSentence && !canPhotoText)
        {
            _quizMenuOpen = false;
            return bar;
        }
        var more = Localize("More");
        var overflow = new IconButton(
            () => Publish(() => _quizMenuOpen = true), enabled: !Busy.Value)
        {
            Modifier = Modifier.TestTag("native.quiz.menu")
                .Semantics(semantics => semantics.ContentDescription(more)),
        };
        overflow.Add(new Icon(Icons.Default.MoreVert, more)
        {
            Modifier = Modifier.Size(24),
            Tint = palette.Text,
        });
        var menu = PopupMenu(expanded: _quizMenuOpen && !Busy.Value,
            onDismissRequest: () => Publish(() => _quizMenuOpen = false));
        if (canSentence)
            menu.Add(new DropdownMenuItem(
                text: new Text(Localize("NativeVocabQuiz_UseInSentence")),
                onClick: OpenQuizSentence)
            {
                Modifier = Modifier.TestTag("native.quiz.use-in-sentence"),
            });
        if (canPhotoText)
            menu.Add(new DropdownMenuItem(
                text: new Text(Localize(state.Prompt?.ShowTextWithPhoto == true
                    ? "VocabQuiz_HideText" : "VocabQuiz_ShowText")),
                onClick: ToggleQuizPhotoText)
            {
                Modifier = Modifier.TestTag("native.quiz.photo-text"),
            });
        bar.Actions = new Box { overflow, menu };
        return bar;
    }

    /// <summary>Source tool row under the prompt: play, details, Show/Hide image, sample sentences.</summary>
    private ComposableNode? RenderQuizPromptActions(
        NativeQuizState state, NativeQuizPrompt prompt, AndroidVisualPalette palette)
    {
        var tools = new Row(Arrangement.SpacedBy(12), Alignment.Vertical.CenterVertically)
        {
            Modifier = Modifier.TestTag("native.quiz.tools"),
        };
        if (prompt.CanPlayPromptAudio)
            tools.Add(QuizToolButton(Icons.Default.PlayArrow,
                Localize("VocabWordEdit_PlayAudio"), "native.quiz.audio", PlayPrompt,
                state.Phase == NativePhase.Ready && !Busy.Value, palette));
        if (state.Phase is NativePhase.Ready or NativePhase.Feedback)
            tools.Add(QuizToolButton(Icons.Default.Info,
                Localize("VocabQuiz_LearningDetails"), "native.quiz.details.open",
                OpenQuizDetails, AndroidQuizTurnPolicy.CanOpenDetails(state, Busy.Value),
                palette, _detailsFocus));

        var toggles = new Row(Arrangement.SpacedBy(8), Alignment.Vertical.CenterVertically)
        {
            Modifier = Modifier.TestTag("native.quiz.toggles"),
        };
        var toggleCount = 0;
        if (AndroidQuizPhotoPolicy.ShowsToggle(state))
        {
            toggles.Add(QuizToggleButton(Localize(prompt.ShowPhoto
                    ? "NativeVocabQuiz_HideImage" : "NativeVocabQuiz_ShowImage"),
                "native.quiz.photo.toggle", ToggleQuizPhotoVisible, !Busy.Value));
            toggleCount++;
        }
        if (prompt.IsTargetLanguagePrompt && state.CanShowSentenceHints
            && state.Phase is NativePhase.Ready or NativePhase.Feedback)
        {
            toggles.Add(QuizToggleButton(Localize(state.SentenceHintsExpanded
                    ? "VocabQuiz_HideSampleSentences" : "VocabQuiz_ShowSampleSentences"),
                "native.quiz.hints.toggle", ToggleQuizSentenceHints,
                AndroidQuizTurnPolicy.CanToggleSentenceHints(state, Busy.Value)));
            toggleCount++;
        }
        var column = new Column(Arrangement.SpacedBy(8),
            Alignment.Horizontal.CenterHorizontally)
        {
            Modifier = Modifier.FillMaxWidth().TestTag("native.quiz.prompt.actions"),
        };
        column.Add(tools);
        if (toggleCount > 0)
            column.Add(toggles);
        return column;
    }

    private static ComposableNode QuizToolButton(ImageVector icon, string label,
        string automationId, Action command, bool enabled,
        AndroidVisualPalette palette, FocusRequester? focus = null)
    {
        var modifier = Modifier.Size(AndroidVisualTheme.ControlHeight)
            .Border(1, palette.Border, AndroidVisualTheme.ControlHeight / 2);
        if (focus is not null)
            modifier = modifier.FocusRequester(focus);
        var button = new IconButton(command, enabled: enabled)
        {
            Modifier = modifier.TestTag(automationId)
                .Semantics(semantics => semantics.ContentDescription(label)),
        };
        button.Add(new Icon(icon, label)
        {
            Modifier = Modifier.Size(24),
            Tint = enabled ? palette.Text : palette.SecondaryText,
        });
        return button;
    }

    private static ComposableNode QuizToggleButton(
        string label, string automationId, Action command, bool enabled)
    {
        var button = new OutlinedButton(onClick: command, enabled: enabled)
        {
            Modifier = Modifier.HeightIn(min: AndroidVisualTheme.ControlHeight)
                .TestTag(automationId)
                .Semantics(semantics => semantics.ContentDescription(label)),
            Shape = Shape.RoundedCorners(AndroidVisualTheme.ControlHeight / 2),
        };
        button.Add(new Text(label));
        return button;
    }

    /// <summary>
    /// The turn's photo, drawn from the decoded owner bitmap. It opens full
    /// screen once the presenter counts it as shown, in the question or after a
    /// correct answer reveals the mnemonic image.
    /// </summary>
    private ComposableNode? RenderQuizThumbnail(
        NativeQuizState state, NativeQuizPrompt prompt, AndroidVisualPalette palette)
    {
        if (!AndroidQuizPhotoPolicy.ShowsThumbnail(state) || _media is null
            || !_media.TryGetPhoto(prompt.Photo, out var bitmap) || bitmap is null)
            return null;
        var imageBitmap = AndroidImageBitmap_androidKt.AsImageBitmap(bitmap);
        var painter = BitmapPainterKt.BitmapPainter(
            image: imageBitmap, srcOffset: 0L,
            srcSize: ((long)bitmap.Width << 32) | (uint)bitmap.Height,
            filterQuality: 1);
        var photoLabel = Localize(prompt.PhotoAccessibilityKey);
        var enlarge = Localize("VocabQuiz_EnlargePhoto");
        var interactive = AndroidQuizPhotoPolicy.CanOpenFullScreen(state) && !Busy.Value;
        var modifier = Modifier.FillMaxWidth().HeightIn(min: 160)
            .TestTag("native.quiz.photo.thumbnail");
        modifier = interactive
            ? modifier.FocusRequester(_photoFocus!)
                .Clickable(OpenPhoto)
                .Semantics(semantics =>
                {
                    semantics.ContentDescription(enlarge);
                    semantics.Role(SemanticsRole.Button);
                })
            : modifier.Semantics(semantics => semantics.ContentDescription(photoLabel));
        var photo = new Surface
        {
            Modifier = modifier,
            Shape = Shape.RoundedCorners(AndroidVisualTheme.ControlRadius),
            Color = palette.Elevated,
        };
        photo.Add(new Image(painter, photoLabel)
        {
            Modifier = Modifier.FillMaxWidth().Height(160).TestTag("native.quiz.photo.image"),
            ContentScale = ContentScale.Fit,
        });
        return photo;
    }

    /// <summary>Source sample-sentence panel: target-language sentences only, on target-prompt turns.</summary>
    private static ComposableNode? RenderSentenceHints(
        NativeQuizState state, NativeQuizPrompt prompt, AndroidVisualPalette palette)
    {
        if (!prompt.IsTargetLanguagePrompt || !state.SentenceHintsExpanded
            || state.SentenceHints.IsDefaultOrEmpty)
            return null;
        var label = Localize("VocabQuiz_SampleSentencesLabel");
        var heading = Body(label, palette,
            modifier: Modifier.Semantics(semantics => semantics.Heading()));
        heading.FontWeight = FontWeight.SemiBold;
        var content = new Column(Arrangement.SpacedBy(6))
        {
            Modifier = Modifier.FillMaxWidth().Padding(12),
        };
        content.Add(heading);
        foreach (var hint in state.SentenceHints)
            content.Add(Body(hint.TargetSentence, palette,
                modifier: Modifier.FillMaxWidth()
                    .TestTag($"native.quiz.hints.sentence.{hint.ExampleSentenceId}")));
        var panel = new Surface
        {
            Modifier = Modifier.FillMaxWidth()
                .Border(1, palette.Border, AndroidVisualTheme.ControlRadius)
                .TestTag("native.quiz.hints.panel"),
            Shape = Shape.RoundedCorners(AndroidVisualTheme.ControlRadius),
            Color = palette.Elevated,
        };
        panel.Add(content);
        return panel;
    }

    /// <summary>
    /// Multiple-choice options. With target-language answer audio on a
    /// native-language prompt (source ShowAudioChoiceControls) each option is a
    /// play button and a lettered choice; otherwise each option shows its text.
    /// </summary>
    private ComposableNode RenderQuizChoices(
        IComposer composer, NativeQuizState state, AndroidVisualPalette palette)
    {
        var choices = state.Choices.IsDefault ? [] : state.Choices;
        var audio = AndroidQuizTurnPolicy.UsesAudioChoices(state);
        var canChoose = AndroidQuizTurnPolicy.CanChoose(state, Busy.Value);
        var canPlay = AndroidQuizTurnPolicy.CanPlayChoice(state, Busy.Value);
        var options = new Column(Arrangement.SpacedBy(8))
        {
            Modifier = Modifier.FillMaxWidth().TestTag("native.quiz.choices"),
        };
        for (var index = 0; index < choices.Length; index++)
        {
            var choice = choices[index];
            if (!audio)
            {
                options.Add(ChoiceAction(composer, palette, choice, index, state,
                    AnswerQuiz, canChoose));
                continue;
            }
            var letter = AndroidQuizTurnPolicy.Letter(index);
            var optionIndex = index;
            var playLabel = string.Format(CultureInfo.CurrentCulture,
                Localize("NativeVocabQuiz_PlayChoice"), letter);
            var play = new OutlinedButton(onClick: () => PlayQuizChoice(optionIndex),
                enabled: canPlay)
            {
                Modifier = Modifier.HeightIn(min: AndroidVisualTheme.ControlHeight)
                    .WidthIn(min: 64)
                    .TestTag($"native.quiz.choice.audio.{index}")
                    .Semantics(semantics => semantics.ContentDescription(playLabel)),
                Shape = Shape.RoundedCorners(AndroidVisualTheme.ControlRadius),
            };
            play.Add(new Icon(Icons.Default.PlayArrow, playLabel)
            {
                Modifier = Modifier.Size(24),
                Tint = canPlay ? palette.Text : palette.SecondaryText,
            });
            options.Add(new Row(Arrangement.SpacedBy(8), Alignment.Vertical.CenterVertically)
            {
                Modifier.FillMaxWidth().TestTag($"native.quiz.choice.row.{index}"),
                play,
                new Box
                {
                    Modifier.Weight(1f),
                    ChoiceAction(composer, palette, choice, index, state, AnswerQuiz,
                        canChoose, letter),
                },
            });
        }
        return options;
    }
}
