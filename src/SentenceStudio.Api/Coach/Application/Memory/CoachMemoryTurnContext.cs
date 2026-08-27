using SentenceStudio.Contracts.Coach;
using SentenceStudio.Contracts.LearnerMemory;

namespace SentenceStudio.Api.Coach.Application.Memory;

/// <summary>
/// Decides what the memory selector is asked for on one turn, from trusted application state.
/// </summary>
/// <remarks>
/// <para>
/// The selector takes a closed category rather than learner text, and this is the code that has to
/// honour the spirit of that. Retrieval is driven by what the application knows — whether a plan
/// suggestion is open, what today's constraints say — not by anything the learner or the model
/// wrote. A free-text classifier here would quietly reintroduce the untrusted-query design the
/// memory lane rejected.
/// </para>
/// <para>
/// Learner text is consulted for exactly one thing: <see cref="ExcludedKinds"/>. That direction is
/// safe by construction because an exclusion can only ever <em>remove</em> a preference from the
/// prompt. The worst a crafted message can do is suppress the sender's own saved setting, which is
/// the same thing they could achieve by saying it in plain words.
/// </para>
/// </remarks>
public static class CoachMemoryTurnContext
{
    /// <summary>
    /// Closed markers that mean "this current message overrides my saved preference for the rest
    /// of this turn". Lower-cased and compared with ordinal containment.
    /// </summary>
    private static readonly string[] DepthOverrideMarkers =
    [
        "be brief", "keep it short", "keep it brief", "short answer", "briefly",
        "in detail", "explain in detail", "more detail", "long answer", "go deep",
        "just the answer", "no explanation"
    ];

    private static readonly string[] TimingOverrideMarkers =
    [
        "correct me now", "correct me immediately", "don't correct", "dont correct",
        "wait until i finish", "let me finish", "correct me after"
    ];

    private static readonly string[] RegisterOverrideMarkers =
    [
        "formal", "informal", "casual", "polite form", "plain form", "honorific"
    ];

    /// <summary>
    /// Classifies the turn from application state alone.
    /// </summary>
    /// <remarks>
    /// The categories only reorder the four kinds under the eight-fact cap; none of them excludes a
    /// kind outright. That is why a coarse two-way split is honest rather than lazy: it moves the
    /// study goal to the front when the learner is demonstrably talking about their plan, and
    /// otherwise leaves the delivery preferences in front, which is the right default for a
    /// conversation whose subject the application has not been told.
    /// </remarks>
    public static CoachMemoryTurnCategory Categorize(
        CoachConstraintSetDto? constraints,
        string? pendingSuggestionId)
    {
        // An open suggestion is the one unambiguous signal the application owns: the learner is
        // mid-decision about what to study, so their persistent study goal is the most relevant
        // thing to have in view.
        if (!string.IsNullOrWhiteSpace(pendingSuggestionId))
        {
            return CoachMemoryTurnCategory.StudyPlanning;
        }

        if (!string.IsNullOrWhiteSpace(constraints?.GoalTag))
        {
            return CoachMemoryTurnCategory.StudyPlanning;
        }

        return CoachMemoryTurnCategory.GeneralConversation;
    }

    /// <summary>
    /// The kinds the current request or current app state has already overridden.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Precedence, made mechanical. A saved preference is the weakest input in the turn, so when
    /// the learner has said what they want <em>now</em>, or the plan already carries an explicit
    /// goal, the saved value is dropped before ranking rather than argued with inside the prompt.
    /// Telling a model "here are two contradictory preferences, prefer the newer one" is a request,
    /// not a guarantee; not sending the older one is a guarantee.
    /// </para>
    /// <para>
    /// Marker matching is deliberately crude and deliberately one-directional. A false positive
    /// costs one turn without a saved preference. A false negative costs nothing that the model's
    /// own reading of the current message would not already fix.
    /// </para>
    /// </remarks>
    public static IReadOnlyCollection<CoachMemoryKind> ExcludedKinds(
        string? learnerText,
        CoachConstraintSetDto? constraints)
    {
        var excluded = new List<CoachMemoryKind>(4);

        // App state: today's plan already names a goal the learner chose in the planning surface.
        // That is a stronger, more current statement of intent than a preference saved weeks ago.
        if (!string.IsNullOrWhiteSpace(constraints?.GoalTag))
        {
            excluded.Add(CoachMemoryKind.PersistentStudyGoal);
        }

        if (!string.IsNullOrWhiteSpace(learnerText))
        {
            var text = learnerText.ToLowerInvariant();

            if (ContainsAny(text, DepthOverrideMarkers))
            {
                excluded.Add(CoachMemoryKind.ExplanationDepth);
            }

            if (ContainsAny(text, TimingOverrideMarkers))
            {
                excluded.Add(CoachMemoryKind.CorrectionTiming);
            }

            if (ContainsAny(text, RegisterOverrideMarkers))
            {
                excluded.Add(CoachMemoryKind.ExampleRegister);
            }
        }

        return excluded;
    }

    private static bool ContainsAny(string lowered, string[] markers)
    {
        foreach (var marker in markers)
        {
            if (lowered.Contains(marker, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}
