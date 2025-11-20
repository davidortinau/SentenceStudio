using SentenceStudio.Services.Progress;
using MauiReactor.Shapes;
using Microsoft.Extensions.Logging;

namespace SentenceStudio.Pages.Dashboard;

/// <summary>
/// Displays today's personalized learning plan with progress tracking and streak information.
/// Pedagogical design:
/// - Low cognitive load: Clear visual hierarchy, one task at a time
/// - Progress visibility: Shows completion percentage and streak
/// - Habit formation: Streak display with grace period encourages consistency
/// - Balanced practice: Algorithm ensures mix of input/output activities
/// - Sequential unlocking: Only first incomplete item is enabled to guide learners through optimal flow
/// </summary>
partial class TodaysPlanCard : MauiReactor.Component
{
    [Prop]
    TodaysPlan? _plan;

    [Prop]
    StreakInfo? _streakInfo;

    [Prop]
    Action<DailyPlanItem>? _onItemTapped;

    LocalizationManager _localize => LocalizationManager.Instance;
    ILogger<TodaysPlanCard> _logger => Services.GetRequiredService<ILogger<TodaysPlanCard>>();

    public override VisualNode Render()
    {
        if (_plan == null)
        {
            return ContentView().HeightRequest(0);
        }

        return Border(
            VStack(spacing: MyTheme.LayoutSpacing,
                // Header with title and streak
                RenderHeader(),

                // Resource and skill context
                RenderPlanContext(),

                // Progress summary with Start/Resume button
                RenderProgressSummary(),

                // Plan items list
                RenderPlanItems()
            )
            .Padding(MyTheme.Size160)
        )
        .BackgroundColor(MyTheme.CardBackground)
        .Stroke(MyTheme.CardBorder)
        .StrokeThickness(1)
        .StrokeShape(new RoundRectangle().CornerRadius(MyTheme.Size120));
    }

    VisualNode RenderHeader()
    {
        return HStack(spacing: MyTheme.ComponentSpacing,
            // Title
            Label($"{_localize["PlanCardTitle"]}")
                .ThemeKey(MyTheme.Title2),

            // Streak badge (if exists)
            _streakInfo != null && _streakInfo.CurrentStreak > 0
                ? Label($"🔥 {_streakInfo.CurrentStreak}")
                    .ThemeKey(MyTheme.Caption1Strong)
                    .TextColor(MyTheme.BadgeText)
                : null
        );
    }

    VisualNode RenderPlanContext()
    {
        // Show what resource(s) and skill this plan focuses on
        if (string.IsNullOrEmpty(_plan.ResourceTitles) && string.IsNullOrEmpty(_plan.SkillTitle))
            return null;

        return VStack(
            // Resource(s)
            !string.IsNullOrEmpty(_plan.ResourceTitles)
                ? Label($"📚 {_plan.ResourceTitles}")
                    .ThemeKey(MyTheme.Body1Strong)
                : null,

            // Skill
            !string.IsNullOrEmpty(_plan.SkillTitle)
                ? Label($"🎯 {_plan.SkillTitle}")
                    .ThemeKey(MyTheme.Body2)
                    .TextColor(MyTheme.SecondaryText)
                : null
        )
        .Spacing(MyTheme.MicroSpacing);
    }

    VisualNode RenderProgressSummary()
    {
        var completedCount = _plan.Items.Count(i => i.IsCompleted);
        var totalCount = _plan.Items.Count;
        var completionPercentage = (int)_plan.CompletionPercentage;
        var totalEstimatedMinutes = _plan.Items.Sum(i => i.EstimatedMinutes);
        var totalMinutesSpent = _plan.Items.Sum(i => i.MinutesSpent);

        // Find next activity to start/resume
        // CRITICAL: Check both IsCompleted flag AND time-based completion
        var nextItem = _plan.Items.FirstOrDefault(i => !IsItemComplete(i));
        var allComplete = nextItem == null;

        // Determine button text: "Resume" if ANY activity has progress, otherwise "Start"
        var hasAnyProgress = _plan.Items.Any(i => i.MinutesSpent > 0);
        var buttonText = hasAnyProgress
            ? $"{_localize["PlanResumeButton"] ?? "Resume"}"
            : $"{_localize["PlanStartButton"] ?? "Start"}";

        _logger.LogDebug("🎯 Button text logic: hasAnyProgress={HasProgress}, nextItem={NextItem}, allComplete={AllComplete}",
            hasAnyProgress, nextItem?.TitleKey ?? "null", allComplete);

        return VStack(spacing: MyTheme.MicroSpacing,
            ProgressBar().Progress(completionPercentage / 100.0)
                .HeightRequest(20)
                .ProgressColor(MyTheme.ProgressBarFill),

            // Stats row with Start/Resume button
            HStack(spacing: MyTheme.ComponentSpacing,
                Label($"{completionPercentage}% {_localize["PlanCompleteLabel"]} {totalMinutesSpent} / {totalEstimatedMinutes} {_localize["PlanMinutesLabel"]}")
                    .HStart()
                    .VCenter(),

                // Start/Resume button for next activity
                !allComplete
                    ? Button(buttonText)
                        .ThemeKey(MyTheme.Primary)
                        .HEnd()
                        .VCenter()
                        .OnClicked(() =>
                        {
                            _logger.LogDebug("🔘 Resume button clicked! nextItem={NextItem}", nextItem?.TitleKey ?? "NULL");
                            if (nextItem != null)
                            {
                                _logger.LogDebug("🔘 Invoking _onItemTapped with item: {TitleKey}", nextItem.TitleKey);
                                _onItemTapped?.Invoke(nextItem);
                                _logger.LogDebug("🔘 _onItemTapped invoked");
                            }
                            else
                            {
                                _logger.LogWarning("❌ Resume button: nextItem is NULL!");
                            }
                        })
                    : Label("✅ Complete!")
                        .ThemeKey(MyTheme.Caption1Strong)
                        .TextColor(MyTheme.ProgressBarFill)
                        .HEnd()
                        .VCenter()
            )
        );
    }

    VisualNode RenderPlanItems()
    {
        var items = new List<VisualNode>();
        var itemsList = _plan.Items.ToList();

        for (int i = 0; i < itemsList.Count; i++)
        {
            var item = itemsList[i];
            var isAvailable = i == 0 || IsItemComplete(itemsList[i - 1]);
            items.Add(RenderPlanItem(item, i + 1, isAvailable));
        }

        return VStack(spacing: MyTheme.Size120, items.ToArray());
    }

    VisualNode RenderPlanItem(DailyPlanItem item, int sequenceNumber, bool isAvailable)
    {
        var isCompleted = IsItemComplete(item);

        var isEnabled = isCompleted || isAvailable;

        _logger.LogDebug("🎯 RenderPlanItem '{TitleKey}': isCompleted={IsCompleted}, MinutesSpent={MinutesSpent}, EstimatedMinutes={EstimatedMinutes}",
            item.TitleKey, isCompleted, item.MinutesSpent, item.EstimatedMinutes);

        return Grid("*", "Auto,*",
            // Icon column - checkmark for completed, circle for todo
            Image()
                .Source(isCompleted ? MyTheme.IconCheckmarkCircleFilled : MyTheme.IconCircle)
                .WidthRequest(28)
                .HeightRequest(28)
                .VStart()
                .GridColumn(0)
                .Margin(0, 2, 0, 0),

            // Content column
            VStack(spacing: MyTheme.MicroSpacing,
                // Title
                Label(GetActivityTitle(item))
                    .ThemeKey(MyTheme.Body1Strong)
                    .HStart(),
                Label(GetActivityDescription(item))
                    .ThemeKey(MyTheme.Body2)
                    ,

                // Metadata row (time, vocab count if applicable)
                HStack(spacing: MyTheme.ComponentSpacing,
                    // Time estimate with actual progress
                    item.MinutesSpent > 0
                        ? Label($"⏱ {item.MinutesSpent}/{item.EstimatedMinutes}{_localize["PlanMinAbbrev"]}")
                            .ThemeKey(MyTheme.Caption1)
                            .FontAttributes(MauiControls.FontAttributes.Bold)
                        : Label($"⏱ {item.EstimatedMinutes}{_localize["PlanMinAbbrev"]}")
                            .ThemeKey(MyTheme.Caption1),

                    // Vocabulary count
                    item.ActivityType == PlanActivityType.VocabularyReview && item.VocabDueCount.HasValue && item.VocabDueCount.Value > 0
                        ? Label($"📝 {item.VocabDueCount.Value} {_localize["PlanWordsLabel"]}")
                            .ThemeKey(MyTheme.Caption1)
                        : null
                )
            )
            .Opacity(isEnabled ? 1.0 : 0.5)
            .GridColumn(1)
            .VCenter()
        )
        .ColumnSpacing(MyTheme.ComponentSpacing)
        .Padding(MyTheme.Size80, MyTheme.Size120)
        .OnTapped(() => _onItemTapped?.Invoke(item));
    }

    string GetActivityTitle(DailyPlanItem item)
    {
        // Use ActivityType enum directly instead of unreliable TitleKey string matching
        return item.ActivityType switch
        {
            PlanActivityType.VocabularyReview => $"{_localize["PlanItemVocabReviewTitle"] ?? "Vocabulary Review"}",
            PlanActivityType.Reading => $"{_localize["PlanItemReadingTitle"] ?? "Reading"}",
            PlanActivityType.Listening => $"{_localize["PlanItemListeningTitle"] ?? "Listening"}",
            PlanActivityType.VideoWatching => $"{_localize["PlanItemVideoTitle"] ?? "Video"}",
            PlanActivityType.Shadowing => $"{_localize["PlanItemShadowingTitle"] ?? "Shadowing"}",
            PlanActivityType.Cloze => $"{_localize["PlanItemClozeTitle"] ?? "Cloze"}",
            PlanActivityType.Translation => $"{_localize["PlanItemTranslationTitle"] ?? "Translation"}",
            PlanActivityType.Conversation => $"{_localize["PlanItemConversationTitle"] ?? "Conversation"}",
            PlanActivityType.VocabularyGame => $"{_localize["PlanItemVocabGameTitle"] ?? "Vocabulary Game"}",
            _ => item.ResourceTitle ?? "Practice"
        };
    }

    string GetActivityDescription(DailyPlanItem item)
    {
        // Build rich, contextual descriptions that tell learners exactly what they'll do
        var parts = new List<string>();

        // Start with activity-specific description with more detail
        var actionDescription = item.ActivityType switch
        {
            PlanActivityType.VocabularyReview when item.VocabDueCount.HasValue =>
                $"Review {item.VocabDueCount.Value} {(item.VocabDueCount.Value == 1 ? "word" : "words")} using spaced repetition flashcards. Test your recall and strengthen long-term memory.",
            PlanActivityType.VocabularyReview =>
                $"{_localize["PlanItemVocabReviewDesc"] ?? "Review words using spaced repetition flashcards to strengthen long-term memory"}",
            PlanActivityType.Reading =>
                $"{_localize["PlanItemReadingDesc"] ?? "Read and comprehend new text content. Click words for instant translations and save new vocabulary."}",
            PlanActivityType.Listening =>
                $"{_localize["PlanItemListeningDesc"] ?? "Listen to native audio and answer comprehension questions. Train your ear for natural speech patterns."}",
            PlanActivityType.VideoWatching =>
                $"{_localize["PlanItemVideoDesc"] ?? "Watch video with subtitles. Pause to study new words and practice listening comprehension."}",
            PlanActivityType.Shadowing =>
                $"{_localize["PlanItemShadowingDesc"] ?? "Listen and repeat each sentence immediately after the speaker. Improve pronunciation, rhythm, and fluency."}",
            PlanActivityType.Cloze =>
                $"{_localize["PlanItemClozeDesc"] ?? "Fill in missing words in sentences. Practice grammar patterns and vocabulary in context."}",
            PlanActivityType.Translation =>
                $"{_localize["PlanItemTranslationDesc"] ?? "Translate sentences from your target language to practice active production and grammar."}",
            PlanActivityType.Conversation =>
                $"{_localize["PlanItemConversationDesc"] ?? "Practice realistic conversations with AI. Speak naturally and get instant feedback on your responses."}",
            PlanActivityType.VocabularyGame =>
                $"{_localize["PlanItemVocabGameDesc"] ?? "Match words with their translations in a timed game. Make learning vocabulary fun and competitive."}",
            _ => "Practice your language skills with interactive exercises"
        };

        parts.Add(actionDescription);

        // Add resource context for clarity
        if (!string.IsNullOrEmpty(item.ResourceTitle))
        {
            parts.Add($"Using content from '{item.ResourceTitle}'.");
        }

        // Add difficulty level if available
        if (!string.IsNullOrEmpty(item.DifficultyLevel))
        {
            var difficultyEmoji = item.DifficultyLevel.ToLowerInvariant() switch
            {
                "beginner" => "🌱",
                "intermediate" => "🌿",
                "advanced" => "🌲",
                _ => "📊"
            };
            parts.Add($"{difficultyEmoji} {item.DifficultyLevel} level");
        }

        return string.Join(" ", parts);
    }

    /// <summary>
    /// Check if plan item is effectively complete - either explicitly marked OR time requirement met.
    /// This enables time-based progression through the plan without requiring manual completion.
    /// </summary>
    bool IsItemComplete(DailyPlanItem item)
    {
        if (item.IsCompleted)
        {
            _logger.LogDebug("🎯 Item '{TitleKey}' is flagged complete", item.TitleKey);
            return true;
        }

        var timeComplete = item.MinutesSpent >= item.EstimatedMinutes;
        if (timeComplete)
        {
            _logger.LogDebug("🎯 Item '{TitleKey}' is time-complete: {MinutesSpent}/{EstimatedMinutes} min",
                item.TitleKey, item.MinutesSpent, item.EstimatedMinutes);
        }

        return timeComplete;
    }
}
