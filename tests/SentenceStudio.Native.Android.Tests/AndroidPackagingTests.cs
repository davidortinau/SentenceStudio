using System.Reflection;
using System.Xml.Linq;
using SentenceStudio.Native;

namespace SentenceStudio.Native.Android.Tests;

public sealed class AndroidPackagingTests
{
    private static readonly string HeadRoot = Path.GetFullPath(typeof(AndroidPackagingTests)
        .Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
        .Single(attribute => attribute.Key == "NativeAndroidHeadRoot").Value!);

    [Fact]
    public void DebugHasAnIsolatedIdentityAndReleaseKeepsProductionIdentity()
    {
        var project = XDocument.Load(Path.Combine(
            HeadRoot, "SentenceStudio.Native.Android.csproj"));
        var ids = project.Descendants("ApplicationId").ToDictionary(
            node => (string?)node.Attribute("Condition") ?? string.Empty,
            node => node.Value);
        Assert.Equal("com.simplyprofound.sentencestudio.compose.debug",
            ids["'$(Configuration)' == 'Debug'"]);
        Assert.Equal("com.simplyprofound.sentencestudio",
            ids["'$(Configuration)' == 'Release'"]);
        Assert.Contains(project.Descendants("ProjectReference"), reference =>
            ((string?)reference.Attribute("Include"))?.EndsWith(
                "/SentenceStudio.Native.csproj", StringComparison.Ordinal) == true);
    }

    [Fact]
    public void AilohaBindingIsReferencedOnlyForDebug()
    {
        var project = XDocument.Load(Path.Combine(
            HeadRoot, "SentenceStudio.Native.Android.csproj"));
        var bindings = project.Descendants("ProjectReference").Where(reference =>
            ((string?)reference.Attribute("Include"))?.Contains(
                "AilohaBinding/", StringComparison.Ordinal) == true);
        var binding = Assert.Single(bindings);
        Assert.Equal("'$(Configuration)' == 'Debug'",
            (string?)binding.Attribute("Condition"));
        Assert.StartsWith("#if DEBUG", File.ReadAllText(
            Path.Combine(HeadRoot, "DebugAgentApplication.cs")));
    }

    [Fact]
    public void NativeTheme_UsesSystemLightAndDarkBarAppearances()
    {
        var activity = File.ReadAllText(Path.Combine(HeadRoot, "MainActivity.cs"));
        Assert.Contains("Theme = \"@style/SentenceStudioNativeTheme\"", activity);
        foreach (var (folder, expectedLight) in new[]
        {
            ("values", "true"), ("values-night", "false")
        })
        {
            var resources = XDocument.Load(Path.Combine(
                HeadRoot, "Resources", folder, "styles.xml"));
            var style = Assert.Single(resources.Descendants("style"));
            Assert.Equal("SentenceStudioNativeTheme",
                (string?)style.Attribute("name"));
            var items = style.Elements("item").ToDictionary(
                item => (string?)item.Attribute("name") ?? string.Empty,
                item => item.Value);
            Assert.Equal(expectedLight, items["android:windowLightStatusBar"]);
            Assert.Equal(expectedLight, items["android:windowLightNavigationBar"]);
        }
        Assert.Contains("UseDynamicColor = false", File.ReadAllText(Path.Combine(
            HeadRoot, "AndroidSliceShell.Compose.cs")));
    }

    [Fact]
    public void QuizChoices_ShowPendingSelectionWithoutScoringEarly()
    {
        Assert.Contains("Quiz = Quiz with { SelectedAnswer = response }",
            File.ReadAllText(Path.Combine(HeadRoot, "AndroidSliceShell.cs")));
        Assert.Contains("semantics.Selected(selected)",
            File.ReadAllText(Path.Combine(HeadRoot, "AndroidSliceShell.Visual.cs")));
    }

    [Fact]
    public void SignInControls_UseSharedRememberAndPasswordVisibilityContracts()
    {
        var shell = File.ReadAllText(Path.Combine(HeadRoot, "AndroidSliceShell.cs"));
        var composition = File.ReadAllText(Path.Combine(
            HeadRoot, "AndroidSliceShell.Compose.cs"));
        Assert.Contains("rememberMe: Remember.Value", shell);
        Assert.Contains("new Checkbox(@checked: Remember.Value", composition);
        Assert.Contains("NativeAccess.PasswordVisibility", composition);
        Assert.Contains("VisualTransformation = ShowPassword.Value", composition);
        Assert.Contains("NativeText.ShowPasswordShort", composition);
        Assert.Contains("state.Phase is not (NativePhase.Loading or NativePhase.Blocked)",
            composition);
        Assert.Contains("new TextButton(", composition);
        Assert.DoesNotContain("TrailingIcon = new IconButton", composition);
        foreach (var culture in new[] { "AppResources.resx", "AppResources.ko.resx" })
        {
            var resources = XDocument.Load(Path.Combine(HeadRoot, "..",
                "SentenceStudio.Shared", "Resources", "Strings", culture));
            foreach (var key in new[]
            {
                "Login_ShowPassword", "Login_HidePassword",
                "Login_ShowPasswordShort", "Login_HidePasswordShort",
            })
                Assert.Contains(resources.Descendants("data"),
                    entry => (string?)entry.Attribute("name") == key);
        }
    }

    [Fact]
    public void AccountEntryAndOnboardingUseNativeRoutesNotAWebAppNotice()
    {
        var shell = File.ReadAllText(Path.Combine(HeadRoot, "AndroidSliceShell.cs"));
        var composition = File.ReadAllText(Path.Combine(
            HeadRoot, "AndroidSliceShell.Compose.cs"));
        var auth = File.ReadAllText(Path.Combine(
            HeadRoot, "AndroidSliceShell.Auth.cs"));
        var onboarding = File.ReadAllText(Path.Combine(
            HeadRoot, "AndroidSliceShell.Onboarding.cs"));

        Assert.Contains("OpenRegistration", shell);
        Assert.Contains("OpenRecovery", shell);
        Assert.Contains("native.auth.register.open", composition);
        Assert.Contains("native.auth.forgot.open", composition);
        Assert.Contains("native.auth.register.submit", auth);
        Assert.Contains("native.auth.recovery.send", auth);
        // Source RegisterPage confirmation, as iOS (AF-N01).
        Assert.Contains("Register_CheckYourEmail", auth);
        Assert.Contains("Register_VerificationSent", auth);
        Assert.Contains("NativePasswordRecoveryOutcome.Accepted", auth);
        Assert.DoesNotContain("PostAsJsonAsync", auth);
        Assert.Contains("RenderOnboarding(palette)", composition);
        Assert.Contains("native.auth.onboarding.pending", onboarding);
        // Step 5 finishes through the shared presenter's owner-checked API write (AF-N04).
        Assert.DoesNotContain("native.auth.onboarding.unavailable", onboarding);
        Assert.Contains("auth.SaveOnboardingProfileAsync(draft, cancellationToken)", onboarding);
        Assert.Contains("auth.FinishOnboardingAsync(createStarter, cancellationToken)", onboarding);
        Assert.Contains("native.auth.onboarding.finish.starter", onboarding);
        Assert.Contains("native.auth.onboarding.finish.skip", onboarding);
        Assert.Contains("NativeOnboardingOptions.NativeLanguages", onboarding);
        Assert.Contains("NativeOnboardingOptions.TargetLanguages", onboarding);
        Assert.Contains("NativeOnboardingOptions.SessionMinutes", onboarding);
        Assert.Contains("NativeOnboardingOptions.CefrLevels", onboarding);
        Assert.Contains("Common_MinutesShort", onboarding);
        Assert.Contains("inline: true", onboarding);
        Assert.Contains("RenderOnboardingSteps(palette)", onboarding);
        Assert.Contains("new Row(Arrangement.Center", onboarding);
        Assert.Contains(".VerticalScroll(_onboardingScroll)", onboarding);
        Assert.Contains("composer.LaunchedEffect(onboardingKey", composition);
        Assert.Contains("_onboardingScroll.ScrollToAsync(0, cancellationToken)",
            composition);
        Assert.Contains("_onboardingMinutes = 15", onboarding);
        Assert.Contains("_onboardingCefr = \"A2\"", onboarding);
        Assert.DoesNotContain("Complete your account setup in the WebApp",
            composition);
    }

    [Fact]
    public void PendingReadinessAndDataLoadsShowNativeProgressInsteadOfAStaticNotice()
    {
        var auth = File.ReadAllText(Path.Combine(
            HeadRoot, "AndroidSliceShell.Auth.cs"));
        var composition = File.ReadAllText(Path.Combine(
            HeadRoot, "AndroidSliceShell.Compose.cs"));

        Assert.Contains("MainLayout_SyncingProfile", auth);
        Assert.Contains("native.auth.sync.progress", auth);
        Assert.Contains("native.auth.sync.retry", auth);
        Assert.Contains("native.auth.signin.progress", composition);
        Assert.Contains("native.dashboard.loading.progress", composition);
        Assert.Contains("native.dashboard.sync.retry", composition);
        Assert.Contains("Localize(\"Native_SyncPending\")", composition);
        Assert.Contains("native.dashboard.sync.caption", composition);
        Assert.DoesNotContain("DebugHealth_CoreSync", composition);
        Assert.Contains("native.quiz.loading.progress", composition);
    }

    [Fact]
    public void PasswordReveal_UsesExplicitNoOpTransformationOnRecomposition()
    {
        var composition = File.ReadAllText(Path.Combine(
            HeadRoot, "AndroidSliceShell.Compose.cs"));
        Assert.Matches(
            @"VisualTransformation = ShowPassword\.Value\s*\?\s*AndroidX\.Compose\.UI\.Text\.Input\.VisualTransformation\.Companion\.None\s*:\s*new PasswordVisualTransformation",
            composition);
    }

    [Fact]
    public void QuizCorrectionOverride_UsesLocalizedPresenterCommand()
    {
        var composition = File.ReadAllText(Path.Combine(
            HeadRoot, "AndroidSliceShell.Compose.cs"));
        var shell = File.ReadAllText(Path.Combine(HeadRoot, "AndroidSliceShell.cs"));
        Assert.Contains("Action(Localize(NativeText.QuizOverride)", composition);
        Assert.Contains("\"native.quiz.override\", OverrideQuiz", composition);
        Assert.Contains("RequiredQuiz().OverrideAsCorrectAsync(cancellationToken)", shell);
        Assert.Contains("state.Phase == NativePhase.Feedback", shell);
    }

    [Fact]
    public void QuizAutoPlayIsOwnedByTheSharedPresenter_NotQueuedTwiceByTheHead()
    {
        var shell = File.ReadAllText(Path.Combine(
            HeadRoot, "AndroidSliceShell.cs"));
        var policy = File.ReadAllText(Path.Combine(
            HeadRoot, "AndroidQuizInputPolicy.cs"));
        Assert.DoesNotContain("QueuePromptAutoPlay", shell);
        Assert.DoesNotContain("ShouldAutoPlay", policy);
        Assert.Contains("RequiredQuiz().PlayPromptAsync(cancellationToken)", shell);
    }

    [Fact]
    public void TextCorrectionRetainsTheAnswerFieldPositionAndImeSubmit()
    {
        var composition = File.ReadAllText(Path.Combine(
            HeadRoot, "AndroidSliceShell.Compose.cs"));
        var shell = File.ReadAllText(Path.Combine(HeadRoot, "AndroidSliceShell.cs"));
        Assert.Contains("native.quiz.answer.reveal", composition);
        Assert.Contains("native.quiz.feedback.slot", composition);
        Assert.Contains("var text = needsCorrection ? Correction : Answer", composition);
        Assert.Contains("MutableState<TextFieldValue> Answer", shell);
        Assert.Contains("ComposeExtensions.NewTextFieldValue(string.Empty)", shell);
        Assert.Contains("native.quiz.correction.submit", composition);
        Assert.Contains("NativeText.CorrectionRequired", composition);
        Assert.Contains("KeyboardActionsHelper.Create(onDone:", composition);
        Assert.DoesNotContain("new OutlinedTextField(Correction", composition);
    }

    [Fact]
    public void QuizSummary_RendersOwnedWordMeaningAndTextualVerdict()
    {
        var composition = File.ReadAllText(Path.Combine(
            HeadRoot, "AndroidSliceShell.Compose.cs"));
        Assert.Contains("state.Results.IsDefault ? [] : state.Results", composition);
        Assert.Contains("result.TargetLanguageTerm", composition);
        Assert.Contains("result.NativeLanguageTerm", composition);
        Assert.Contains("result.WasCorrect", composition);
        Assert.Contains("result.ReadyToRotateOut", composition);
        Assert.Contains("\"VocabQuiz_Mastered\" : NativeText.QuizCorrect", composition);
        Assert.Contains("NativeText.QuizIncorrect", composition);
        Assert.Contains("NativeAccess.QuizSummaryResult", composition);
    }

    [Fact]
    public void QuizCompleteFormAndMnemonicAppearOnlyInCorrectGradedFeedback()
    {
        var composition = File.ReadAllText(Path.Combine(
            HeadRoot, "AndroidSliceShell.Compose.cs"));
        var start = composition.IndexOf("if (state.Phase == NativePhase.Feedback)",
            StringComparison.Ordinal);
        Assert.True(start >= 0);
        var end = composition.IndexOf("if (state.ErrorKey is not null)",
            start, StringComparison.Ordinal);
        Assert.True(end > start);
        var feedback = composition[start..end];

        Assert.Contains("state.AnswerWasCorrect == true", feedback);
        Assert.Contains("state.CompleteForm", feedback);
        Assert.Contains("NativeVocabQuiz_FullForm", feedback);
        // Source order: thumbnail, mnemonic, sample sentences, then the answer.
        var mnemonicStart = composition.IndexOf(
            "if (state.Phase == NativePhase.Feedback && state.AnswerWasCorrect == true",
            StringComparison.Ordinal);
        Assert.True(mnemonicStart >= 0 && mnemonicStart < start);
        var mnemonic = composition[mnemonicStart..start];
        Assert.Contains("state.FeedbackMnemonicText", mnemonic);
        Assert.Contains("NativeVocabQuiz_Mnemonic", mnemonic);
        Assert.Contains("native.quiz.feedback.mnemonic", mnemonic);
    }

    [Fact]
    public void QuizDetailsSheetOpensFromTheInfoButtonWithoutLeakingAnswerTerms()
    {
        var composition = File.ReadAllText(Path.Combine(
            HeadRoot, "AndroidSliceShell.Compose.cs"));
        var start = composition.IndexOf(
            "private ComposableNode RenderLearningDetails(", StringComparison.Ordinal);
        var end = composition.IndexOf(
            "private static ComposableNode DetailField(", start,
            StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start);
        var details = composition[start..end];
        // AF-Q08: the info button in the prompt tool row opens the sheet.
        var quiz = File.ReadAllText(Path.Combine(
            HeadRoot, "AndroidSliceShell.Quiz.Compose.cs"));
        Assert.Contains("\"native.quiz.details.open\"", quiz);
        Assert.Contains("OpenQuizDetails, AndroidQuizTurnPolicy.CanOpenDetails(state, Busy.Value)",
            quiz);
        Assert.Contains("_quiz.OpenDetails()", File.ReadAllText(Path.Combine(
            HeadRoot, "AndroidSliceShell.Quiz.cs")));
        Assert.Contains("state.Overlay == NativeQuizOverlay.LearningDetails",
            composition);
        Assert.Contains("new ModalBottomSheet(onDismissRequest: DismissOverlay)",
            details);
        Assert.Contains("NativeAccess.QuizDetails", details);
        Assert.Contains("0.7f", details);
        Assert.Contains("details.TotalAttempts", details);
        Assert.Contains("details.CorrectAttempts", details);
        Assert.Contains("details.CurrentStreak", details);
        Assert.Contains("details.ProductionInStreak", details);
        Assert.Contains("details.EffectiveStreak", details);
        Assert.Contains("details.MasteryScore", details);
        Assert.Contains("details.NextReviewDate", details);
        Assert.Contains("details.ReviewInterval", details);
        Assert.Contains("details.EaseFactor", details);
        Assert.Contains("details.IsDueForReview", details);
        Assert.Contains("details.Mode", details);
        Assert.Contains("details.DueOnly", details);
        Assert.Contains("details.WhyModeKey", details);
        Assert.DoesNotContain("TargetLanguageTerm", details);
        Assert.DoesNotContain("NativeLanguageTerm", details);
        Assert.DoesNotContain("CorrectAnswerAfterGrading", details);
        Assert.DoesNotContain("PromptText", details);
    }

    [Theory]
    [InlineData("AppResources.resx")]
    [InlineData("AppResources.ko.resx")]
    public void QuizDetailsLabelsExistForTheAndroidDetailsControl(
        string culture)
    {
        var resources = XDocument.Load(Path.Combine(HeadRoot, "..",
            "SentenceStudio.Shared", "Resources", "Strings", culture));
        var keys = resources.Descendants("data")
            .Select(entry => (string?)entry.Attribute("name") ?? string.Empty)
            .ToHashSet(StringComparer.Ordinal);
        var missing = new[]
        {
            "TotalAttempts", "CorrectAttempts", "CurrentStreak",
            "ProductionInStreak", "EffectiveStreak", "MasteryScore",
            "SrsSchedule", "ReviewInterval", "Days", "EaseFactor",
            "IsDueForReview", "QuizContext", "QuizMode", "TextMode",
            "MultipleChoiceMode", "DueOnly", "WhyMode",
            "RecognitionDemonstrations"
        }.Where(suffix => !keys.Contains("NativeVocabQuiz_Details_" + suffix))
            .ToArray();
        // The shared Learning Details labels exist; both heads show the sheet.
        Assert.Empty(missing);
        Assert.Contains("VocabQuiz_LearningDetails", File.ReadAllText(
            Path.Combine(HeadRoot, "AndroidSliceShell.Quiz.Compose.cs")));
    }

    [Fact]
    public void ClosingAResourceOpenedFromTheDashboardReloadsTheDashboard()
    {
        var resources = File.ReadAllText(Path.Combine(HeadRoot, "AndroidSliceShell.Resources.cs"));
        var start = resources.IndexOf("private void OnResourceFormOpenChanged", StringComparison.Ordinal);
        Assert.True(start >= 0);
        var end = resources.IndexOf("\n    }", start, StringComparison.Ordinal);
        var body = resources[start..end];
        // A renamed or deleted resource must not leave stale Today's Insight links.
        Assert.Contains("Destination = NativeDestination.Dashboard;", body);
        Assert.Contains("ReloadDashboardAfterResources();", body);
    }

    [Fact]
    public void QuizPhotoIsTheOwnersDecodedBitmapReportedOnceAndOpensFromFeedback()
    {
        var quiz = File.ReadAllText(Path.Combine(
            HeadRoot, "AndroidSliceShell.Quiz.Compose.cs"));
        var commands = File.ReadAllText(Path.Combine(
            HeadRoot, "AndroidSliceShell.Quiz.cs"));
        var shell = File.ReadAllText(Path.Combine(
            HeadRoot, "AndroidSliceShell.cs"));
        var media = File.ReadAllText(Path.Combine(HeadRoot, "AndroidQuizMedia.cs"));
        // AF-Q05: the adapter returns owner-checked bytes that decoded to a bitmap.
        Assert.Contains("public async Task<NativeQuizPhotoAsset?> LoadPhotoAsync(", media);
        Assert.Contains("AndroidQuizMediaPolicy.DetectImageContentType(encoded)", media);
        Assert.Contains("Task.Run(() => Decode(encoded), cancellationToken)", media);
        Assert.Contains("ReferenceEquals(photo.Asset, asset)", media);
        // The thumbnail draws the presenter's asset and reports it drawn.
        Assert.Contains("_media.TryGetPhoto(prompt.Photo, out var bitmap)", quiz);
        Assert.Contains("AndroidQuizPhotoPolicy.NeedsRenderReport(Quiz, owner)", quiz);
        Assert.Contains("ReportQuizPhotoRenderedAsync(photo.Owner, photo.Turn, photo.Asset)", quiz);
        Assert.Contains("quiz.PhotoRenderedAsync(turnId, _lifetime.Token)", commands);
        Assert.Contains("ReferenceEquals(current.Prompt?.Photo, asset)", commands);
        Assert.Contains("native.quiz.photo.toggle", quiz);
        Assert.Contains("AndroidQuizPhotoPolicy.CanOpenFullScreen(state) && !Busy.Value", quiz);
        Assert.Contains(".Clickable(OpenPhoto)", quiz);
        Assert.DoesNotContain("FeedbackPhotoCandidateUri", quiz);
        // A requested photo this head cannot draw falls back through the presenter.
        Assert.Contains("RequiredQuiz().PhotoFailedAsync(turnId, cancellationToken)", shell);
        var open = shell[shell.IndexOf(
            "public void OpenPhoto()", StringComparison.Ordinal)
            ..shell.IndexOf("private void OnPhotoDismissed(",
                StringComparison.Ordinal)];
        Assert.Contains("AndroidQuizPhotoPolicy.CanOpenFullScreen(Quiz)", open);
        Assert.Contains("_media.TryGetPhoto(state.Prompt.Photo, out var bitmap)", open);
        Assert.Contains("ShowPhotoDialog(bitmap, state.TurnId, uri, state.Phase)", open);
        Assert.Contains("ApplyQuizState(RequiredQuiz().DismissOverlay())", open);
    }

    [Theory]
    [InlineData(NativePhase.Ready)]
    [InlineData(NativePhase.Feedback)]
    public void UiModeChange_MidTurnKeepsRouteAndAnswerOrCorrection(
        NativePhase phase)
    {
        var activity = File.ReadAllText(Path.Combine(HeadRoot, "MainActivity.cs"));
        var shell = File.ReadAllText(Path.Combine(HeadRoot, "AndroidSliceShell.cs"));
        var composition = File.ReadAllText(Path.Combine(
            HeadRoot, "AndroidSliceShell.Compose.cs"));
        Assert.Contains("ConfigurationChanges = ConfigChanges.UiMode", activity);
        var callback = activity[activity.IndexOf(
            "public override void OnConfigurationChanged", StringComparison.Ordinal)..activity.IndexOf(
            "internal void UpdateBackHandling", StringComparison.Ordinal)];
        Assert.Contains("base.OnConfigurationChanged(newConfig)", callback);
        Assert.Contains("_shell?.RefreshForUiMode()", callback);
        Assert.DoesNotContain("new AndroidSliceShell", callback);
        var refresh = shell[shell.IndexOf("internal void RefreshForUiMode", StringComparison.Ordinal)
            ..shell.IndexOf("public void Start()", StringComparison.Ordinal)];
        Assert.Contains("_revision.Value =", refresh);
        Assert.DoesNotContain("Destination =", refresh);
        Assert.DoesNotContain("Answer.Value =", refresh);
        Assert.DoesNotContain("Correction.Value =", refresh);
        Assert.Contains("MaterialTheme.IsSystemInDarkTheme(composer)", composition);
        Assert.False(AndroidQuizInputPolicy.ShouldClearForNewTurn(
            "same-turn", new NativeQuizState(
                phase, NativeDestination.VocabularyQuiz, TurnId: "same-turn")));
    }

    [Fact]
    public void QuizPrompt_PreservesReferenceDisplayHierarchyInLightAndDark()
    {
        var source = File.ReadAllText(Path.Combine(
            HeadRoot, "..", "SentenceStudio.UI", "Pages", "VocabQuiz.razor"));
        var theme = File.ReadAllText(Path.Combine(HeadRoot, "AndroidVisualTheme.cs"))
            + File.ReadAllText(Path.Combine(HeadRoot, "AndroidThemeTokens.cs"));
        var visual = File.ReadAllText(Path.Combine(
            HeadRoot, "AndroidSliceShell.Visual.cs"));
        var composition = File.ReadAllText(Path.Combine(
            HeadRoot, "AndroidSliceShell.Compose.cs"));
        Assert.Contains("ss-display mb-2\" style=\"color: var(--bs-primary);\"",
            source);
        // The prompt colour is --bs-primary for the selected theme and mode, read from
        // the shared web colour table (decision 8b).
        Assert.Contains("NativeThemeColorTable.For(id, dark)", theme);
        Assert.Contains("colors.Primary,", theme);
        Assert.Contains("Color.FromHex(tokens.Primary)", theme);
        Assert.Contains("internal const int QuizPromptSize = 42", theme);
        Assert.Contains("Color = palette.Primary", visual);
        Assert.Contains("FontWeight = FontWeight.Bold", visual);
        Assert.Contains("Align = TextAlign.Center", visual);
        var quiz = File.ReadAllText(Path.Combine(
            HeadRoot, "AndroidSliceShell.Quiz.Compose.cs"));
        Assert.Contains("viewport.Add(QuizPromptHeading(question, palette))", composition);
        Assert.Contains("RenderQuizPromptActions(state, prompt, palette)", composition);
        Assert.Contains("RenderQuizThumbnail(state, prompt, palette)", composition);
        Assert.Contains("native.quiz.tools", quiz);
        Assert.Contains("\"native.quiz.audio\"", quiz);
        Assert.Contains("prompt.ShowPhoto && prompt.CanPlayPromptAudio", composition);
        Assert.Contains("VocabQuiz_ListenLookAnswer", composition);
        Assert.Contains("VocabQuiz_LookAndAnswer", composition);
        Assert.Contains("new Image(painter, photoLabel)", quiz);
        Assert.Contains(".Clickable(OpenPhoto)", quiz);
    }

    [Fact]
    public void FullscreenPhotoUsesComposeImageGesturesAndAccessibleClose()
    {
        var viewer = File.ReadAllText(Path.Combine(
            HeadRoot, "AndroidQuizPhotoDialog.cs"));
        var shell = File.ReadAllText(Path.Combine(
            HeadRoot, "AndroidSliceShell.cs"));
        var composition = File.ReadAllText(Path.Combine(
            HeadRoot, "AndroidSliceShell.Compose.cs"));
        Assert.Contains("new BoxWithConstraints(", viewer);
        Assert.Contains("Modifier.FillMaxSize().ClipToBounds()", viewer);
        Assert.Contains("Modifier.MatchParentSize()", viewer);
        Assert.Contains("Modifier.FillMaxSize().SafeDrawingPadding().Padding(16)",
            viewer);
        Assert.Contains("native.quiz.photo.backdrop", viewer);
        Assert.Contains(".DetectTapGestures(", viewer);
        Assert.Contains(".DetectTransformGestures(", viewer);
        Assert.Contains(".GraphicsLayer(", viewer);
        Assert.Contains("_transform.ContainsImage(", viewer);
        Assert.Contains("new Image(_painter, _imageLabel)", viewer);
        Assert.Contains("new IconButton(onClick:", viewer);
        Assert.Contains("native.quiz.photo.viewer", viewer);
        Assert.Contains("native.quiz.photo.image", viewer);
        Assert.Contains("native.quiz.photo.close", viewer);
        Assert.Contains(".SystemBarsPadding().Padding(16)", viewer);
        Assert.Contains(".FocusRequester(_closeFocus)", viewer);
        Assert.Contains("semantics.ContentDescription(_title)", viewer);
        Assert.Contains("semantics.ContentDescription(_closeLabel)", viewer);
        Assert.DoesNotContain("using Android.Widget", viewer);
        Assert.DoesNotContain("new Dialog(", viewer);
        var backdropTap = viewer[viewer.IndexOf(
            "onTap: location =>", StringComparison.Ordinal)..viewer.IndexOf(
            "onDoubleTap: location =>", StringComparison.Ordinal)];
        Assert.Contains("!_transform.ContainsImage(location.X, location.Y)",
            backdropTap);
        Assert.Contains("_onDismiss()", backdropTap);
        var closeButton = viewer[viewer.IndexOf(
            "var close = new IconButton(", StringComparison.Ordinal)..viewer.IndexOf(
            "topBar.Add(close)", StringComparison.Ordinal)];
        Assert.Contains("_onDismiss()", closeButton);
        Assert.Contains("Localize(NativeText.QuizPhoto)", shell);
        Assert.Contains("theme.Add(photoViewer.Render())", composition);
        Assert.Contains("photoViewer.FocusClose()", composition);
        Assert.Contains("AndroidQuizPhotoPresentationPolicy.CanPresent(",
            composition);
        Assert.Contains("_photoDialog?.Dispose()", shell);
        Assert.Contains("QueuePhotoFocusRestore()", shell);
        var dismiss = shell[shell.IndexOf(
            "private void OnPhotoDismissed(", StringComparison.Ordinal)..shell.IndexOf(
            "public void PlayPrompt()", StringComparison.Ordinal)];
        Assert.Contains("ApplyQuizState(_quiz.DismissOverlay())", dismiss);
        Assert.Contains("QueuePhotoFocusRestore()", dismiss);
        Assert.Contains("private void ClosePhotoDialogWithoutRestore()", dismiss);
        Assert.Contains("composer.LaunchedEffect(restoreVersion", composition);
        Assert.Contains(".FocusRequester(_photoFocus!)", File.ReadAllText(
            Path.Combine(HeadRoot, "AndroidSliceShell.Quiz.Compose.cs")));
    }

    [Fact]
    public void ActivityRoutesBackEscapeAndDestructionWithoutHostingWidgetPresentation()
    {
        var activity = File.ReadAllText(Path.Combine(
            HeadRoot, "MainActivity.cs"));
        var shell = File.ReadAllText(Path.Combine(
            HeadRoot, "AndroidSliceShell.cs"));
        var viewer = File.ReadAllText(Path.Combine(
            HeadRoot, "AndroidQuizPhotoDialog.cs"));
        Assert.Contains("this.SetContent(_shell.Build)", activity);
        Assert.Contains("activity._shell?.HandleBack()", activity);
        Assert.Contains("e?.KeyCode == Keycode.Escape", activity);
        Assert.Contains("e.Action == KeyEventActions.Down", activity);
        Assert.Contains("_shell?.HandlePhotoEscape()", activity);
        Assert.Contains("base.DispatchKeyEvent(e)", activity);
        Assert.Contains("_shell?.Dispose()", activity);
        // Back closes the photo viewer or the details sheet before leaving the quiz.
        Assert.Contains("&& Quiz.Overlay != NativeQuizOverlay.None)", shell);
        Assert.Contains("Quiz.Overlay != NativeQuizOverlay.FullscreenPhoto", shell);
        Assert.Contains("viewer?.Dispose()", shell);
        Assert.Contains("_lifetime.Cancel()", shell);
        Assert.Contains("if (_disposed)", viewer);
        Assert.Contains("key: \"photo-tap\"", viewer);
        Assert.Contains("key: \"photo-transform\"", viewer);
        Assert.DoesNotContain("SetContentView(", activity);

        foreach (var file in Directory.EnumerateFiles(HeadRoot, "*.cs"))
        {
            var source = File.ReadAllText(file);
            foreach (var widget in new[]
            {
                "using Android.Widget", "new Dialog(", "new FrameLayout(",
                "new ImageView(", "new TextView(", "new EditText(",
                "new AlertDialog.Builder(", ".SetContentView("
            })
                Assert.DoesNotContain(widget, source);
        }
    }

    [Theory]
    [InlineData("Debug")]
    [InlineData("Release")]
    public void ManifestEnforcesApi36AndConfigurationSpecificHttp(string configuration)
    {
        var manifest = XDocument.Load(Path.Combine(
            HeadRoot, "Properties", $"AndroidManifest.{configuration}.xml"));
        XNamespace android = "http://schemas.android.com/apk/res/android";
        Assert.Equal("36",
            (string?)manifest.Descendants("uses-sdk").Single().Attribute(android + "minSdkVersion"));
        Assert.Contains(manifest.Descendants("queries").Descendants("action"),
            action => (string?)action.Attribute(android + "name")
                == "android.intent.action.TTS_SERVICE");
        var application = manifest.Descendants("application").Single();
        if (configuration == "Release")
        {
            Assert.Equal("false",
                (string?)application.Attribute(android + "usesCleartextTraffic"));
            Assert.Null(application.Attribute(android + "networkSecurityConfig"));
        }
        else
        {
            Assert.Equal("@xml/native_network_security_config",
                (string?)application.Attribute(android + "networkSecurityConfig"));
            var rules = XDocument.Load(Path.Combine(
                HeadRoot, "Resources", "xml", "native_network_security_config.xml"));
            Assert.Equal("false",
                (string?)rules.Descendants("base-config").Single()
                    .Attribute("cleartextTrafficPermitted"));
            var exception = Assert.Single(rules.Descendants("domain-config"));
            Assert.Equal("true",
                (string?)exception.Attribute("cleartextTrafficPermitted"));
            Assert.Equal("10.0.2.2",
                Assert.Single(exception.Elements("domain")).Value);
            var project = XDocument.Load(Path.Combine(
                HeadRoot, "SentenceStudio.Native.Android.csproj"));
            Assert.Contains(project.Descendants("AndroidResource"), resource =>
                (string?)resource.Attribute("Remove")
                    == "Resources/xml/native_network_security_config.xml"
                && (string?)resource.Attribute("Condition")
                    == "'$(Configuration)' == 'Release'");
        }
    }

    [Fact]
    public void Release_KeepsComposeMembersTheFacadeResolvesThroughJni()
    {
        // R8 removed these JNI targets in Release: the TextFieldValue
        // constructor and FontWeight.SemiBold getter crashed startup; the
        // others are reached by the theme, ScrollToAsync, LaunchedEffect
        // failure, image, pointer, and icon paths.
        var project = XDocument.Load(Path.Combine(
            HeadRoot, "SentenceStudio.Native.Android.csproj"));
        Assert.Contains(project.Descendants("ProguardConfiguration"), item =>
            (string?)item.Attribute("Include") == "proguard-compose-jni.cfg"
            && item.Attribute("Condition") is null);
        var rules = File.ReadAllText(Path.Combine(HeadRoot, "proguard-compose-jni.cfg"));
        Assert.Contains("class androidx.compose.ui.text.input.TextFieldValue", rules);
        Assert.Contains("<init>(java.lang.String, long, androidx.compose.ui.text.TextRange, int, "
            + "kotlin.jvm.internal.DefaultConstructorMarker);", rules);
        var colors = string.Join(", ", Enumerable.Repeat("long", 48));
        foreach (var scheme in new[] { "lightColorScheme", "darkColorScheme" })
            Assert.Contains($"{scheme}-_VG5OTI$default({colors}, int, int, java.lang.Object);",
                rules);
        Assert.Contains("public kotlin.coroutines.CoroutineContext getMain();", rules);
        Assert.Contains("public static java.lang.Object createFailure(java.lang.Throwable);",
            rules);
        foreach (var member in new[]
        {
            "public androidx.compose.ui.text.font.FontWeight getSemiBold();",
            "public androidx.compose.ui.text.font.FontWeight getBold();",
            "public androidx.compose.ui.layout.ContentScale getFit();",
            "public int getCenter-e0LSkKk();",
            "public final long unbox-impl();",
            "public static androidx.compose.material.icons.Icons$Filled INSTANCE;",
            "public static androidx.compose.material.icons.Icons$AutoMirrored$Filled INSTANCE;",
        })
            Assert.Contains(member, rules);
    }
}
