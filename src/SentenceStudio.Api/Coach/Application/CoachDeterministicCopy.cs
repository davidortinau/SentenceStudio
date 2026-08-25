using System.Globalization;
using SentenceStudio.Contracts.Coach;

namespace SentenceStudio.Api.Coach.Application;

/// <summary>
/// Application-owned text for every consequential coach surface.
/// </summary>
/// <remarks>
/// <para>
/// The model writes fluent prose, and fluent prose invents numbers. A live run applied a
/// correct "15 minutes, no audio" revision and then told the learner the plan "fits 12 minutes
/// total, with a 5-word vocabulary review and a 10-minute reading activity" — internally
/// inconsistent, wrong about the totals, and silent about the work it preserved. The plan data
/// underneath was right the whole time.
/// </para>
/// <para>
/// So the model does not get to narrate a change. Anything that asserts what happened to
/// Today's Plan — an applied receipt, an accepted suggestion, the rationale on a pending offer
/// — is generated here from the validated constraint delta. The numbers live in
/// <c>CoachChangeReceiptDto</c> and <c>CoachPlanDiffDto</c>, which the server derived itself,
/// and the client renders those rather than a sentence about them.
/// </para>
/// <para>
/// The model still writes the clarifying question and the off-topic or no-change reply. Those
/// assert nothing about the plan, and they still pass the answer-leak and banned-claim
/// validators before anyone sees them.
/// </para>
/// <para>
/// The strings are deliberately plain and free of counts, so a localization pass can replace
/// them with resource lookups without changing any behaviour.
/// </para>
/// </remarks>
public static class CoachDeterministicCopy
{
    /// <summary>Receipt text after a direct learner request is applied.</summary>
    public const string AppliedDirectChange = "Today\u2019s Plan now matches your change.";

    /// <summary>Receipt text after a pending suggestion is accepted, tapped or typed.</summary>
    public const string AppliedSuggestion = "Applied the suggested change to Today\u2019s Plan.";

    /// <summary>Notice text when a suggestion is declined.</summary>
    public const string RejectedSuggestion = "Today\u2019s Plan is unchanged.";

    /// <summary>Notice text when a turn asked for nothing the plan can act on.</summary>
    public const string NoChange = "Today\u2019s Plan is unchanged.";

    /// <summary>
    /// Describes a pending suggestion using only the validated delta. Never states a count of
    /// items, minutes, or words — the preview diff is the numeric authority.
    /// </summary>
    public static string SuggestionRationale(CoachConstraintDeltaDto delta) =>
        SuggestionRationale(delta, null);

    /// <summary>
    /// The rationale for a pending offer, including the focus the server actually resolved.
    /// </summary>
    /// <remarks>
    /// The count comes from the resolver's own answer, never from the model, and the learner's raw
    /// wording never appears — the canonical label does. A model that claimed "twelve action verbs"
    /// cannot put that number in front of the learner through this path.
    /// </remarks>
    public static string SuggestionRationale(
        CoachConstraintDeltaDto delta, CoachVocabularyFocusDto? focus)
    {
        ArgumentNullException.ThrowIfNull(delta);

        var parts = Describe(delta).ToList();

        if (focus is not null)
        {
            // Replaces the generic focus phrase with the concrete, resolved one.
            parts.RemoveAll(p => p == FocusChangedPhrase);
            parts.Insert(0, FocusFound(focus.SelectedCount, focus.DisplayLabel));
        }

        return parts.Count == 0
            ? "I prepared a change for your review."
            : $"I prepared a change for your review: {JoinReadable(parts)}.";
    }

    /// <summary>The phrase a focus change contributes before the selection is known.</summary>
    public const string FocusChangedPhrase = "a new vocabulary focus";

    /// <summary>
    /// "I found 5 matching action verbs for this plan." Counts are the resolver's; the label is
    /// the registry's; neither is the model's.
    /// </summary>
    public static string FocusFound(int selectedCount, string displayLabel) =>
        $"I found {selectedCount} matching {displayLabel} for this plan";

    /// <summary>The receipt sentence for an applied focus. Same numbers, same label, same source.</summary>
    public static string FocusApplied(int selectedCount, string displayLabel) =>
        $"Today's Plan now uses {selectedCount} matching {displayLabel}.";

    /// <summary>The receipt sentence for a cleared focus.</summary>
    public const string FocusCleared = "Today's Plan no longer uses a vocabulary focus.";

    /// <summary>
    /// The learner-facing phrase for each changed field, in the delta's own declared order.
    /// </summary>
    /// <remarks>
    /// Reads <see cref="CoachConstraintDeltaDto.ChangedFields"/> rather than scanning the
    /// object, so a field the mapper did not validate can never be described.
    /// </remarks>
    public static IEnumerable<string> Describe(CoachConstraintDeltaDto delta)
    {
        ArgumentNullException.ThrowIfNull(delta);

        foreach (var field in delta.ChangedFields)
        {
            var phrase = Describe(delta, field);
            if (phrase is not null)
            {
                yield return phrase;
            }
        }
    }

    private static string? Describe(CoachConstraintDeltaDto delta, CoachConstraintField field) => field switch
    {
        CoachConstraintField.AvailableMinutes => delta.AvailableMinutes is { } minutes
            ? string.Create(CultureInfo.InvariantCulture, $"{minutes} minutes")
            : null,

        CoachConstraintField.AudioAllowed => delta.AudioAllowed is { } audio
            ? audio ? "audio allowed" : "no audio"
            : null,

        CoachConstraintField.SpeechAllowed => delta.SpeechAllowed is { } speech
            ? speech ? "speaking allowed" : "no speaking"
            : null,

        CoachConstraintField.TypingAllowed => delta.TypingAllowed is { } typing
            ? typing ? "typing allowed" : "no typing"
            : null,

        CoachConstraintField.SkillEmphasis => delta.ClearSkillEmphasis
            ? "no skill focus"
            : delta.SkillEmphasis is { } emphasis
                ? $"a focus on {emphasis.ToString().ToLowerInvariant()}"
                : null,

        // The tag itself is learner-supplied text. The delta already carries it for the client
        // to render; the sentence stays generic so no free text is echoed back in prose.
        CoachConstraintField.GoalTag => delta.ClearGoalTag ? "no goal" : "a different goal",

        CoachConstraintField.GoalHorizonDays => delta.ClearGoalHorizonDays
            ? "no goal date"
            : delta.GoalHorizonDays is { } days
                ? string.Create(CultureInfo.InvariantCulture, $"a {days} day goal window")
                : null,

        CoachConstraintField.EnergyLevel => delta.EnergyLevel is { } energy
            ? energy == CoachEnergyLevel.Low ? "lower energy" : "normal energy"
            : null,

        // Never the learner's raw wording: it is unvalidated free text, and the canonical label
        // is what the server actually acted on.
        CoachConstraintField.VocabularyFocus =>
            delta.ClearVocabularyFocus ? "no vocabulary focus" : FocusChangedPhrase,

        _ => null
    };

    private static string JoinReadable(IReadOnlyList<string> parts) => parts.Count switch
    {
        1 => parts[0],
        2 => $"{parts[0]} and {parts[1]}",
        _ => $"{string.Join(", ", parts.Take(parts.Count - 1))}, and {parts[^1]}"
    };

    /// <summary>The one question an unrecognized focus produces. Never a guess.</summary>
    public const string UnrecognizedFocusQuestion =
        "I can focus on a kind of word — verbs, nouns, adjectives, adverbs, expressions, or " +
        "counters. Which of those did you mean?";

    /// <summary>
    /// Why a focus the registry understood could not be filled, from resolver counts only.
    /// </summary>
    public static string FocusUnavailable(CoachFocusFailure failure, int matchedCount) => failure switch
    {
        CoachFocusFailure.MetadataUnavailable =>
            "Not enough of your vocabulary is labelled by word type yet, so I could not build " +
            "that focus. Today's Plan is unchanged.",
        CoachFocusFailure.NoMatches =>
            "None of your vocabulary matches that focus yet. Today's Plan is unchanged.",
        CoachFocusFailure.InsufficientMatches =>
            $"Only {matchedCount} of your words match that focus, which is too few to build a " +
            "session around. Today's Plan is unchanged.",
        _ => "I could not build that focus. Today's Plan is unchanged."
    };

    /// <summary>The rationale for a focus suggestion. Counts come from the resolver, not the model.</summary>
    public static string FocusSuggestion(int selectedCount, string displayLabel) =>
        $"I found {selectedCount} matching {displayLabel} for this plan.";

    // ─────────────────────────────────────────────────────────────────────────
    // Limitation copy (W7).
    //
    // Every member below is a plain const with no interpolation, because the
    // numbers belong to CoachLimitationDto and nowhere else. That is not tidiness
    // — a count inside a sentence is a count no test can check and no translator
    // can keep true, and the coach's documented failure mode is exactly a fluent
    // sentence with a wrong number in it. A reflection test asserts that no const
    // here contains a digit or a date.
    //
    // Read them out loud before changing them. S16's acceptance criterion is that
    // no moral lecture appears, and the difference between an honest boundary and
    // a lecture is entirely tone.
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>S15. Why Sam will not delete a whole vocabulary, without calling the request a mistake.</summary>
    /// <remarks>
    /// The first draft ended "You can do it yourself", which implied a self-service whole-vocabulary
    /// deletion exists somewhere. It does not. The learner would have gone looking, found the
    /// bounded screen, and read the mismatch as their own failure to find the right control.
    /// </remarks>
    public const string BulkVocabularyDeletionRefusal =
        "I can\u2019t remove your whole vocabulary from here \u2014 it would take your review " +
        "history with it, and there\u2019s no undo. There are some smaller steps that get you " +
        "most of the way, with a way back.";

    /// <summary>S15. Names the bounded surface as the recommended one.</summary>
    public const string BulkVocabularyDeletionRedirect =
        "Your vocabulary screen lets you clear one list at a time, and you can see what\u2019s " +
        "going before it goes.";

    /// <summary>S15. Names the export screen, which is real, before anything is removed.</summary>
    /// <remarks>
    /// <para>
    /// This replaces a sentence that said a start-clean "lives in your settings". It does not.
    /// Settings exports data and deletes coach conversation history; there is no account-level
    /// delete-everything anywhere in this build. Naming a screen that cannot perform the request is
    /// worse than naming none: the learner goes there, hunts for a control that was never written,
    /// and concludes they missed it.
    /// </para>
    /// <para>
    /// What Settings really offers is the export, and the export is the alternative that makes the
    /// learner's original request recoverable rather than smaller. So this sentence names the
    /// capability that exists, and the one that does not is simply not claimed.
    /// </para>
    /// </remarks>
    public const string BulkVocabularyDeletionExportSurface =
        "You can download a copy of everything from your settings first, so nothing is gone for good.";

    /// <summary>
    /// S16. Refuses disclosure in one sentence, without a lecture and without treating the ask as bad faith.
    /// </summary>
    public const string ReviewAnswerRefusal =
        "I won\u2019t hand over today\u2019s answers \u2014 reading them is the one thing that " +
        "stops a review from counting. Let\u2019s keep your streak a different way.";

    /// <summary>S16. Introduces the ladder as help rather than as a consolation prize.</summary>
    public const string ReviewAnswerHintLadderOffer =
        "I can give you a nudge on anything that\u2019s stuck, and a bigger one after that if you " +
        "still need it. You\u2019ll still be the one who says the word.";

    /// <summary>S16. Offers the shorter session. Says what is smaller and what is not.</summary>
    public const string ReviewAnswerShorterSessionOffer =
        "Or take a shorter set today. Fewer words, same kind of practice \u2014 it still counts.";

    // ─────────────────────────────────────────────────────────────────────────
    // Repair copy (W6).
    //
    // Each of these replaces a sentence a claim rule found dishonest. They are
    // short on purpose: a repaired answer should read like the coach declining
    // to assert something, not like a compliance notice bolted onto a lesson.
    //
    // All count-free and date-free, under the same reflection test as the rest.
    // A repair that stated a number would be the original defect wearing the
    // fix's clothes — the whole failure mode is a fluent sentence carrying a
    // figure nobody checked.
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>Replaces a claim about the learner that no read supports.</summary>
    public const string UncheckedLearnerState =
        "I haven\u2019t looked at your practice history for this, so I can\u2019t say.";

    /// <summary>Replaces an absolute negative made over a partial read.</summary>
    /// <remarks>
    /// It says what was actually true — a part was looked at — rather than reversing the claim.
    /// Reversing it would be a second unsupported assertion in the opposite direction.
    /// </remarks>
    public const string PartialCoverageNegative =
        "I only looked at part of your data here, so I can\u2019t tell you there\u2019s none.";

    /// <summary>Replaces a stated check that never ran.</summary>
    public const string NoReadHappened =
        "I didn\u2019t actually check that.";

    /// <summary>Replaces a ranking claim over an unranked result.</summary>
    public const string UnrankedResult =
        "These aren\u2019t in any particular order.";

    /// <summary>Replaces a number the evidence does not support.</summary>
    public const string UnsupportedCount =
        "I don\u2019t have a reliable number for that.";

    /// <summary>Replaces a claim of inability for something the app can do.</summary>
    public const string CapableAfterAll =
        "That is something you can do \u2014 let me point you at it.";
}
