using SentenceStudio.Contracts.Coach;
using SentenceStudio.Services.Progress;

namespace SentenceStudio.WebUI.Services;

/// <summary>
/// Single source of truth for how a plan activity is presented in the shared Blazor UI:
/// Bootstrap icon, localized label, input/output category and dot class, and activity route.
/// </summary>
/// <remarks>
/// <para>
/// Before this helper existed there were two divergent maps — <c>Index.razor</c> (localized) and
/// <c>PlanSummaryCard.razor</c> (hardcoded English) — and the coach plan canvas would have been the
/// third. The hardcoded map was a live localization bug: a Korean-display learner saw
/// "Vocabulary Review" on the activity log while the rest of that card was translated.
/// </para>
/// <para>
/// Labels are resolved through <see cref="BlazorLocalizationService"/> at call time, never cached in
/// a static field, so a culture change re-renders correctly.
/// </para>
/// </remarks>
public static class PlanActivityPresentation
{
    /// <summary>Bootstrap icon class for an activity type. Icons only, never emoji.</summary>
    public static string Icon(PlanActivityType type) => type switch
    {
        PlanActivityType.VocabularyReview => "bi-card-checklist",
        PlanActivityType.Reading => "bi-book",
        PlanActivityType.Listening => "bi-headphones",
        PlanActivityType.Shadowing => "bi-soundwave",
        PlanActivityType.Cloze => "bi-puzzle",
        PlanActivityType.Translation => "bi-translate",
        PlanActivityType.Writing => "bi-pencil-square",
        PlanActivityType.SceneDescription => "bi-image",
        PlanActivityType.Conversation => "bi-chat-dots",
        PlanActivityType.VocabularyGame => "bi-grid-3x3-gap",
        PlanActivityType.VideoWatching => "bi-play-circle",
        PlanActivityType.NumberDrill => "bi-123",
        _ => "bi-check-circle"
    };

    /// <summary>Bootstrap icon class for a coach plan item.</summary>
    public static string Icon(CoachPlanActivityType type) => Icon(ToPlanActivityType(type));

    /// <summary>Localized display label for an activity type.</summary>
    public static string Label(BlazorLocalizationService localize, PlanActivityType type)
    {
        ArgumentNullException.ThrowIfNull(localize);

        return type switch
        {
            PlanActivityType.VocabularyReview => localize["Activity_VocabularyReview"],
            PlanActivityType.Reading => localize["Activity_Reading"],
            PlanActivityType.Listening => localize["Activity_Listening"],
            PlanActivityType.Shadowing => localize["Activity_Shadowing"],
            PlanActivityType.Cloze => localize["Activity_ClozeExercise"],
            PlanActivityType.Translation => localize["Activity_Translate"],
            PlanActivityType.Writing => localize["Activity_Writing"],
            PlanActivityType.SceneDescription => localize["Activity_DescribeScene"],
            PlanActivityType.Conversation => localize["Activity_Conversation"],
            PlanActivityType.VocabularyGame => localize["Activity_VocabMatching"],
            PlanActivityType.VideoWatching => localize["Activity_VideoWatching"],
            PlanActivityType.NumberDrill => localize["Activity_NumberDrill"],
            _ => localize["Activity_Generic"]
        };
    }

    /// <summary>Localized display label for a coach plan item.</summary>
    public static string Label(BlazorLocalizationService localize, CoachPlanActivityType type) =>
        Label(localize, ToPlanActivityType(type));

    /// <summary>Input/output classification, delegated to the shared domain mapper.</summary>
    public static ActivityCategory Category(PlanActivityType type) =>
        ActivityCategoryMapper.Categorize(type);

    /// <summary>Input/output classification for a coach plan item.</summary>
    public static ActivityCategory Category(CoachPlanActivityType type) =>
        ActivityCategoryMapper.Categorize(ToPlanActivityType(type));

    /// <summary>CSS modifier for the shared <c>activity-dot</c> indicator.</summary>
    public static string DotClass(ActivityCategory category) => category switch
    {
        ActivityCategory.Input => "activity-dot-input",
        ActivityCategory.Output => "activity-dot-output",
        _ => string.Empty
    };

    /// <summary>Relative route segment that starts the activity.</summary>
    public static string Route(PlanActivityType type) => type switch
    {
        PlanActivityType.VocabularyReview => "vocab-quiz",
        PlanActivityType.Reading => "reading",
        // No dedicated listening page; shares the shadowing UI.
        PlanActivityType.Listening => "shadowing",
        PlanActivityType.Shadowing => "shadowing",
        PlanActivityType.Cloze => "cloze",
        PlanActivityType.Translation => "translation",
        PlanActivityType.Writing => "writing",
        PlanActivityType.SceneDescription => "scene",
        PlanActivityType.Conversation => "conversation",
        PlanActivityType.VocabularyGame => "vocab-matching",
        PlanActivityType.VideoWatching => "video-watching",
        PlanActivityType.NumberDrill => "numberdrill",
        _ => "vocab-quiz"
    };

    /// <summary>
    /// Maps the coach wire enum onto the domain enum. The two are declared separately on purpose
    /// (contract stability), so this is the one place that couples them.
    /// </summary>
    public static PlanActivityType ToPlanActivityType(CoachPlanActivityType type) => type switch
    {
        CoachPlanActivityType.VocabularyReview => PlanActivityType.VocabularyReview,
        CoachPlanActivityType.Reading => PlanActivityType.Reading,
        CoachPlanActivityType.Listening => PlanActivityType.Listening,
        CoachPlanActivityType.VideoWatching => PlanActivityType.VideoWatching,
        CoachPlanActivityType.Shadowing => PlanActivityType.Shadowing,
        CoachPlanActivityType.Cloze => PlanActivityType.Cloze,
        CoachPlanActivityType.Translation => PlanActivityType.Translation,
        CoachPlanActivityType.Writing => PlanActivityType.Writing,
        CoachPlanActivityType.SceneDescription => PlanActivityType.SceneDescription,
        CoachPlanActivityType.Conversation => PlanActivityType.Conversation,
        CoachPlanActivityType.VocabularyGame => PlanActivityType.VocabularyGame,
        CoachPlanActivityType.NumberDrill => PlanActivityType.NumberDrill,
        _ => PlanActivityType.VocabularyReview
    };
}
