namespace SentenceStudio.Api.Coach.Operations;

/// <summary>
/// The lifecycle of one proposed learner-owned write.
/// </summary>
/// <remarks>
/// Stored as an ordinal. Members may only be appended — inserting one silently re-labels every
/// row already written. <c>CoachWriteStoredEnumContractTests</c> pins the values.
/// </remarks>
public enum CoachWriteOperationStatus
{
    /// <summary>
    /// The model asked for the change and the server recorded the request. Nothing was written to
    /// learner data, and nothing will be until the learner accepts or confirms.
    /// </summary>
    Proposed = 0,

    /// <summary>The learner accepted (soft) or confirmed (protected) and the write completed.</summary>
    Executed = 1,

    /// <summary>An executed, reversible operation was rolled back inside its undo window.</summary>
    Undone = 2,

    /// <summary>The learner declined. Nothing was written and the proposal can never execute.</summary>
    Rejected = 3,

    /// <summary>The proposal window elapsed before the learner answered.</summary>
    Expired = 4,

    /// <summary>
    /// Exactly one approver has claimed the operation and the domain write is in flight, or its
    /// outcome was never recorded.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the state that makes execution exactly-once across processes. The claim is taken by
    /// a conditional <c>UPDATE</c> that only matches a row still in <see cref="Proposed"/> at the
    /// version the approver read, so of two concurrent approvals exactly one moves the row and only
    /// that one is allowed to call the handler. The other is told the change is already under way.
    /// </para>
    /// <para>
    /// A row left here is <em>in doubt</em>, not re-runnable: the process that held the claim died,
    /// or the settle failed after the handler had already changed learner data. Approval refuses it
    /// rather than guessing, because retrying would risk a second write and abandoning it would risk
    /// a receipt for something that did happen.
    /// </para>
    /// </remarks>
    Executing = 5,

    /// <summary>
    /// The claimed operation's handler refused, so the operation is closed and can never run again.
    /// </summary>
    /// <remarks>
    /// Terminal on purpose. A handler that threw may have changed nothing, or may have changed
    /// something and failed on the way back; the ledger cannot tell the two apart, so it refuses to
    /// offer the proposal a second life. The learner asks again and gets a fresh proposal.
    /// </remarks>
    Failed = 6
}

/// <summary>
/// Which of a stored status's two jobs it is still doing: answering for the request, or nothing.
/// </summary>
/// <remarks>
/// <para>
/// Idempotency is not "one row per request forever". It is "one row per request for as long as
/// that row can still speak for the request", and the difference is the whole of MAL-1. A row in
/// <see cref="CoachWriteOperationStatus.Proposed"/> speaks for the request because it can still be
/// approved; a row in <see cref="CoachWriteOperationStatus.Executed"/> speaks for it because its
/// receipt is the authoritative answer; a row in <see cref="CoachWriteOperationStatus.Executing"/>
/// speaks for it because the write may already have happened and a second row would risk a second
/// write. Everything else — declined, elapsed, reversed, closed after a failure — speaks for
/// nothing. Learner data is not in the state the request asked for and never will be through that
/// row, so continuing to answer with it turns idempotency into a permanent refusal wearing a
/// proposal's clothes.
/// </para>
/// <para>
/// The two predicates are written as explicit switches rather than as each other's negation, so a
/// status added later falls out of both and <c>CoachWriteStatusClassificationTests</c> fails.
/// Defining one as <c>!</c> the other would classify a new member silently, which is the failure
/// mode this exists to prevent.
/// </para>
/// </remarks>
public static class CoachWriteOperationStates
{
    /// <summary>The proposal is still awaiting the learner's answer.</summary>
    public static bool IsOpen(CoachWriteOperationStatus status) =>
        status == CoachWriteOperationStatus.Proposed;

    /// <summary>An approver holds the execution claim and the outcome is not yet known.</summary>
    public static bool IsInFlight(CoachWriteOperationStatus status) =>
        status == CoachWriteOperationStatus.Executing;

    /// <summary>The write happened and has not been reversed, so the receipt is authoritative.</summary>
    public static bool IsEffective(CoachWriteOperationStatus status) =>
        status == CoachWriteOperationStatus.Executed;

    /// <summary>
    /// The row still answers for its request, so no second proposal may be recorded for it.
    /// </summary>
    public static bool HoldsRequest(CoachWriteOperationStatus status) => status switch
    {
        CoachWriteOperationStatus.Proposed => true,
        CoachWriteOperationStatus.Executing => true,
        CoachWriteOperationStatus.Executed => true,
        _ => false
    };

    /// <summary>
    /// The row is closed and left no effect, so the request is unanswered and may be asked again.
    /// </summary>
    public static bool IsClosedWithoutEffect(CoachWriteOperationStatus status) => status switch
    {
        CoachWriteOperationStatus.Undone => true,
        CoachWriteOperationStatus.Rejected => true,
        CoachWriteOperationStatus.Expired => true,
        CoachWriteOperationStatus.Failed => true,
        _ => false
    };
}

/// <summary>
/// How — and whether — an executed operation can be reversed.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="None"/> is the default and is the honest answer for anything the server cannot put
/// back exactly as it was. Offering an undo button that deletes an approximation of what used to
/// exist is worse than offering no button, because the learner believes the original is safe.
/// </para>
/// <para>
/// External reads (a transcript import) are deliberately <see cref="None"/>. Deleting the
/// resource the import created would look like an undo while leaving anything downstream that
/// already consumed the transcript untouched.
/// </para>
/// </remarks>
public enum CoachWriteUndoKind
{
    /// <summary>Not reversible. No undo is offered and the undo route refuses.</summary>
    None = 0,

    /// <summary>The operation created a row; undo deletes exactly that row.</summary>
    DeleteCreatedEntity = 1,

    /// <summary>The operation changed named fields; undo restores the captured prior values.</summary>
    RestoreFields = 2,

    /// <summary>The operation linked a word to a resource; undo removes exactly that link.</summary>
    UnlinkVocabulary = 3
}

/// <summary>
/// What happened to a write operation, as recorded in the append-only audit.
/// </summary>
public enum CoachWriteAuditEvent
{
    /// <summary>A proposal was recorded. No learner data changed.</summary>
    Proposed = 0,

    /// <summary>The write completed.</summary>
    Executed = 1,

    /// <summary>An executed operation was reversed.</summary>
    Undone = 2,

    /// <summary>The learner declined the proposal.</summary>
    Rejected = 3,

    /// <summary>
    /// A request was refused before touching learner data — wrong owner, expired or replayed
    /// token, unknown operation, or a closed operation asked to run again.
    /// </summary>
    Denied = 4,

    /// <summary>An already-executed operation was requested again and the stored receipt was replayed.</summary>
    Replayed = 5
}

/// <summary>
/// The kind of learner-owned entity a write operation touches.
/// </summary>
/// <remarks>
/// Entity identifiers are the only learner-data reference the audit is allowed to carry, so the
/// kind has to be a closed set: an operator reading the audit must be able to tell what an id
/// points at without joining to anything that holds learner text.
/// </remarks>
public enum CoachWriteEntityKind
{
    /// <summary>No entity, or the entity was never created.</summary>
    None = 0,

    /// <summary>A row in <c>VocabularyWord</c>.</summary>
    VocabularyWord = 1,

    /// <summary>A row in <c>SkillProfile</c>.</summary>
    SkillProfile = 2,

    /// <summary>A row in <c>LearningResource</c>.</summary>
    LearningResource = 3,

    /// <summary>A row in <c>ResourceVocabularyMapping</c>.</summary>
    ResourceVocabularyLink = 4,

    /// <summary>The learner's own <c>UserProfile</c> row.</summary>
    UserProfile = 5,

    /// <summary>Today's plan for the learner.</summary>
    DailyPlan = 6
}
