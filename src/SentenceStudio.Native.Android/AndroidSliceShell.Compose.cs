using System.Globalization;
using AndroidX.Compose;
using AndroidX.Compose.Runtime;
using AndroidX.Compose.UI.Graphics;
using AndroidX.Compose.UI.Graphics.Painter;
using AndroidX.Compose.UI.Text.Input;
using SentenceStudio.Common;
using SentenceStudio.Contracts.Theme;
using SentenceStudio.Native;
using SentenceStudio.Services.Plans;
using SentenceStudio.Services.Progress;
using SentenceStudio.Shared.Models;
using ComposeScrollState = AndroidX.Compose.ScrollState;
namespace SentenceStudio.Native.Android;

internal sealed partial class AndroidSliceShell
{
    public ComposableNode Build(IComposer composer)
    {
        _ = _revision.Value;
        _signInScroll = composer.Remember(() => new ComposeScrollState());
        _onboardingScroll = composer.Remember(() => new ComposeScrollState());
        _dashboardScroll = composer.Remember(() => new ComposeScrollState());
        _quizScroll = composer.Remember(() => new ComposeScrollState());
        _detailsScroll = composer.Remember(() => new ComposeScrollState());
        _settingsScroll = composer.Remember(() => new ComposeScrollState());
        var onboardingKey = Destination == NativeDestination.Onboarding
            ? _onboardingStep + 1 : 0;
        composer.LaunchedEffect(onboardingKey, async cancellationToken =>
        {
            if (onboardingKey > 0)
                await _onboardingScroll.ScrollToAsync(0, cancellationToken);
        });
        var detailsKey = Quiz.Overlay == NativeQuizOverlay.LearningDetails
            ? Quiz.TurnId : null;
        composer.LaunchedEffect(detailsKey, async cancellationToken =>
        {
            if (detailsKey is not null)
                await _detailsScroll.ScrollToAsync(0, cancellationToken);
        });
        (string Owner, string Turn, string Uri)? feedbackPhotoLoad = null;
        if (!_preview && _media is not null
            && HasDashboardOwner(Auth.UserId)
            && AndroidQuizFeedbackPhotoPolicy.CanLoad(Quiz, Auth.UserId))
            feedbackPhotoLoad = (Auth.UserId!, Quiz.TurnId!,
                Quiz.FeedbackPhotoCandidateUri!);
        composer.LaunchedEffect(feedbackPhotoLoad, async cancellationToken =>
        {
            if (feedbackPhotoLoad is not { } candidate || _media is not { } media
                || !await media.CanShowPhotoAsync(candidate.Uri, cancellationToken))
                return;
            Post(() =>
            {
                if (HasDashboardOwner(Auth.UserId)
                    && AndroidQuizFeedbackPhotoPolicy.CanLoad(Quiz, Auth.UserId)
                    && Auth.UserId == candidate.Owner
                    && Quiz.TurnId == candidate.Turn
                    && Quiz.FeedbackPhotoCandidateUri == candidate.Uri
                    && media.TryGetPhoto(candidate.Uri, out _))
                {
                    _feedbackPhotoOwner = candidate.Owner;
                    _feedbackPhotoTurnId = candidate.Turn;
                    _feedbackPhotoUri = candidate.Uri;
                    _revision.Value = unchecked(_revision.Value + 1);
                }
            });
        });
        _photoFocus = composer.Remember(() => new FocusRequester());
        var restoreVersion = _photoFocusRestoreVersion;
        composer.LaunchedEffect(restoreVersion, _ =>
        {
            if (restoreVersion > 0 && restoreVersion == _photoFocusRestoreVersion
                && Destination == NativeDestination.VocabularyQuiz
                && HasDashboardOwner(Auth.UserId)
                && Quiz.Phase == NativePhase.Ready
                && Quiz.Prompt?.ShowPhoto == true)
                _photoFocus.RequestFocus();
            return Task.CompletedTask;
        });
        var photoViewer = Destination == NativeDestination.VocabularyQuiz
            && _photoDialog is { } viewer
            && HasDashboardOwner(Auth.UserId)
            && AndroidQuizPhotoPresentationPolicy.CanPresent(
                Quiz, Auth.UserId, viewer.OwnerId, viewer.TurnId,
                viewer.PhotoUri)
            ? viewer : null;
        composer.LaunchedEffect(photoViewer, _ =>
        {
            if (photoViewer is not null
                && ReferenceEquals(_photoDialog, photoViewer)
                && AndroidQuizPhotoPresentationPolicy.CanPresent(
                    Quiz, Auth.UserId, photoViewer.OwnerId,
                    photoViewer.TurnId, photoViewer.PhotoUri))
                photoViewer.FocusClose();
            return Task.CompletedTask;
        });
        var systemIsDark = MaterialTheme.IsSystemInDarkTheme(composer);
        var dark = CurrentAppearance.Mode.ResolveEffective(systemIsDark)
            == ThemeMode.Dark;
        var palette = AndroidVisualTheme.For(dark);
        var theme = new MaterialTheme
        {
            Dark = dark,
            ColorScheme = AndroidVisualTheme.ColorScheme(dark),
            UseDynamicColor = false,
        };
        if (photoViewer is not null)
        {
            theme.Add(photoViewer.Render());
            return theme;
        }
        var content = new Column
        {
            Modifier.FillMaxSize().SystemBarsPadding().ImePadding()
                .Semantics(semantics => semantics.TestTagsAsResourceId(true)),
        };
#if DEBUG
        if (_preview && Destination is not (NativeDestination.Dashboard
            or NativeDestination.TodaysPlan))
            content.Add(StatusAlert("Debug preview - isolated sample; not signed in.",
                palette, automationId: "native.debug.preview.banner"));
#endif
        content.Add(Destination switch
        {
            NativeDestination.Dashboard or NativeDestination.TodaysPlan
                when _appearanceSettingsOpen => RenderAppearanceSettings(palette),
            NativeDestination.Dashboard or NativeDestination.TodaysPlan => RenderDashboard(palette),
            NativeDestination.VocabularyQuiz or NativeDestination.QuizSummary => RenderQuiz(composer, palette),
            NativeDestination.Onboarding => RenderOnboarding(palette),
            _ => RenderAuthentication(palette),
        });
        if (!string.IsNullOrWhiteSpace(Error.Value)
            && _accountEntry == AccountEntry.SignIn
            && Destination is not (NativeDestination.Dashboard
                or NativeDestination.TodaysPlan or NativeDestination.Onboarding))
            content.Add(StatusAlert(Error.Value, palette, error: true,
                automationId: "native.shell.error"));
        var surface = new Surface
        {
            Modifier = Modifier.FillMaxSize(),
            Color = palette.Background,
            ContentColor = palette.Text,
        };
        surface.Add(content);
        theme.Add(surface);
        return theme;
    }

    private ComposableNode RenderSignIn(AndroidVisualPalette palette)
    {
        var state = Auth;
        var keyboard = KeyboardOptionsCompanion.Default;
        var emailKeyboard = keyboard.Copy(
            keyboard.Capitalization, keyboard.AutoCorrectEnabled,
            AndroidX.Compose.KeyboardType.Email,
            AndroidX.Compose.ImeAction.Next, keyboard.PlatformImeOptions,
            keyboard.ShowKeyboardOnFocus, keyboard.HintLocales);
        var passwordKeyboard = keyboard.Copy(
            keyboard.Capitalization, keyboard.AutoCorrectEnabled,
            AndroidX.Compose.KeyboardType.Password,
            AndroidX.Compose.ImeAction.Done, keyboard.PlatformImeOptions,
            keyboard.ShowKeyboardOnFocus, keyboard.HintLocales);

        var signInHeading = Heading(Localize(NativeText.SignIn), palette, 22,
            Modifier.FillMaxWidth());
        signInHeading.Align = TextAlign.Center;
        var fields = new List<ComposableNode> { signInHeading };
        if (state.Phase is NativePhase.PendingSync or NativePhase.Error or NativePhase.Blocked)
            fields.Add(StatusAlert(Localize(state.ErrorKey ?? NativeText.UnexpectedError),
                palette, error: true, automationId: NativeAccess.AuthError));
        if (state.Phase == NativePhase.Loading)
        {
            fields.Add(new CircularProgressIndicator
            {
                Modifier = Modifier.Size(28).TestTag("native.auth.signin.progress")
            });
            fields.Add(Body(Localize("Common_Loading"), palette, secondary: true));
        }
        fields.AddRange(
        [
            new OutlinedTextField(
                Email, enabled: !Busy.Value
                    && state.Phase is not (NativePhase.Loading or NativePhase.Blocked),
                singleLine: true)
            {
                Modifier = Modifier.FillMaxWidth().TestTag(NativeAccess.Email)
                    .Semantics(semantics => semantics.ContentDescription(Localize(NativeText.Email))),
                Label = new Text(Localize(NativeText.Email)),
                Placeholder = new Text(Localize("Common_EmailPlaceholder")),
                KeyboardOptions = emailKeyboard,
            },
            new Row(Arrangement.SpacedBy(8), Alignment.Vertical.CenterVertically)
            {
                Modifier.FillMaxWidth(),
                new OutlinedTextField(
                    Password,
                    enabled: !Busy.Value
                        && state.Phase is not (NativePhase.Loading or NativePhase.Blocked),
                    singleLine: true)
                {
                    Modifier = Modifier.Weight(1f).TestTag(NativeAccess.Password)
                        .Semantics(semantics => semantics.ContentDescription(
                            Localize(NativeText.Password))),
                    Label = new Text(Localize(NativeText.Password)),
                    Placeholder = new Text(Localize("Common_PasswordPlaceholder")),
                    VisualTransformation = ShowPassword.Value
                        ? AndroidX.Compose.UI.Text.Input.VisualTransformation.Companion.None
                        : new PasswordVisualTransformation('\u2022'),
                    KeyboardOptions = passwordKeyboard,
                    KeyboardActions = KeyboardActionsHelper.Create(
                        onDone: () =>
                        {
                            if (_auth is not null && !Busy.Value)
                                SignIn();
                        }),
                },
                new TextButton(
                    onClick: () => ShowPassword.Value = !ShowPassword.Value,
                    enabled: !Busy.Value)
                {
                    Modifier.HeightIn(min: AndroidVisualTheme.ControlHeight)
                        .WidthIn(min: 72)
                        .TestTag(NativeAccess.PasswordVisibility)
                        .Semantics(semantics => semantics.ContentDescription(
                            Localize(ShowPassword.Value
                                ? NativeText.HidePassword : NativeText.ShowPassword))),
                    new Text(Localize(ShowPassword.Value
                        ? NativeText.HidePasswordShort : NativeText.ShowPasswordShort)),
                },
            },
            new Row(Arrangement.SpacedBy(8), Alignment.Vertical.CenterVertically)
            {
                Modifier.FillMaxWidth(),
                new Checkbox(@checked: Remember.Value,
                    onCheckedChange: value => Remember.Value = value,
                    enabled: !Busy.Value)
                {
                    Modifier = Modifier.TestTag(NativeAccess.RememberMe)
                        .Semantics(semantics => semantics.ContentDescription(
                            Localize(NativeText.RememberMe))),
                },
                Body(Localize(NativeText.RememberMe), palette),
            },
            Action(Localize(NativeText.SignIn), NativeAccess.SignIn,
                SignIn, !Busy.Value
                    && state.Phase is not (NativePhase.Loading or NativePhase.Blocked)
                    && _auth is not null
                    && !string.IsNullOrWhiteSpace(Email.Value)
                    && !string.IsNullOrWhiteSpace(Password.Value)),
        ]);
        if (state.Phase == NativePhase.PendingSync)
            fields.Add(Action(Localize(NativeText.Retry), "native.auth.retry",
                RetryAuth, !Busy.Value, primary: false));
        fields.Add(Action(Localize("Login_ForgotPassword"),
            "native.auth.forgot.open", OpenRecovery,
            !Busy.Value && state.Phase != NativePhase.Blocked,
            primary: false));
        fields.Add(Action($"{Localize("Login_NoAccountCreateOne")} " +
            Localize("Login_CreateOne"), "native.auth.register.open",
            OpenRegistration, !Busy.Value && state.Phase != NativePhase.Blocked,
            primary: false));
        var viewport = new Column(Arrangement.SpacedBy(18))
        {
            Modifier = Modifier.FillMaxWidth()
                .VerticalScroll(_signInScroll)
                .Padding(horizontal: AndroidVisualTheme.PagePadding, vertical: 24)
        };
        var brand = Heading("SentenceStudio", palette, 28,
            Modifier.FillMaxWidth().Padding(bottom: 8));
        brand.Color = palette.Primary;
        brand.Align = TextAlign.Center;
        viewport.Add(brand);
        viewport.Add(Card(palette, null, fields.ToArray()));
#if DEBUG
        viewport.Add(Action("Debug preview (isolated sample, no API)",
            "native.debug.preview", OpenDebugPreview, !Busy.Value,
            primary: false));
#endif
        return new Column(Arrangement.Center)
        {
            Modifier.FillMaxWidth().Weight(1f).TestTag("native.auth.screen"),
            viewport,
        };
    }

    private ComposableNode RenderDashboard(AndroidVisualPalette palette)
    {
        var state = Dashboard;
        var cards = state.Cards.IsDefault ? [] : state.Cards;
        var viewport = new Column(Arrangement.SpacedBy(8))
        {
            Modifier = Modifier.FillMaxWidth().Weight(1f)
                .VerticalScroll(_dashboardScroll)
                .Padding(horizontal: AndroidVisualTheme.PagePadding, vertical: 16)
                .TestTag(NativeAccess.Plan),
        };
#if DEBUG
        if (_preview)
            viewport.Add(StatusAlert("Debug preview - isolated sample; not signed in.",
                palette, automationId: "native.debug.preview.banner"));
#endif
        if (!string.IsNullOrWhiteSpace(Error.Value))
            viewport.Add(StatusAlert(Error.Value, palette, error: true,
                automationId: "native.shell.error"));
        if (!_preview && state.IsNewUser)
        {
            viewport.Add(RenderNewLearner(palette));
            return new Column
            {
                Modifier.FillMaxSize().TestTag("native.dashboard.screen"),
                DashboardAppBar(palette),
                viewport,
            };
        }
        viewport.Add(SelectedPlanMode());
        var chooseMode = !_preview
            && state.Mode == NativeDashboardMode.ChooseOwn
            && (state.Phase is NativePhase.Ready or NativePhase.Empty)
            && HasDashboardOwner(Auth.UserId);
        if (state.HasPendingSync)
            viewport.Add(StatusAlert(
                $"{Localize("DebugHealth_CoreSync")}: " +
                Localize("DebugHealth_Pending"), palette));
        if (chooseMode)
            viewport.Add(RenderChooseOwn(palette));
        else
        {
            if (state.CurrentStreak > 0)
            {
                var streak = string.Format(CultureInfo.CurrentCulture,
                    Localize("Dashboard_DayStreak"), state.CurrentStreak);
                if (state.LongestStreak > state.CurrentStreak)
                    streak += "  " + string.Format(CultureInfo.CurrentCulture,
                        Localize("Dashboard_BestStreak"), state.LongestStreak);
                viewport.Add(Card(palette, null, Body(streak, palette)));
            }

            if (state.Phase == NativePhase.Ready)
            {
                var counts = Body(string.Format(CultureInfo.CurrentCulture,
                    Localize("Dashboard_ProgressCounts"),
                    state.CompletedCount, state.TotalCount,
                    state.MinutesSpent, state.EstimatedTotalMinutes), palette, secondary: true);
                counts.FontSize = new Sp(14);
                viewport.Add(Card(palette, "native.plan.progress",
                    Heading(Localize("Dashboard_TodaysProgress"), palette, 17),
                    counts,
                    ProgressBar((float)(state.CompletionPercentage / 100),
                        palette, completed: true)));
                if (state.Narrative is not null)
                    viewport.Add(StudyInsight(state, palette));
                else if (!string.IsNullOrWhiteSpace(state.Rationale))
                {
                    var rationale = new List<ComposableNode>();
                    var resourceNames = cards.Select(card => card.ResourceTitle)
                        .Where(name => !string.IsNullOrWhiteSpace(name))
                        .Distinct(StringComparer.Ordinal).ToArray();
                    if (resourceNames.Length > 0)
                        rationale.Add(Body(string.Join(", ", resourceNames),
                            palette, secondary: true));
                    rationale.Add(Body(state.Rationale, palette, secondary: true));
                    viewport.Add(Card(palette, "native.plan.rationale",
                        rationale.ToArray()));
                }
                foreach (var card in cards)
                    viewport.Add(RenderPlanCard(card, palette));
            }
            else if (state.Phase == NativePhase.Empty)
            {
                viewport.Add(Card(palette, "native.dashboard.status",
                    Body(Localize(state.ErrorKey ?? NativeText.NoPlan), palette),
                    Action(Localize("Dashboard_GeneratePlan"),
                        "native.dashboard.generate", GeneratePlan, !Busy.Value)));
            }
            else if (state.Phase == NativePhase.PendingSync)
            {
                viewport.Add(Card(palette, "native.dashboard.status",
                    StatusAlert(Localize(NativeText.ConnectionError),
                        palette, error: true),
                    Action(Localize(NativeText.Retry),
                        "native.dashboard.sync.retry",
                        RefreshPlan, !Busy.Value)));
            }
            else if (state.Phase is NativePhase.Error or NativePhase.Blocked)
                viewport.Add(StatusAlert(Localize(state.ErrorKey ?? NativeText.PlanError),
                    palette, error: true, automationId: "native.dashboard.status"));
            else
            {
                viewport.Add(new CircularProgressIndicator
                {
                    Modifier = Modifier.Size(28).TestTag("native.dashboard.loading.progress")
                });
                viewport.Add(Body(Localize("Common_Loading"), palette, secondary: true,
                    modifier: Modifier.TestTag("native.dashboard.status")));
            }
        }

        if (state.VocabSummary is { } vocabulary)
            AddVocabularySummary(viewport, vocabulary, palette);
        else
        {
            viewport.Add(Heading(Localize("Dashboard_VocabularyHeader"), palette, 22,
                Modifier.FillMaxWidth().Padding(vertical: 12)
                    .TestTag("native.dashboard.vocab.title")));
            viewport.Add(Card(palette, "native.dashboard.vocab.empty",
                Body(Localize(state.Phase == NativePhase.Loading
                    ? "Common_Loading" : "Dashboard_NoVocabData"), palette)));
        }

        var numbers = state.NumberContexts.IsDefault ? [] : state.NumberContexts;
        if (numbers.Length > 0 || state.NumberErrorKey is not null)
        {
            viewport.Add(Heading(
                $"{Localize("Activity_NumberDrill")} — {Localize("VocabQuiz_Mastered")}",
                palette, 22,
                Modifier.FillMaxWidth().Padding(vertical: 12)
                    .TestTag("native.dashboard.numbers.title")));
            if (state.NumberErrorKey is { } numberError)
                viewport.Add(StatusAlert(Localize(numberError), palette,
                    error: true, automationId: "native.dashboard.numbers.error"));
            else
                foreach (var number in numbers)
                    viewport.Add(NumberMasteryCard(number, palette));
        }

        var page = new Column
        {
            Modifier.FillMaxSize().TestTag("native.dashboard.screen"),
            DashboardAppBar(palette),
            viewport,
        };
        if (_chooseSelector.Value != (int)ChooseSelector.None && chooseMode)
            page.Add(RenderChooseSelector(palette));
        return page;
    }

    private static ComposableNode RenderNewLearner(AndroidVisualPalette palette) =>
        new Column(Arrangement.SpacedBy(12))
        {
            Modifier.FillMaxWidth().TestTag("native.dashboard.new-user"),
            Heading(Localize("Dashboard_NewUserTitle"), palette, 24),
            Body(Localize("Dashboard_NewUserDescription"), palette,
                secondary: true),
            Card(palette, "native.dashboard.new-user.quick-start",
                Heading(Localize("Dashboard_QuickStart"), palette, 18),
                Body(Localize("Dashboard_QuickStartDescription"), palette,
                    secondary: true),
                StatusAlert(Localize("NativeResources_Unavailable"), palette,
                    error: true, automationId: "native.dashboard.starter.unavailable")),
            Card(palette, "native.dashboard.new-user.create-own",
                Heading(Localize("Dashboard_CreateYourOwn"), palette, 18),
                Body(Localize("Dashboard_CreateYourOwnDescription"), palette,
                    secondary: true),
                StatusAlert(Localize("NativeResources_Unavailable"), palette,
                    error: true, automationId: "native.dashboard.create-own.unavailable"))
        };

    private static ComposableNode NumberMasteryCard(
        NativeNumberContextStat number, AndroidVisualPalette palette)
    {
        var title = number.ContextCode;
        var percent = number.MasteryPercentage;
        return Card(palette, $"native.dashboard.numbers.{title}",
            Heading(title, palette, 17),
            Body($"{number.DueCount} {Localize("DueNow")}", palette,
                secondary: true),
            ProgressBar(percent / 100f, palette),
            Body($"{number.CorrectCount}/{number.TotalCount} " +
                Localize(NativeText.QuizCorrect), palette, secondary: true));
    }

    private ComposableNode SelectedPlanMode()
    {
        var hasOwner = !_preview && HasDashboardOwner(Auth.UserId);
        var choose = hasOwner
            && Dashboard.Mode == NativeDashboardMode.ChooseOwn;
        var row = new Row(Arrangement.SpacedBy(8))
        {
            Modifier.FillMaxWidth().Padding(bottom: 12),
        };
        row.Add(PlanModeOption(Localize(NativeText.Today),
            "native.dashboard.mode.today", !choose,
            () => SelectDashboardMode(false), hasOwner));
        row.Add(PlanModeOption(Localize("Dashboard_ChooseMyOwn"),
            "native.dashboard.mode.choose", choose,
            () => SelectDashboardMode(true), hasOwner));
        return row;
    }

    private ComposableNode PlanModeOption(
        string label, string tag, bool selected,
        Action change, bool enabled)
    {
        var modifier = Modifier.Weight(1f)
            .HeightIn(min: AndroidVisualTheme.ControlHeight)
            .TestTag(tag)
            .Semantics(semantics =>
            {
                semantics.ContentDescription(label);
                semantics.Selected(selected);
            });
        if (selected)
        {
            var button = new AndroidX.Compose.Button(
                change, enabled: enabled && !Busy.Value)
            {
                Modifier = modifier,
            };
            button.Add(new Text(label));
            return button;
        }
        var outlined = new OutlinedButton(change,
            enabled: enabled && !Busy.Value)
        {
            Modifier = modifier
        };
        outlined.Add(new Text(label));
        return outlined;
    }

    private ComposableNode StudyInsight(
        NativeDashboardState state, AndroidVisualPalette palette)
    {
        var narrative = state.Narrative
            ?? throw new InvalidOperationException("Study insight requires a loaded narrative.");
        var expanded = _insightExpanded;
        var label = Localize("Dashboard_TodaysInsight");
        var header = new Row(Arrangement.SpaceBetween,
            Alignment.Vertical.CenterVertically)
        {
            Modifier.FillMaxWidth().HeightIn(min: AndroidVisualTheme.ControlHeight)
                .TestTag("native.plan.insight.toggle")
                .Clickable(() => Publish(() => _insightExpanded = !_insightExpanded))
                .Padding(horizontal: AndroidVisualTheme.CardPadding,
                    vertical: AndroidVisualTheme.CardPadding / 2)
                .Semantics(semantics =>
                {
                    semantics.ContentDescription(label);
                    semantics.StateDescription(Localize(expanded
                        ? "Dashboard_InsightExpanded" : "Dashboard_InsightCollapsed"));
                    semantics.Role(SemanticsRole.Button);
                }),
            new Row(Arrangement.SpacedBy(10),
                Alignment.Vertical.CenterVertically)
            {
                new Icon(Icons.Default.Info, label)
                {
                    Modifier = Modifier.Size(20),
                    Tint = palette.Primary,
                },
                Body(label, palette),
            },
            new Icon(expanded
                    ? Icons.Default.KeyboardArrowUp : Icons.Default.KeyboardArrowDown,
                Localize(expanded
                    ? "Dashboard_InsightExpanded" : "Dashboard_InsightCollapsed"))
            {
                Modifier = Modifier.Size(20).TestTag("native.plan.insight.chevron"),
                Tint = palette.Text,
            },
        };
        var content = new List<ComposableNode> { header };
        if (expanded)
        {
            if (!string.IsNullOrWhiteSpace(narrative.Story))
                content.Add(Body(narrative.Story, palette,
                    modifier: Modifier.TestTag("native.plan.insight.story")));
            foreach (var resource in narrative.Resources)
            {
                if (string.IsNullOrWhiteSpace(resource.Title))
                    continue;
                var resourceHeading = new Row(Arrangement.SpacedBy(8),
                    Alignment.Vertical.CenterVertically)
                {
                    Modifier.FillMaxWidth(),
                    new Icon(resource.MediaType.Contains("Video",
                            StringComparison.OrdinalIgnoreCase)
                            ? Icons.Default.PlayArrow : Icons.Default.Info,
                        resource.MediaType)
                    {
                        Modifier = Modifier.Size(20),
                        Tint = palette.Primary,
                    },
                    Heading(resource.Title, palette, 16),
                };
                content.Add(resourceHeading);
                content.Add(Body(Localize("NativeResources_Unavailable"),
                    palette, secondary: true,
                    modifier: Modifier.TestTag(
                        $"native.plan.insight.resource.{resource.Id}.unavailable")));
                if (!string.IsNullOrWhiteSpace(resource.SelectionReason))
                    content.Add(Body(resource.SelectionReason, palette, secondary: true));
            }
            if (narrative.VocabInsight is { } vocab)
            {
                content.Add(Body(string.Join("    ",
                [
                    $"{vocab.NewCount} {Localize("Dashboard_InsightNew")}",
                    $"{vocab.ReviewCount} {Localize("Dashboard_InsightReview")}",
                    $"{vocab.TotalDue} {Localize("Dashboard_InsightTotalDue")}",
                ]), palette, modifier: Modifier.TestTag("native.plan.insight.vocab")));
                if (vocab.AverageMastery > 0)
                    content.Add(Body(string.Format(CultureInfo.CurrentCulture,
                        Localize("Dashboard_InsightMastery"),
                        (vocab.AverageMastery * 100).ToString("F0",
                            CultureInfo.CurrentCulture)), palette));
                if (!state.StrugglingCategories.IsDefaultOrEmpty)
                    content.Add(CategoryInsight(
                        Localize("Dashboard_StrugglingWith"),
                        state.StrugglingCategories.Select(tag =>
                            $"{tag.Tag} ({tag.AverageAccuracy:P0})"),
                        palette, "struggling"));
                if (!state.NewCategories.IsDefaultOrEmpty)
                    content.Add(CategoryInsight(Localize("Dashboard_NewToYou"),
                        state.NewCategories.Select(tag => tag.Tag), palette, "new"));
                if (!state.DoingWellCategories.IsDefaultOrEmpty)
                    content.Add(CategoryInsight(
                        null,
                        state.DoingWellCategories.Select(tag =>
                            $"{tag.Tag} ({tag.AverageAccuracy:P0})"),
                        palette, "doing-well"));
                if (!string.IsNullOrWhiteSpace(vocab.PatternInsight))
                    content.Add(StatusAlert(vocab.PatternInsight, palette));
                if (vocab.SampleStrugglingWords.Count > 0)
                    content.Add(Body(string.Format(CultureInfo.CurrentCulture,
                        Localize(!state.StrugglingCategories.IsDefaultOrEmpty
                            ? "Dashboard_WordsToReview" : "Dashboard_WordsToDiscover"),
                        string.Join(", ", vocab.SampleStrugglingWords)),
                        palette, secondary: true));
            }
            if (narrative.FocusAreas.Count > 0)
            {
                content.Add(Heading(Localize("Dashboard_FocusAreas"), palette, 16));
                foreach (var focus in narrative.FocusAreas)
                    content.Add(Body($"\u2022 {focus}", palette));
            }
        }
        var surface = new Surface
        {
            Modifier = Modifier.FillMaxWidth().Padding(bottom: 12)
                .TestTag("native.plan.insight"),
            Shape = Shape.RoundedCorners(AndroidVisualTheme.CardRadius),
            Color = palette.Card
        };
        var cardContents = new Column(Arrangement.SpacedBy(8))
        {
            Modifier.FillMaxWidth()
        };
        cardContents.Add(header);
        if (expanded)
        {
            var detail = new Column(Arrangement.SpacedBy(8))
            {
                Modifier.FillMaxWidth()
                    .Padding(horizontal: AndroidVisualTheme.CardPadding,
                        vertical: AndroidVisualTheme.CardPadding / 2)
            };
            foreach (var child in content.Skip(1))
                detail.Add(child);
            cardContents.Add(detail);
        }
        surface.Add(cardContents);
        return surface;
    }

    private static ComposableNode CategoryInsight(
        string? heading, IEnumerable<string> categories,
        AndroidVisualPalette palette, string tier)
    {
        var column = new Column(Arrangement.SpacedBy(3))
        {
            Modifier.FillMaxWidth().TestTag($"native.plan.insight.category.{tier}"),
        };
        if (!string.IsNullOrWhiteSpace(heading))
            column.Add(Body(heading, palette, secondary: true));
        column.Add(Body(string.Join(", ", categories), palette));
        return column;
    }

    private void AddVocabularySummary(
        Column viewport, VocabProgressSummary summary, AndroidVisualPalette palette)
    {
        viewport.Add(Heading(Localize("Dashboard_VocabularyHeader"), palette, 22,
            Modifier.FillMaxWidth().Padding(vertical: 12)
                .TestTag("native.dashboard.vocab.title")));
        viewport.Add(new Row(Arrangement.SpacedBy(12))
        {
            Modifier.FillMaxWidth(),
            VocabularyTile(summary.New, "Dashboard_VocabNew", "new",
                palette.Primary, palette),
            VocabularyTile(summary.Learning, "Dashboard_VocabLearning", "learning",
                palette.WarningText, palette),
        });
        viewport.Add(new Row(Arrangement.SpacedBy(12))
        {
            Modifier.FillMaxWidth(),
            VocabularyTile(summary.Familiar, "Dashboard_VocabFamiliar", "familiar",
                palette.Primary, palette),
            VocabularyTile(summary.Review, "Dashboard_VocabReview", "review",
                palette.WarningText, palette),
        });
        viewport.Add(new Row(Arrangement.SpacedBy(12))
        {
            Modifier.FillMaxWidth(),
            VocabularyTile(summary.Known, "Dashboard_VocabKnown", "known",
                palette.KnownText, palette),
            new Box { Modifier.Weight(1f) },
        });
        var total = summary.New + summary.Learning + summary.Familiar
            + summary.Review + summary.Known;
        if (total > 0)
            viewport.Add(Card(palette, "native.dashboard.vocab.summary",
                Body($"{Localize("Dashboard_TotalWordsLabel")} " +
                    total.ToString(CultureInfo.CurrentCulture), palette),
                Body($"{Localize("Dashboard_WeeklyAccuracyLabel")} " +
                    $"{Math.Round(summary.SuccessRate7d * 100).ToString(CultureInfo.CurrentCulture)}%",
                    palette, secondary: true)));
    }

    private ComposableNode VocabularyTile(
        int count, string labelKey, string name, AndroidX.Compose.Color numberColor,
        AndroidVisualPalette palette)
    {
        var label = Localize(labelKey);
        var number = Heading(count.ToString(CultureInfo.CurrentCulture), palette, 24);
        number.Color = numberColor;
        number.Align = TextAlign.Center;
        var caption = Body(label, palette, secondary: true);
        caption.Align = TextAlign.Center;
        var tile = new Surface
        {
            Modifier = Modifier.Weight(1f).HeightIn(min: 88)
                .TestTag($"native.dashboard.vocab.stat.{name}")
                .Semantics(semantics =>
                    semantics.ContentDescription($"{count} {label}")),
            Shape = Shape.RoundedCorners(AndroidVisualTheme.CardRadius),
            Color = palette.Card,
        };
        tile.Add(new Column(Arrangement.SpacedBy(4),
            Alignment.Horizontal.CenterHorizontally)
        {
            Modifier.FillMaxWidth().Padding(12),
            number,
            caption,
        });
        return tile;
    }

    private ComposableNode RenderPlanCard(
        NativePlanCard card, AndroidVisualPalette palette)
    {
        var title = card.TitleKey is null ? card.Title : Localize(card.TitleKey);
        var minutes = card.MinutesSpent > 0
            ? string.Format(CultureInfo.CurrentCulture, Localize("Dashboard_SpentOfEstimate"),
                card.MinutesSpent, card.EstimatedMinutes)
            : string.Format(CultureInfo.CurrentCulture, Localize("Dashboard_EstimateMinutes"),
                card.EstimatedMinutes);
        var metadata = card.ResourceTitle is { Length: > 0 }
            ? $"{card.ResourceTitle} \u00b7 {minutes}" : minutes;
        if (card.VocabDueCount > 0)
            metadata += " \u00b7 " + string.Format(CultureInfo.CurrentCulture,
                Localize("Dashboard_VocabDueCount"), card.VocabDueCount.Value);
        var label = card.IsCompleted
            ? $"{title}. {Localize("PlanSummary_Completed")}. {metadata}"
            : $"{title}. {metadata}";
        var canOpen = card.Activity == PlanActivityType.VocabularyReview
            && !card.IsCompleted;
        if (!canOpen)
            label += $". {Localize("NativeResources_Unavailable")}";
        var modifier = Modifier.FillMaxWidth().Padding(bottom: 8)
            .TestTag($"{card.AccessibilityId}.{card.Id}");
        if (canOpen && !Busy.Value)
            modifier = modifier.Clickable(() => OpenQuiz(card.Id))
                .Semantics(semantics =>
                {
                    semantics.ContentDescription(label);
                    semantics.Role(SemanticsRole.Button);
                });
        else
            modifier = modifier.Semantics(semantics => semantics.ContentDescription(label));

        var icon = card.IsCompleted ? Icons.Default.CheckCircle
            : card.Activity switch
            {
                PlanActivityType.VocabularyReview => Icons.Default.Check,
                PlanActivityType.Listening or PlanActivityType.Shadowing
                    or PlanActivityType.VideoWatching => Icons.Default.PlayArrow,
                PlanActivityType.Writing or PlanActivityType.Translation => Icons.Default.Edit,
                PlanActivityType.Reading => Icons.Default.Info,
                _ => Icons.Default.DateRange,
            };
        var details = new Column(Arrangement.SpacedBy(3))
        {
            Modifier.Weight(1f),
            Heading(title, palette, 17),
            Body(metadata, palette, secondary: true),
        };
        if (card.IsCompleted)
            details.Add(Body(Localize("PlanSummary_Completed"), palette));
        else if (card.MinutesSpent > 0 && card.EstimatedMinutes > 0)
            details.Add(ProgressBar(
                (float)card.MinutesSpent / card.EstimatedMinutes, palette));
        if (!canOpen)
            details.Add(Body(Localize("NativeResources_Unavailable"), palette,
                secondary: true,
                modifier: Modifier.TestTag(
                    $"{card.AccessibilityId}.{card.Id}.unavailable")));
        var content = new Row(Arrangement.SpacedBy(12),
            Alignment.Vertical.CenterVertically)
        {
            Modifier.FillMaxWidth().HeightIn(min: 76)
                .Padding(AndroidVisualTheme.CardPadding)
                .Alpha(card.IsCompleted ? 0.75f : 1f),
            new Icon(icon, title)
            {
                Modifier = Modifier.Size(24),
                Tint = card.IsCompleted ? palette.Success : palette.Primary,
            },
            details,
        };
        if (canOpen)
            content.Add(new Icon(Icons.AutoMirrored.Default.KeyboardArrowRight, title)
            {
                Modifier = Modifier.Size(20),
                Tint = palette.SecondaryText,
            });
        var surface = new Surface
        {
            Modifier = modifier,
            Shape = Shape.RoundedCorners(AndroidVisualTheme.CardRadius),
            Color = palette.Card,
        };
        surface.Add(content);
        return surface;
    }

    private ComposableNode RenderQuiz(IComposer composer, AndroidVisualPalette palette)
    {
        var state = Quiz;
        var viewport = new Column(Arrangement.SpacedBy(14))
        {
            Modifier = Modifier.FillMaxWidth().Weight(1f)
                .VerticalScroll(_quizScroll)
                .Padding(horizontal: AndroidVisualTheme.PagePadding, vertical: 16)
                .TestTag("native.quiz.screen"),
        };
        if (state.Phase == NativePhase.ResumeChoice)
        {
            var resume = new List<ComposableNode>
            {
                Heading(Localize("VocabQuiz_ResumePrompt"), palette, 22),
            };
            if (state.ErrorKey is not null)
                resume.Add(StatusAlert(Localize(state.ErrorKey), palette, error: true));
            else
                resume.Add(Action(Localize(NativeText.Resume),
                    "native.quiz.resume.continue", ResumeQuiz, !Busy.Value));
            resume.Add(Action(Localize(NativeText.StartFresh),
                "native.quiz.resume.fresh", StartFreshQuiz, !Busy.Value,
                primary: false));
            viewport.Add(Card(palette, NativeAccess.QuizResume, resume.ToArray()));
        }
        else if (state.Destination == NativeDestination.QuizSummary
            || state.Phase == NativePhase.Summary)
        {
            var stats = new Row(Arrangement.SpaceBetween)
            {
                Modifier.FillMaxWidth(),
                SummaryStatistic(state.CorrectCount,
                    Localize(NativeText.QuizCorrect), palette),
                SummaryStatistic(state.TotalTurns,
                    Localize("VocabQuiz_Total"), palette),
                SummaryStatistic(state.WordsMastered,
                    Localize("VocabQuiz_Mastered"), palette),
                SummaryStatistic(state.RoundsCompleted,
                    Localize("VocabQuiz_Rounds"), palette),
            };
            viewport.Add(Card(palette, NativeAccess.QuizSummary,
                Heading(Localize(NativeText.QuizSummary), palette, 24),
                stats));
            var results = state.Results.IsDefault ? [] : state.Results;
            for (var index = 0; index < results.Length; index++)
            {
                var result = results[index];
                var id = $"{NativeAccess.QuizSummaryResult}.{index}";
                var statusKey = result.WasCorrect
                    ? result.ReadyToRotateOut
                        ? "VocabQuiz_Mastered" : NativeText.QuizCorrect
                    : NativeText.QuizIncorrect;
                viewport.Add(Card(palette, id,
                    Heading(result.TargetLanguageTerm, palette, 20,
                        Modifier.TestTag($"{id}.word")),
                    Body(result.NativeLanguageTerm, palette,
                        modifier: Modifier.TestTag($"{id}.meaning")),
                    Body(Localize(statusKey),
                        palette, modifier: Modifier.TestTag($"{id}.status"))));
            }
            if (state.ErrorKey is not null)
                viewport.Add(StatusAlert(Localize(state.ErrorKey), palette, error: true));
            if (state.Phase == NativePhase.Summary)
                viewport.Add(Action(Localize("VocabQuiz_ContinueQuiz"),
                    "native.quiz.summary.continue", ContinueRound, !Busy.Value));
            viewport.Add(Action(Localize(NativeText.Done), "native.quiz.summary.done",
                DoneQuiz, !Busy.Value, primary: false));
            if (state.Phase == NativePhase.Error)
                viewport.Add(Action(Localize(NativeText.Retry), "native.quiz.retry",
                    RetryQuizSave, !Busy.Value));
        }
        else if (state.Prompt is { } prompt)
            RenderQuestion(composer, viewport, state, prompt, palette);
        else
        {
            if (state.Phase == NativePhase.Loading)
                viewport.Add(new CircularProgressIndicator
                {
                    Modifier = Modifier.Size(28).TestTag("native.quiz.loading.progress")
                });
            viewport.Add(StatusAlert(state.Phase == NativePhase.Loading
                ? Localize("Common_Loading")
                : Localize(state.ErrorKey ?? NativeText.QuizUnavailable),
                palette, error: state.Phase is NativePhase.Error or NativePhase.Blocked,
                automationId: "native.quiz.status"));
            if (state.Phase == NativePhase.Error)
                viewport.Add(Action(Localize(NativeText.Retry), "native.quiz.retry",
                    RetryQuizSave, !Busy.Value));
        }

        var page = new Column
        {
            Modifier.FillMaxSize().TestTag("native.quiz.page"),
            ScreenHeader(Localize(NativeText.Quiz), palette, back: true),
            viewport,
        };
        if (state.Prompt is not null
            && state.Destination != NativeDestination.QuizSummary)
            page.Add(RenderQuizFooter(state, palette));
        if (state.Overlay == NativeQuizOverlay.LearningDetails
            && state.Details is { } details)
            page.Add(RenderLearningDetails(details, palette));
        return page;
    }

    private void RenderQuestion(IComposer composer, Column viewport,
        NativeQuizState state, NativeQuizPrompt prompt, AndroidVisualPalette palette)
    {
        var choices = state.Choices.IsDefault ? [] : state.Choices;
        var isQuestion = state.Phase == NativePhase.Ready;
        var question = prompt.ShowWrittenPrompt && !string.IsNullOrEmpty(prompt.PromptText)
            ? prompt.PromptText
            : prompt.ShowPhoto && prompt.CanPlayPromptAudio
                ? Localize("VocabQuiz_ListenLookAnswer")
                : prompt.ShowPhoto
                    ? Localize("VocabQuiz_LookAndAnswer")
                    : Localize("VocabQuiz_ListenAndAnswer");
        viewport.Add(QuizPromptHeading(question, palette));
        if (prompt.CanPlayPromptAudio)
        {
            var tools = new Row(Arrangement.Center)
            {
                Modifier.FillMaxWidth().TestTag("native.quiz.tools")
            };
            var play = Localize("VocabWordEdit_PlayAudio");
            var button = new IconButton(
                PlayPrompt, enabled: isQuestion && !Busy.Value)
            {
                Modifier = Modifier.HeightIn(min: AndroidVisualTheme.ControlHeight)
                    .WidthIn(min: AndroidVisualTheme.ControlHeight)
                    .TestTag("native.quiz.audio")
                    .Semantics(semantics => semantics.ContentDescription(play))
            };
            button.Add(new Icon(Icons.Default.PlayArrow, play)
            {
                Modifier = Modifier.Size(24),
                Tint = palette.Text
            });
            tools.Add(button);
            viewport.Add(tools);
        }
        if (prompt.ShowPhoto && prompt.PhotoUri is { } photoUri)
            viewport.Add(RenderQuizPhoto(photoUri, palette));
        else if (HasDashboardOwner(Auth.UserId)
            && AndroidQuizFeedbackPhotoPolicy.CanDisplay(
                state, Auth.UserId, _feedbackPhotoOwner,
                _feedbackPhotoTurnId, _feedbackPhotoUri)
            && _media?.TryGetPhoto(_feedbackPhotoUri, out _) == true)
            viewport.Add(RenderQuizPhoto(_feedbackPhotoUri!, palette,
                feedback: true));

        var answerSlot = new Column
        {
            Modifier.FillMaxWidth().HeightIn(min: 42)
                .TestTag("native.quiz.answer.reveal")
        };
        if (state.Phase == NativePhase.Feedback)
        {
            if (!string.IsNullOrWhiteSpace(state.CorrectAnswerAfterGrading))
                answerSlot.Add(Heading(state.CorrectAnswerAfterGrading, palette, 22));
        }
        viewport.Add(answerSlot);
        if (state.Phase == NativePhase.Feedback)
        {
            var feedback = Localize(state.FeedbackKey ?? NativeText.QuizIncorrect);
            if (state.AnswerWasCorrect == true
                && !string.IsNullOrWhiteSpace(state.CompleteForm))
                feedback += " " + string.Format(CultureInfo.CurrentCulture,
                    Localize("NativeVocabQuiz_FullForm"), state.CompleteForm);
            viewport.Add(StatusAlert(feedback, palette,
                error: state.AnswerWasCorrect == false,
                success: state.AnswerWasCorrect == true,
                automationId: NativeAccess.QuizFeedback));
            if (state.AnswerWasCorrect == true
                && !string.IsNullOrWhiteSpace(state.FeedbackMnemonicText))
                viewport.Add(StatusAlert(
                    $"{Localize("NativeVocabQuiz_Mnemonic")}: " +
                    state.FeedbackMnemonicText, palette,
                    automationId: "native.quiz.feedback.mnemonic"));
        }
        else
            viewport.Add(new Box
            {
                Modifier.FillMaxWidth().HeightIn(min: 56)
                    .TestTag("native.quiz.feedback.slot")
            });
        if (state.ErrorKey is not null)
            viewport.Add(StatusAlert(Localize(state.ErrorKey), palette,
                error: true, automationId: "native.quiz.error"));

        if (state.Mode == "MultipleChoice")
        {
            var optionList = new Column(Arrangement.SpacedBy(8))
            {
                Modifier = Modifier.FillMaxWidth().TestTag("native.quiz.choices"),
            };
            for (var index = 0; index < choices.Length; index++)
            {
                var choice = choices[index];
                optionList.Add(ChoiceAction(composer, palette, choice, index, state,
                    AnswerQuiz, isQuestion && state.CanSubmit && !Busy.Value));
            }
            viewport.Add(optionList);
        }
        else if (state.Mode == "Text")
        {
            var needsCorrection = state.RequiresCorrection
                && state.Phase == NativePhase.Feedback;
            var text = needsCorrection ? Correction : Answer;
            var label = Localize(needsCorrection
                ? NativeText.CorrectionRequired : NativeText.AnswerRequired);
            viewport.Add(Body(Localize("VocabQuiz_TypeTranslation"), palette, secondary: true));
            var keyboard = KeyboardOptionsCompanion.Default;
            viewport.Add(new OutlinedTextField(
                text, enabled: (isQuestion || needsCorrection) && !Busy.Value,
                singleLine: true)
            {
                Modifier = Modifier.FillMaxWidth()
                    .TestTag(needsCorrection ? "native.quiz.correction"
                        : NativeAccess.QuizAnswer)
                    .Semantics(semantics => semantics.ContentDescription(label)),
                Label = new Text(label),
                KeyboardOptions = keyboard.Copy(
                    keyboard.Capitalization, keyboard.AutoCorrectEnabled,
                    AndroidX.Compose.KeyboardType.Text,
                    AndroidX.Compose.ImeAction.Done,
                    keyboard.PlatformImeOptions, keyboard.ShowKeyboardOnFocus,
                    keyboard.HintLocales),
                KeyboardActions = KeyboardActionsHelper.Create(onDone: () =>
                {
                    if (Busy.Value)
                        return;
                    if (needsCorrection
                        && !string.IsNullOrWhiteSpace(Correction.Value.Text))
                        SubmitCorrection();
                    else if (isQuestion
                        && !string.IsNullOrWhiteSpace(Answer.Value.Text))
                        SubmitTextAnswer();
                })
            });
            if (isQuestion || needsCorrection)
                viewport.Add(Action(Localize("VocabQuiz_SubmitAnswer"),
                    needsCorrection ? "native.quiz.correction.submit"
                        : "native.quiz.answer.submit",
                    needsCorrection ? SubmitCorrection : SubmitTextAnswer,
                    !Busy.Value
                        && (needsCorrection
                            ? !string.IsNullOrWhiteSpace(Correction.Value.Text)
                            : state.CanSubmit
                                && !string.IsNullOrWhiteSpace(Answer.Value.Text))));
        }
        if (state.RequiresCorrection && state.Phase == NativePhase.Feedback)
        {
            viewport.Add(Action(Localize(NativeText.QuizOverride),
                "native.quiz.override", OverrideQuiz, !Busy.Value,
                primary: false));
        }
        else if (state.Phase == NativePhase.Feedback)
            viewport.Add(Action(Localize(NativeText.QuizNext), "native.quiz.next",
                NextQuestion, !Busy.Value));
        if (state.Phase == NativePhase.Error)
            viewport.Add(Action(Localize(NativeText.Retry), "native.quiz.retry",
                RetryQuizSave, !Busy.Value));
    }

    private ComposableNode RenderQuizPhoto(
        string photoUri, AndroidVisualPalette palette, bool feedback = false)
    {
        if (_media is null || !_media.TryGetPhoto(photoUri, out var bitmap)
            || bitmap is null)
            return StatusAlert(Localize(NativeText.QuizUnavailable),
                palette, error: true, automationId: "native.quiz.photo.error");

        var imageBitmap = AndroidImageBitmap_androidKt.AsImageBitmap(bitmap);
        var sourceSize = ((long)bitmap.Width << 32) | (uint)bitmap.Height;
        var painter = BitmapPainterKt.BitmapPainter(
            image: imageBitmap, srcOffset: 0L,
            srcSize: sourceSize, filterQuality: 1);
        var enlarge = Localize("VocabQuiz_EnlargePhoto");
        var interactive = !feedback && Quiz.Phase == NativePhase.Ready;
        var modifier = Modifier.FillMaxWidth().HeightIn(min: 160)
            .TestTag(feedback ? "native.quiz.photo.feedback.thumbnail"
                : "native.quiz.photo.thumbnail");
        modifier = interactive
            ? modifier.FocusRequester(_photoFocus!)
                .Clickable(OpenPhoto)
                .Semantics(semantics =>
                {
                    semantics.ContentDescription(enlarge);
                    semantics.Role(SemanticsRole.Button);
                })
            : modifier.Semantics(semantics =>
                semantics.ContentDescription(Localize(NativeText.QuizPhoto)));
        var photo = new Surface
        {
            Modifier = modifier,
            Shape = Shape.RoundedCorners(AndroidVisualTheme.ControlRadius),
            Color = palette.Elevated
        };
        photo.Add(new Image(painter, Localize(NativeText.QuizPhoto))
        {
            Modifier = Modifier.FillMaxWidth().Height(160),
            ContentScale = ContentScale.Fit
        });
        return photo;
    }

    private ComposableNode RenderQuizFooter(
        NativeQuizState state, AndroidVisualPalette palette)
    {
        var footer = new Column(Arrangement.SpacedBy(8))
        {
            Modifier = Modifier.FillMaxWidth().Background(palette.Background)
                .Border(1, palette.Border)
                .Padding(horizontal: AndroidVisualTheme.PagePadding, vertical: 12)
                .TestTag("native.quiz.footer"),
        };
        var row = new Row(Arrangement.SpaceBetween)
        {
            Modifier = Modifier.FillMaxWidth(),
        };
        row.Add(Body($"{state.CurrentTurn} / {state.TurnsInRound}", palette,
            secondary: true, modifier: Modifier.TestTag("native.quiz.progress")));
        row.Add(Body(string.Format(CultureInfo.CurrentCulture,
            Localize("VocabQuiz_CorrectCount"), state.CorrectCount),
            palette, modifier: Modifier.TestTag("native.quiz.correct-count")));
        footer.Add(row);
        footer.Add(ProgressBar(state.TurnsInRound == 0 ? 0f
            : (float)state.CurrentTurn / state.TurnsInRound, palette));
        return footer;
    }

    private ComposableNode RenderLearningDetails(
        NativeQuizLearningDetails details, AndroidVisualPalette palette)
    {
        var metrics = activity.Resources?.DisplayMetrics
            ?? throw new InvalidOperationException("Display metrics are unavailable.");
        var maxHeight = (int)Math.Round(
            metrics.HeightPixels / metrics.Density * 0.7f);
        var statusKey = details.Status switch
        {
            LearningStatus.Known => "VocabWordEdit_StatusKnown",
            LearningStatus.Learning => "VocabWordEdit_StatusLearning",
            LearningStatus.Familiar => "VocabWordEdit_StatusFamiliar",
            _ => "StatusUnknown"
        };
        var sheet = new ModalBottomSheet(onDismissRequest: DismissOverlay)
        {
            Modifier = Modifier.TestTag(NativeAccess.QuizDetails)
        };
        var content = new Column(Arrangement.SpacedBy(9))
        {
            Modifier = Modifier.FillMaxWidth().HeightIn(max: maxHeight)
                .VerticalScroll(_detailsScroll)
                .Padding(horizontal: AndroidVisualTheme.PagePadding,
                    vertical: AndroidVisualTheme.CardPadding)
        };
        content.Add(Heading(Localize("VocabQuiz_LearningDetails"), palette, 20));
        content.Add(DetailField(Localize("Status"), Localize(statusKey),
            palette, "status"));
        content.Add(DetailField(Localize("NativeVocabQuiz_Details_TotalAttempts"),
            details.TotalAttempts.ToString(CultureInfo.CurrentCulture),
            palette, "attempts"));
        content.Add(DetailField(Localize("NativeVocabQuiz_Details_CorrectAttempts"),
            details.CorrectAttempts.ToString(CultureInfo.CurrentCulture),
            palette, "correct"));
        content.Add(DetailField(Localize("Common_Accuracy"),
            details.Accuracy.ToString("P0", CultureInfo.CurrentCulture),
            palette, "accuracy"));
        content.Add(DetailField(Localize("NativeVocabQuiz_Details_CurrentStreak"),
            details.CurrentStreak.ToString("F1", CultureInfo.CurrentCulture),
            palette, "streak"));
        content.Add(DetailField(Localize("NativeVocabQuiz_Details_ProductionInStreak"),
            $"{details.ProductionInStreak.ToString(CultureInfo.CurrentCulture)} / 2",
            palette, "production"));
        content.Add(DetailField(Localize("NativeVocabQuiz_Details_EffectiveStreak"),
            details.EffectiveStreak.ToString("F1", CultureInfo.CurrentCulture),
            palette, "effective-streak"));
        content.Add(DetailField(Localize("NativeVocabQuiz_Details_MasteryScore"),
            details.MasteryScore.ToString("P0", CultureInfo.CurrentCulture),
            palette, "mastery"));
        content.Add(Heading(Localize("NativeVocabQuiz_Details_SrsSchedule"), palette, 16));
        content.Add(DetailField(Localize("VocabWordEdit_NextReviewLabel"),
            details.NextReviewDate?.ToString("MMM d, yyyy",
                CultureInfo.CurrentCulture) ?? Localize("NotScheduled"),
            palette, "next-review"));
        content.Add(DetailField(Localize("NativeVocabQuiz_Details_ReviewInterval"),
            string.Format(CultureInfo.CurrentCulture,
                Localize("NativeVocabQuiz_Details_Days"), details.ReviewInterval),
            palette, "review-interval"));
        content.Add(DetailField(Localize("NativeVocabQuiz_Details_EaseFactor"),
            details.EaseFactor.ToString("F2", CultureInfo.CurrentCulture),
            palette, "ease"));
        content.Add(DetailField(Localize("NativeVocabQuiz_Details_IsDueForReview"),
            Localize(details.IsDueForReview ? "VocabQuiz_Yes" : "VocabQuiz_No"),
            palette, "due"));
        content.Add(Heading(Localize("NativeVocabQuiz_Details_QuizContext"),
            palette, 16));
        content.Add(DetailField(Localize("NativeVocabQuiz_Details_QuizMode"),
            Localize(details.Mode == "Text"
                ? "NativeVocabQuiz_Details_TextMode"
                : "NativeVocabQuiz_Details_MultipleChoiceMode"),
            palette, "mode"));
        content.Add(DetailField(Localize("NativeVocabQuiz_Details_DueOnly"),
            Localize(details.DueOnly ? "VocabQuiz_Yes" : "VocabQuiz_No"),
            palette, "due-only"));
        content.Add(DetailField(Localize("NativeVocabQuiz_Details_WhyMode"),
            Localize(details.WhyModeKey), palette, "why-mode"));
        if (details.WhyModeKey == "NativeVocabQuiz_ModeFocusRecognition")
            content.Add(DetailField(Localize(
                    "NativeVocabQuiz_Details_RecognitionDemonstrations"),
                $"{details.RecognitionDemonstrations}/3", palette,
                "recognitions"));
        content.Add(Action(Localize("Common_Close"), "native.quiz.details.close",
            DismissOverlay, !Busy.Value, primary: false));
        sheet.Add(content);
        return sheet;
    }

    private static ComposableNode DetailField(
        string label, string value, AndroidVisualPalette palette, string tag) =>
        new Column(Arrangement.SpacedBy(2))
        {
            Modifier.FillMaxWidth().TestTag($"native.quiz.details.{tag}"),
            Body(label, palette, secondary: true),
            Body(value, palette)
        };

    private void HandleBackAction() => HandleBack();

    private static string Localize(string key) =>
        LocalizationManager.Instance.GetString(key);
}
