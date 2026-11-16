namespace SentenceStudio.Api.Admin;

/// <summary>Who is acting on an admin route. Stored in the audit log as an integer.</summary>
public enum AdminActorKind
{
    /// <summary>A signed-in account that holds the role and is on the list.</summary>
    Admin = 1,

    /// <summary>The maintenance agent's token. It can never use the account tools.</summary>
    AgentToken = 2
}

/// <summary>
/// The caller of an admin route, as resolved from the database and the server's settings on this
/// request. Never built from token claims alone.
/// </summary>
/// <param name="Kind">Admin account or agent token.</param>
/// <param name="Id">The Identity user id, or the token id. This is what the audit log records.</param>
/// <param name="Email">The account's address, for display to the admin themself. Null for a token.</param>
/// <param name="DisplayName">The account's display name, or the name a token was given.</param>
/// <param name="StampDigest">
/// <see cref="AdminStepUpService.DigestOf"/> of the account's current Identity security stamp. A
/// password-again proof is bound to it, so a password change ends every proof issued before it.
/// The stamp itself never leaves the query that read it, so nothing that prints this record can
/// print it.
/// </param>
public sealed record AdminActor(
    AdminActorKind Kind,
    string Id,
    string? Email,
    string? DisplayName,
    string? StampDigest)
{
    /// <summary>What an agent token may do. Always none for an admin account, which needs none.</summary>
    public AdminTokenPermissions TokenPermissions { get; init; }

    /// <summary>When an agent token stops working. Null for an admin account.</summary>
    public DateTime? TokenExpiresAtUtc { get; init; }
}

/// <summary>Carries the resolved <see cref="AdminActor"/> for the rest of the request.</summary>
internal interface IAdminActorFeature
{
    AdminActor Actor { get; }
}

internal sealed record AdminActorFeature(AdminActor Actor) : IAdminActorFeature;

/// <summary>The audit row the middleware opened for this request.</summary>
internal sealed record AdminAuditFeature(long EntryId);

internal static class AdminHttpContextExtensions
{
    /// <summary>The actor the admin policy resolved, or null when it did not run or refused.</summary>
    public static AdminActor? FindAdminActor(this HttpContext http) =>
        http.Features.Get<IAdminActorFeature>()?.Actor;

    /// <summary>
    /// The actor the admin policy resolved. Throws when there is none: a handler on an admin route
    /// must never run for a caller the policy did not admit.
    /// </summary>
    public static AdminActor GetAdminActor(this HttpContext http) =>
        http.FindAdminActor()
        ?? throw new InvalidOperationException("An admin route ran without an admitted admin caller.");

    /// <summary>The id of this request's own audit row, which is still open while the handler runs.</summary>
    public static long? FindAdminAuditEntryId(this HttpContext http) =>
        http.Features.Get<AdminAuditFeature>()?.EntryId;
}
