namespace SentenceStudio.WebApp.Operator;

/// <summary>
/// The sentences the operator page shows when a call does not return data.
/// </summary>
/// <remarks>
/// <para>
/// Two scopes, because a refusal on one row is not a statement about the surface.
/// <see cref="Surface"/> answers "can this caller use the operator page at all" and drives the
/// banner at the top; <see cref="Evidence"/> answers "could this one row be decrypted" and is
/// rendered inside the open detail card. Telling the learner-facing consequence apart from the
/// row-level one matters: a reviewer whose rollup and row list are working fine was being told
/// the whole surface was unavailable because one row was no longer accessible.
/// </para>
/// <para>
/// <b>The evidence text is deliberately identical for a foreign and a nonexistent row.</b>
/// Both produce a 404, so the page must not recreate an ownership oracle through its notice.
/// </para>
/// </remarks>
public static class SamOpportunityNotices
{
    /// <summary>Shown in the page banner when the surface itself is not available.</summary>
    public const string SurfaceUnavailable =
        "The operator surface is not available for this caller.";

    /// <summary>Shown in the open detail card when one row's evidence cannot be read.</summary>
    public const string EvidenceUnavailable =
        "Evidence is not available for this opportunity.";

    /// <summary>
    /// Describes a failure that concerns the whole surface: the rollup, the row list, or a review.
    /// </summary>
    /// <returns>Null when the call succeeded.</returns>
    public static string? Surface(SamOpportunityClientStatus status) => status switch
    {
        SamOpportunityClientStatus.Success => null,

        SamOpportunityClientStatus.NotAvailable => SurfaceUnavailable,

        SamOpportunityClientStatus.InvalidRequest =>
            "That request was rejected. Check the linked spec path shape.",

        SamOpportunityClientStatus.CrossOwnerRefused => SurfaceUnavailable,

        SamOpportunityClientStatus.TransitionRefused =>
            "An accepted opportunity cannot be returned to a status the retention sweep would "
            + "age out. Reload the row before deciding again.",

        SamOpportunityClientStatus.EphemeralKeyRing =>
            "This host's Data Protection key ring is ephemeral, so stored messages cannot be "
            + "decrypted. Configure a durable key ring first.",

        _ => "The operator request failed."
    };

    /// <summary>
    /// Describes a failure to reveal one row's evidence. Scoped to that row, never to the surface.
    /// </summary>
    /// <returns>Null when the call succeeded.</returns>
    public static string? Evidence(SamOpportunityClientStatus status) => status switch
    {
        SamOpportunityClientStatus.Success => null,

        // The row is gone, it belongs to another learner, or the surface refused. All three are
        // one sentence here for the same reason the server answers all three with 404.
        SamOpportunityClientStatus.NotAvailable => EvidenceUnavailable,
        SamOpportunityClientStatus.CrossOwnerRefused => EvidenceUnavailable,

        // Still row-scoped: the reveal was refused, the rest of the page is unaffected.
        SamOpportunityClientStatus.EphemeralKeyRing =>
            "This host's Data Protection key ring is ephemeral, so this opportunity's stored "
            + "messages cannot be decrypted. Configure a durable key ring first.",

        SamOpportunityClientStatus.InvalidRequest =>
            "That reveal was rejected before anything was decrypted.",

        _ => "The reveal failed for this opportunity."
    };
}
