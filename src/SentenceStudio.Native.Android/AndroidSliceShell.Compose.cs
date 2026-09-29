using System.Globalization;
using AndroidX.Compose;
using AndroidX.Compose.Runtime;
using AndroidX.Compose.UI.Text.Input;
using SentenceStudio.Common;
using SentenceStudio.Contracts.Plans;
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
        _dashboardScroll = composer.Remember(() => new ComposeScrollState());
        _quizScroll = composer.Remember(() => new ComposeScrollState());
        var dark = MaterialTheme.IsSystemInDarkTheme(composer);
        var palette = AndroidVisualTheme.For(dark);
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
            NativeDestination.Dashboard or NativeDestination.TodaysPlan => RenderDashboard(palette),
            NativeDestination.VocabularyQuiz or NativeDestination.QuizSummary => RenderQuiz(composer, palette),
            NativeDestination.Onboarding => RenderOnboardingNotice(palette),
            _ => RenderSignIn(palette),
        });
        if (!string.IsNullOrWhiteSpace(Error.Value)
            && Destination is not (NativeDestination.Dashboard
                or NativeDestination.TodaysPlan))
            content.Add(StatusAlert(Error.Value, palette, error: true,
                automationId: "native.shell.error"));
        var surface = new Surface
        {
            Modifier = Modifier.FillMaxSize(),
            Color = palette.Background,
            ContentColor = palette.Text,
        };
        surface.Add(content);
        var theme = new MaterialTheme
        {
            Dark = dark,
            ColorScheme = AndroidVisualTheme.ColorScheme(dark),
            UseDynamicColor = false,
        };
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
            fields.Add(Body(Localize("Common_Loading"), palette, secondary: true));
        fields.AddRange(
        [
            new OutlinedTextField(Email, singleLine: true)
            {
                Modifier = Modifier.FillMaxWidth().TestTag(NativeAccess.Email)
                    .Semantics(semantics => semantics.ContentDescription(Localize(NativeText.Email))),
                Label = new Text(Localize(NativeText.Email)),
                KeyboardOptions = emailKeyboard,
            },
            new Row(Arrangement.SpacedBy(8), Alignment.Vertical.CenterVertically)
            {
                Modifier.FillMaxWidth(),
                new OutlinedTextField(Password, singleLine: true)
                {
                    Modifier = Modifier.Weight(1f).TestTag(NativeAccess.Password)
                        .Semantics(semantics => semantics.ContentDescription(
                            Localize(NativeText.Password))),
                    Label = new Text(Localize(NativeText.Password)),
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
                SignIn, !Busy.Value && state.Phase != NativePhase.Loading
                    && _auth is not null
                    && !string.IsNullOrWhiteSpace(Email.Value)
                    && !string.IsNullOrWhiteSpace(Password.Value)),
        ]);
        if (state.Phase == NativePhase.PendingSync)
            fields.Add(Action(Localize(NativeText.Retry), "native.auth.retry",
                RetryAuth, !Busy.Value, primary: false));
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

    private ComposableNode RenderOnboardingNotice(AndroidVisualPalette palette) =>
        new Column
        {
            Modifier.FillMaxWidth().Padding(AndroidVisualTheme.PagePadding)
                .TestTag("native.auth.onboarding"),
            Card(palette, null,
                Body("Complete your account setup in the WebApp before using the native app.", palette),
                Action(Localize("Nav_Logout"), "native.auth.signout",
                    SignOut, !Busy.Value, primary: false)),
        };

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
        viewport.Add(SelectedPlanMode(palette));
        if (state.HasPendingSync)
            viewport.Add(StatusAlert("Local changes have not synced yet.", palette));
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
            if (state.Narrative is { } narrative)
                viewport.Add(StudyInsight(narrative, palette));
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
        else if (state.Phase is NativePhase.Error or NativePhase.Blocked)
            viewport.Add(StatusAlert(Localize(state.ErrorKey ?? NativeText.PlanError),
                palette, error: true, automationId: "native.dashboard.status"));
        else
            viewport.Add(Body(Localize("Common_Loading"), palette, secondary: true,
                modifier: Modifier.TestTag("native.dashboard.status")));

        if (state.VocabSummary is { } vocabulary)
            AddVocabularySummary(viewport, vocabulary, palette);

        return new Column
        {
            Modifier.FillMaxSize().TestTag("native.dashboard.screen"),
            DashboardAppBar(palette),
            viewport,
        };
    }

    private ComposableNode SelectedPlanMode(AndroidVisualPalette palette)
    {
        var surface = new Surface
        {
            Modifier = Modifier.FillMaxWidth().Padding(bottom: 12)
                .TestTag("native.dashboard.mode.today")
                .Semantics(semantics =>
                {
                    semantics.ContentDescription(Localize(NativeText.Today));
                    semantics.Selected(true);
                }),
            Shape = Shape.RoundedCorners(AndroidVisualTheme.ControlRadius),
            Color = palette.Primary,
            ContentColor = palette.OnPrimary,
        };
        surface.Add(new Row(Arrangement.SpacedBy(8),
            Alignment.Vertical.CenterVertically)
        {
            Modifier.FillMaxWidth().HeightIn(min: AndroidVisualTheme.ControlHeight)
                .Padding(horizontal: 16),
            new Icon(Icons.Default.DateRange, Localize(NativeText.Today))
            {
                Modifier = Modifier.Size(20),
                Tint = palette.OnPrimary,
            },
            new Text(Localize(NativeText.Today))
            {
                Color = palette.OnPrimary,
                FontSize = new Sp(16),
                FontWeight = FontWeight.Medium,
            },
        });
        return surface;
    }

    private ComposableNode StudyInsight(
        PlanNarrativeDto narrative, AndroidVisualPalette palette)
    {
        var expanded = _insightExpanded;
        var label = Localize("Dashboard_TodaysInsight");
        var header = new Row(Arrangement.SpaceBetween,
            Alignment.Vertical.CenterVertically)
        {
            Modifier.FillMaxWidth().HeightIn(min: AndroidVisualTheme.ControlHeight)
                .TestTag("native.plan.insight.toggle")
                .Clickable(() => Publish(() => _insightExpanded = !_insightExpanded))
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
                var struggling = vocab.StrugglingCategories
                    .Where(tag => tag.TotalAttempts > 0 && tag.AverageAccuracy < 0.6f)
                    .Select(tag => $"{tag.Tag} ({tag.AverageAccuracy:P0})").ToArray();
                if (struggling.Length > 0)
                    content.Add(Body($"{Localize("Dashboard_StrugglingWith")} " +
                        string.Join(", ", struggling), palette, secondary: true));
                if (!string.IsNullOrWhiteSpace(vocab.PatternInsight))
                    content.Add(StatusAlert(vocab.PatternInsight, palette));
                if (vocab.SampleStrugglingWords.Count > 0)
                    content.Add(Body(string.Format(CultureInfo.CurrentCulture,
                        Localize(struggling.Length > 0
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
        return Card(palette, "native.plan.insight", content.ToArray());
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
        int count, string labelKey, string name, Color numberColor,
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
        if (state.Overlay != NativeQuizOverlay.None)
            viewport.Add(RenderOverlay(state, palette));
        else if (state.Phase == NativePhase.ResumeChoice)
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
                viewport.Add(Card(palette, id,
                    Heading(result.TargetLanguageTerm, palette, 20,
                        Modifier.TestTag($"{id}.word")),
                    Body(result.NativeLanguageTerm, palette,
                        modifier: Modifier.TestTag($"{id}.meaning")),
                    Body(Localize(result.WasCorrect
                            ? NativeText.QuizCorrect : NativeText.QuizIncorrect),
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
            && state.Destination != NativeDestination.QuizSummary
            && state.Overlay == NativeQuizOverlay.None)
            page.Add(RenderQuizFooter(state, palette));
        return page;
    }

    private void RenderQuestion(IComposer composer, Column viewport,
        NativeQuizState state, NativeQuizPrompt prompt, AndroidVisualPalette palette)
    {
        var choices = state.Choices.IsDefault ? [] : state.Choices;
        var isQuestion = state.Phase == NativePhase.Ready;
        var question = prompt.ShowWrittenPrompt && !string.IsNullOrEmpty(prompt.PromptText)
            ? prompt.PromptText
            : Localize("VocabQuiz_ListenAndAnswer");
        viewport.Add(QuizPromptHeading(question, palette));
        if (prompt.ShowPhoto)
        {
            viewport.Add(StatusAlert(Localize(NativeText.QuizUnavailable),
                palette, error: true));
            return;
        }
        if (prompt.CanPlayPromptAudio)
            viewport.Add(Action(Localize("VocabQuiz_ListenAndAnswer"),
                "native.quiz.audio", PlayPrompt, isQuestion && !Busy.Value,
                primary: false));

        if (state.Phase == NativePhase.Feedback)
        {
            if (!string.IsNullOrWhiteSpace(state.CorrectAnswerAfterGrading))
                viewport.Add(Heading(state.CorrectAnswerAfterGrading, palette, 22));
            viewport.Add(StatusAlert(Localize(
                state.FeedbackKey ?? NativeText.QuizIncorrect), palette,
                error: state.AnswerWasCorrect == false,
                success: state.AnswerWasCorrect == true,
                automationId: NativeAccess.QuizFeedback));
        }
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
        else if (state.Mode == "Text" && isQuestion)
        {
            viewport.Add(Body(Localize("VocabQuiz_TypeTranslation"), palette, secondary: true));
            viewport.Add(new OutlinedTextField(Answer, singleLine: true)
            {
                Modifier = Modifier.FillMaxWidth().TestTag(NativeAccess.QuizAnswer)
                    .Semantics(semantics => semantics.ContentDescription(
                        Localize(NativeText.AnswerRequired))),
                Label = new Text(Localize(NativeText.AnswerRequired)),
            });
            viewport.Add(Action(Localize("VocabQuiz_SubmitAnswer"),
                "native.quiz.answer.submit", SubmitTextAnswer,
                state.CanSubmit && !Busy.Value && !string.IsNullOrWhiteSpace(Answer.Value)));
        }
        if (state.RequiresCorrection && state.Phase == NativePhase.Feedback)
        {
            viewport.Add(new OutlinedTextField(Correction, singleLine: true)
            {
                Modifier = Modifier.FillMaxWidth().TestTag("native.quiz.correction")
                    .Semantics(semantics => semantics.ContentDescription(
                        Localize(NativeText.CorrectionRequired))),
                Label = new Text(Localize(NativeText.CorrectionRequired)),
            });
            viewport.Add(Action(Localize("VocabQuiz_SubmitAnswer"),
                "native.quiz.correction.submit", SubmitCorrection,
                !Busy.Value && !string.IsNullOrWhiteSpace(Correction.Value)));
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

    private ComposableNode RenderOverlay(
        NativeQuizState state, AndroidVisualPalette palette) =>
        new Column
        {
            Modifier.FillMaxSize().TestTag(state.Overlay == NativeQuizOverlay.FullscreenPhoto
                ? NativeAccess.QuizPhoto : NativeAccess.QuizDetails),
            StatusAlert(Localize(NativeText.QuizUnavailable), palette, error: true),
            Action(Localize("Common_Close"), "native.quiz.overlay.close",
                DismissOverlay, !Busy.Value, primary: false),
        };

    private void HandleBackAction() => HandleBack();

    private static string Localize(string key) =>
        LocalizationManager.Instance.GetString(key);
}
