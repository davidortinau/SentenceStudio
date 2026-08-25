using SentenceStudio.Contracts.Coach;

namespace SentenceStudio.Api.Coach.Opportunities;

/// <summary>
/// Pointers to the two encrypted messages that explain one Product row, and nothing else.
/// </summary>
/// <remarks>
/// <para>
/// Identifiers and sequence numbers only. There is deliberately no text member: a call site that
/// wanted to attach a learner's phrase has nowhere to put it, and
/// <c>CoachOpportunityShapeTests</c> fails the build if one appears.
/// </para>
/// <para>
/// Resolution goes through <c>ICoachMessageStore</c> with a <c>CoachOwner</c> built from the
/// <b>row's</b> <c>UserProfileId</c>, so the content protector's purpose chain does the
/// enforcement. A pointer copied onto another row cannot decrypt.
/// </para>
/// </remarks>
/// <param name="ConversationId">The conversation the evidence lives in.</param>
/// <param name="MessageId">The learner's message for this turn.</param>
/// <param name="MessageSequence">Its immutable position in the conversation.</param>
/// <param name="OfferMessageId">The prior coach message the answer was answering.</param>
/// <param name="OfferMessageSequence">Its immutable position in the conversation.</param>
public readonly record struct CoachOpportunityEvidencePointer(
    string? ConversationId = null,
    string? MessageId = null,
    long? MessageSequence = null,
    string? OfferMessageId = null,
    long? OfferMessageSequence = null)
{
    /// <summary>A pointer to nothing. The value every AggregateOnly row carries.</summary>
    public static CoachOpportunityEvidencePointer None { get; } = new();

    /// <summary>True when at least one message pointer is present.</summary>
    public bool HasMessagePointer =>
        !string.IsNullOrWhiteSpace(MessageId) || !string.IsNullOrWhiteSpace(OfferMessageId);
}

/// <summary>
/// The one and only input a caller may hand to <see cref="ICoachOpportunityRecorder"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>The privacy boundary is this type's shape, not a redaction routine.</b> Every member is a
/// closed enum, a closed-vocabulary code, an opaque identifier, or a sequence number. There is no
/// free-text member and no payload member, so no call site — present or future — can put a
/// learner's phrase, a vocabulary term, a prompt, a model completion, an email, a token, or a
/// tool argument into the ledger. A reviewer can verify that by reading this declaration rather
/// than by trusting that every call site remembered to sanitize.
/// </para>
/// <para>
/// The recorder validates <see cref="CapabilityCode"/>, <see cref="ToolName"/>, and
/// <see cref="FailureCode"/> against their closed sets before writing, so even a member typed as
/// <c>string</c> cannot become an unbounded column.
/// </para>
/// </remarks>
/// <param name="Kind">What kind of gap this is.</param>
/// <param name="CapabilityCode">
/// What the learner was reaching for. Must be a member of
/// <see cref="CoachOpportunityCapabilityCodes.All"/>.
/// </param>
/// <param name="Surface">Which boundary observed it.</param>
/// <param name="Disposition">Whether this is individually reviewable or only counted.</param>
/// <param name="OfferLink">What the learner's message was answering, when anything.</param>
/// <param name="ToolName">
/// The registered tool name, validated against <c>ICoachToolRegistry.IsRegistered</c>. Never a
/// model-supplied string.
/// </param>
/// <param name="FailureCode">
/// Why the server said no, from the existing closed vocabularies
/// (<c>CoachWriteFailureCodes</c>, <c>CoachToolException.Code</c>).
/// </param>
/// <param name="StopReason">The turn's stop reason, when the surface is the turn outcome.</param>
/// <param name="Evidence">Message pointers. Stripped when <paramref name="Disposition"/> is aggregate-only.</param>
/// <param name="TurnId">The turn identity, when one is known.</param>
/// <param name="TurnOperationId">The durable turn operation, when one is known.</param>
/// <param name="WriteOperationId">The write ledger row, when one is known.</param>
/// <param name="RelatedOpportunityId">An earlier row this one continues, when one is known.</param>
public readonly record struct CoachOpportunitySignal(
    CoachOpportunityKind Kind,
    string CapabilityCode,
    CoachOpportunitySurface Surface,
    CoachOpportunityDisposition Disposition,
    CoachOpportunityOfferLink OfferLink = CoachOpportunityOfferLink.None,
    string? ToolName = null,
    string? FailureCode = null,
    CoachStopReason? StopReason = null,
    CoachOpportunityEvidencePointer Evidence = default,
    string? TurnId = null,
    string? TurnOperationId = null,
    string? WriteOperationId = null,
    string? RelatedOpportunityId = null)
{
    /// <summary>
    /// The same signal with every conversation, turn, and evidence pointer removed.
    /// </summary>
    /// <remarks>
    /// Applied unconditionally by the recorder to every
    /// <see cref="CoachOpportunityDisposition.AggregateOnly"/> signal, so a mapper that forgot
    /// cannot produce an inspectable row. This is the mechanism behind the
    /// "a refusal never becomes a dossier" guarantee.
    /// </remarks>
    public CoachOpportunitySignal WithoutPointers() => this with
    {
        Evidence = CoachOpportunityEvidencePointer.None,
        TurnId = null,
        TurnOperationId = null,
        WriteOperationId = null,
        RelatedOpportunityId = null
    };
}
