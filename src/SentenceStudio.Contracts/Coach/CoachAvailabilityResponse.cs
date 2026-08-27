namespace SentenceStudio.Contracts.Coach;

/// <summary>
/// The answer to an availability request.
/// The client shows an entry point only when IsAvailable is true.
/// </summary>
public sealed class CoachAvailabilityResponse
{
    /// <summary>True if the learner can open the coach now.</summary>
    public required bool IsAvailable { get; init; }

    /// <summary>The reason for the availability state.</summary>
    public required CoachAvailabilityState State { get; init; }

    /// <summary>The localized label for the entry point, for example "Resume coach".</summary>
    public string? EntryPointLabel { get; init; }

    /// <summary>
    /// True when there is a plan for today that the coach can adjust.
    /// </summary>
    /// <remarks>
    /// The coach is still available when this is false: a learner can ask what a word means
    /// before they have generated a plan. Only the plan-editing surface is hidden. Opening the
    /// coach, or asking it a question, never creates a plan.
    /// </remarks>
    public bool CanEditPlan { get; init; } = true;

    /// <summary>The session the learner can resume. Null if there is no active session.</summary>
    public string? ActiveSessionId { get; init; }

    /// <summary>The status of the active session. Null if there is no active session.</summary>
    public CoachSessionStatus? ActiveSessionStatus { get; init; }

    /// <summary>The time the active session expires. Null if there is no active session.</summary>
    public DateTime? ActiveSessionExpiresAtUtc { get; init; }

    /// <summary>The runs the learner has left today. Null if the server sets no daily limit.</summary>
    public int? RunsRemainingToday { get; init; }

    /// <summary>The runs the learner has left this week. Null if the server sets no weekly limit.</summary>
    public int? RunsRemainingThisWeek { get; init; }

    /// <summary>
    /// True when conversations persist beyond the current session and the history surface works.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Defaults to <see langword="false"/>, so a client reading an older server — or a response
    /// that predates this field — behaves as though history is off. That is the safe direction:
    /// hiding a surface that exists is a missing feature, while showing one that does not exist
    /// produces failed requests the learner cannot act on.
    /// </para>
    /// <para>
    /// This says only whether the surface is usable. It deliberately reveals nothing about
    /// <em>why</em>: a client cannot tell a disabled flag from an unregistered service, because
    /// that difference is an operator's concern and naming it would leak deployment shape to
    /// anyone who can call the route.
    /// </para>
    /// <para>
    /// It replaces probing a history route and inferring the answer from a 404, which cannot
    /// distinguish "history is off" from "this conversation is not yours".
    /// </para>
    /// </remarks>
    public bool IsDurableHistoryAvailable { get; init; }

    /// <summary>
    /// True when the coach can hold saved learner preferences and propose new ones.
    /// </summary>
    /// <remarks>
    /// Independent of <see cref="IsDurableHistoryAvailable"/> in both directions, and the two are
    /// tested that way. They are separate features behind separate flags: memory survives without
    /// durable history because an approved preference is not part of any conversation, and
    /// history works without memory because a transcript needs nothing remembered about it.
    /// Defaults to <see langword="false"/> for the same reason as above.
    /// </remarks>
    public bool IsMemoryAvailable { get; init; }

    /// <summary>
    /// True when the Sam persistent overlay UX is enabled for this learner.
    /// When true, the client renders <c>SamOverlayHost</c> instead of the legacy
    /// <c>CoachWorkspaceHost</c>.
    /// </summary>
    public bool IsSamOverlayAvailable { get; init; }

    /// <summary>
    /// True when Sam may propose changes to the learner's own study material, and the client may
    /// therefore render approval controls.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Defaults to <see langword="false"/>, and the default is the point. A client reading an
    /// older server, a response that predates this field, or an availability call that failed
    /// outright all end up here, and all three must render a conversation with no approval
    /// affordances rather than buttons whose requests would be refused.
    /// </para>
    /// <para>
    /// This says the surface exists, not that any particular change may be approved. Every
    /// approval route re-checks ownership, state, expiry, and the approval channel on the
    /// authenticated request, so a client that had this wrong could still change nothing.
    /// </para>
    /// </remarks>
    public bool IsSamWriteAvailable { get; init; }
}
