using SentenceStudio.Api.Coach.Memory;
using SentenceStudio.Contracts.Coach.Intent;
using SentenceStudio.Contracts.LearnerMemory;

namespace SentenceStudio.Api.Coach.Application.Memory;

/// <summary>Why a memory proposal was refused before it could become a candidate.</summary>
public enum CoachMemoryProposalRefusal
{
    /// <summary>The proposal passed every gate.</summary>
    None = 0,

    /// <summary>The turn carried no proposal.</summary>
    NoProposal = 1,

    /// <summary>The learner's message contains no explicit request to remember anything.</summary>
    NoExplicitMarker = 2,

    /// <summary>The quoted evidence is not an exact substring of the learner's message.</summary>
    EvidenceMismatch = 3,

    /// <summary>The evidence span is missing or outside the allowed length.</summary>
    EvidenceUnusable = 4,

    /// <summary>The typed value does not match the declared kind, or is empty.</summary>
    ValueUnusable = 5,

    /// <summary>The value is something this system refuses to remember at all.</summary>
    ContentRefused = 6,

    /// <summary>The scope and language combination is not one the store accepts.</summary>
    ScopeUnusable = 7
}

/// <summary>The outcome of screening one model-proposed memory.</summary>
/// <param name="Refusal">Why it was refused, or <see cref="CoachMemoryProposalRefusal.None"/>.</param>
/// <param name="Value">The re-derived typed value. Non-null only when the proposal passed.</param>
/// <param name="Scope">The scope the store should record.</param>
/// <param name="EvidenceSpan">The verified span, taken from the learner's message.</param>
public readonly record struct CoachMemoryProposalScreening(
    CoachMemoryProposalRefusal Refusal,
    CoachMemoryStoredValue? Value,
    CoachMemoryScope Scope,
    string EvidenceSpan)
{
    /// <summary>True when a candidate may be created.</summary>
    public bool IsAccepted => Refusal == CoachMemoryProposalRefusal.None && Value is not null;

    /// <summary>Builds a refusal.</summary>
    public static CoachMemoryProposalScreening Refused(CoachMemoryProposalRefusal refusal) =>
        new(refusal, null, CoachMemoryScope.TargetLanguage, string.Empty);
}

/// <summary>
/// The gate between what the model proposes and what the store is asked to remember.
/// </summary>
/// <remarks>
/// <para>
/// Everything here exists because the model is not a trusted source for the claim "the learner
/// asked to be remembered". Provenance in this system is <c>UserExplicit</c>, and that word has to
/// survive contact with a model that will happily infer a preference from tone. So the application
/// re-derives every field it will store: it checks the learner's own message for an explicit
/// marker, it verifies the quoted span really is in that message character for character, it maps
/// the value into the typed branch itself rather than trusting the proposed shape, and it screens
/// the result through the same content policy the memory surface uses.
/// </para>
/// <para>
/// The store then verifies the span again. That duplication is intentional: this gate protects the
/// store from the model, and the store's own check protects it from this gate.
/// </para>
/// <para>
/// Nothing here activates anything. The best possible outcome is a <c>Candidate</c> row that never
/// enters a prompt until the learner approves it in a separate, explicit action.
/// </para>
/// </remarks>
public static class CoachMemoryProposalGate
{
    /// <summary>
    /// Closed markers that count as the learner explicitly asking to be remembered.
    /// </summary>
    /// <remarks>
    /// Short and boring on purpose. Every entry is a phrase whose plain meaning is a request to
    /// persist something; none of them is a word that shows up incidentally while studying a
    /// language. The alternative — inferring intent from sentiment — is exactly the automatic
    /// pattern memory this design refuses.
    /// </remarks>
    private static readonly string[] ExplicitMarkers =
    [
        "remember",
        "from now on",
        "going forward",
        "keep in mind",
        "don't forget",
        "dont forget",
        "make a note",
        "note that i",
        "save that",
        "save this",
        "always use",
        "in future",
        "in the future"
    ];

    /// <summary>True when the learner's own message asks for something to be remembered.</summary>
    public static bool HasExplicitMarker(string? learnerText) =>
        !string.IsNullOrWhiteSpace(learnerText)
        && ContainsAnyMarker(learnerText.ToLowerInvariant());

    /// <summary>
    /// Screens one proposal against the learner's message.
    /// </summary>
    /// <param name="proposal">What the model asked for. May be null.</param>
    /// <param name="learnerText">The learner's raw message for this turn.</param>
    /// <param name="targetLanguageCode">
    /// The trusted target language from the learner's profile. Never taken from the proposal: a
    /// model-chosen language would let a preference be filed against a language the learner is not
    /// studying, where they would never see it to remove it.
    /// </param>
    public static CoachMemoryProposalScreening Screen(
        CoachMemoryProposalIntent? proposal,
        string? learnerText,
        string? targetLanguageCode)
    {
        if (proposal is null)
        {
            return CoachMemoryProposalScreening.Refused(CoachMemoryProposalRefusal.NoProposal);
        }

        if (string.IsNullOrWhiteSpace(learnerText))
        {
            return CoachMemoryProposalScreening.Refused(CoachMemoryProposalRefusal.NoExplicitMarker);
        }

        // Gate one: the learner, not the model, has to have asked.
        if (!HasExplicitMarker(learnerText))
        {
            return CoachMemoryProposalScreening.Refused(CoachMemoryProposalRefusal.NoExplicitMarker);
        }

        // Gate two: the quoted evidence has to be real. This is what stops the model from
        // manufacturing a preference and labelling it as the learner's own words.
        var span = proposal.EvidenceSpan ?? string.Empty;
        if (span.Length < CoachMemoryLimits.EvidenceSpanMinLength
            || span.Length > CoachMemoryLimits.EvidenceSpanMaxLength
            || learnerText.Length > CoachMemoryLimits.EvidenceSourceMaxLength)
        {
            return CoachMemoryProposalScreening.Refused(CoachMemoryProposalRefusal.EvidenceUnusable);
        }

        if (!learnerText.Contains(span, StringComparison.Ordinal))
        {
            return CoachMemoryProposalScreening.Refused(CoachMemoryProposalRefusal.EvidenceMismatch);
        }

        // Gate three: rebuild the value from the declared kind rather than accepting whatever
        // combination of branches the model happened to fill in.
        var value = BuildValue(proposal);
        if (value is null)
        {
            return CoachMemoryProposalScreening.Refused(CoachMemoryProposalRefusal.ValueUnusable);
        }

        // Gate four: the capability boundary. Instructions, commands, credentials, links, role
        // markers, sensitive biography, and assessment answers are refused here, before a row
        // exists — not filtered later on the way into a prompt. A value that would be unsafe to
        // show the model is not a value worth storing and asking the learner to approve.
        var rejection = CoachMemoryPromptFormatter.IsSafeForPrompt(value);
        if (rejection != CoachMemoryValueRejection.None)
        {
            return CoachMemoryProposalScreening.Refused(
                rejection is CoachMemoryValueRejection.MissingValue
                    or CoachMemoryValueRejection.WrongBranch
                    or CoachMemoryValueRejection.Empty
                    or CoachMemoryValueRejection.UnsupportedKind
                    ? CoachMemoryProposalRefusal.ValueUnusable
                    : CoachMemoryProposalRefusal.ContentRefused);
        }

        // Gate five: scope. The proposed scope is mapped, not cast — an unmapped member is a
        // refusal, so adding one to the model's vocabulary cannot silently reach storage. A
        // target-language preference then needs a language the profile actually names.
        if (MapScope(proposal.Scope) is not { } scope)
        {
            return CoachMemoryProposalScreening.Refused(CoachMemoryProposalRefusal.ScopeUnusable);
        }

        if (scope == CoachMemoryScope.TargetLanguage && string.IsNullOrWhiteSpace(targetLanguageCode))
        {
            return CoachMemoryProposalScreening.Refused(CoachMemoryProposalRefusal.ScopeUnusable);
        }

        return new CoachMemoryProposalScreening(CoachMemoryProposalRefusal.None, value, scope, span);
    }

    /// <summary>
    /// Translates the model's proposal vocabulary into a stored value, or refuses.
    /// </summary>
    /// <remarks>
    /// This is the single join between what a model may emit and what the store accepts. Both
    /// sides are enumerated explicitly and every unmapped member falls through to <c>null</c>,
    /// so the two vocabularies can drift apart without either one quietly widening the other.
    /// </remarks>
    private static CoachMemoryStoredValue? BuildValue(CoachMemoryProposalIntent proposal) =>
        proposal.Kind switch
        {
            CoachProposedMemoryKind.PersistentStudyGoal when !string.IsNullOrWhiteSpace(proposal.StudyGoalText)
                => CoachMemoryStoredValue.StudyGoal(proposal.StudyGoalText),

            CoachProposedMemoryKind.ExplanationDepth when MapDepth(proposal.ExplanationDepth) is { } depth
                => CoachMemoryStoredValue.Depth(depth),

            CoachProposedMemoryKind.CorrectionTiming when MapTiming(proposal.CorrectionTiming) is { } timing
                => CoachMemoryStoredValue.Timing(timing),

            CoachProposedMemoryKind.ExampleRegister when MapRegister(proposal.Register) is { } register
                => CoachMemoryStoredValue.Register(register),

            _ => null
        };

    private static CoachMemoryScope? MapScope(CoachProposedMemoryScope scope) => scope switch
    {
        CoachProposedMemoryScope.TargetLanguage => CoachMemoryScope.TargetLanguage,
        CoachProposedMemoryScope.Global => CoachMemoryScope.Global,
        _ => null
    };

    private static CoachMemoryExplanationDepth? MapDepth(CoachProposedExplanationDepth? depth) => depth switch
    {
        CoachProposedExplanationDepth.Concise => CoachMemoryExplanationDepth.Concise,
        CoachProposedExplanationDepth.Balanced => CoachMemoryExplanationDepth.Balanced,
        CoachProposedExplanationDepth.Detailed => CoachMemoryExplanationDepth.Detailed,
        _ => null
    };

    private static CoachMemoryCorrectionTiming? MapTiming(CoachProposedCorrectionTiming? timing) => timing switch
    {
        CoachProposedCorrectionTiming.Immediate => CoachMemoryCorrectionTiming.Immediate,
        CoachProposedCorrectionTiming.AfterResponse => CoachMemoryCorrectionTiming.AfterResponse,
        _ => null
    };

    private static CoachMemoryExampleRegister? MapRegister(CoachProposedRegister? register) => register switch
    {
        CoachProposedRegister.NeutralPolite => CoachMemoryExampleRegister.NeutralPolite,
        CoachProposedRegister.Casual => CoachMemoryExampleRegister.Casual,
        CoachProposedRegister.Formal => CoachMemoryExampleRegister.Formal,
        _ => null
    };

    private static bool ContainsAnyMarker(string lowered)
    {
        foreach (var marker in ExplicitMarkers)
        {
            if (lowered.Contains(marker, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}
