namespace SentenceStudio.Api.Admin;

/// <summary>
/// What an agent token may do. Stored as an integer on the token's row, so values may only be
/// added, never renumbered.
/// </summary>
/// <remarks>
/// There is no permission for the account tools (confirm, unlock, reset link, switch off,
/// delete), for creating or revoking tokens, or for confirming a password: no route for those
/// accepts a token, whatever it holds.
/// </remarks>
[Flags]
public enum AdminTokenPermissions
{
    None = 0,

    /// <summary>Read the feedback inbox: the lists and each report's codes, counts and times.</summary>
    Reports = 1,

    /// <summary>Read learner content: the one flagged question and answer, and saved screen state.</summary>
    LearnerContent = 2,

    /// <summary>Set a report's status and its linked GitHub issue number.</summary>
    Triage = 4
}

/// <summary>How token permissions are written on the wire. Fixed strings, not the member names.</summary>
public static class AdminTokenPermissionWire
{
    public const string Reports = "reports";
    public const string LearnerContent = "learner_content";
    public const string Triage = "triage";

    private static readonly (AdminTokenPermissions Permission, string Code)[] Known =
    [
        (AdminTokenPermissions.Reports, Reports),
        (AdminTokenPermissions.LearnerContent, LearnerContent),
        (AdminTokenPermissions.Triage, Triage)
    ];

    /// <summary>Every bit that names a permission.</summary>
    public const AdminTokenPermissions All =
        AdminTokenPermissions.Reports | AdminTokenPermissions.LearnerContent | AdminTokenPermissions.Triage;

    public static IReadOnlyList<string> Of(AdminTokenPermissions permissions) =>
        [.. Known.Where(k => permissions.HasFlag(k.Permission)).Select(k => k.Code)];

    /// <summary>
    /// Reads a list of codes. False for an empty list, a repeat or a code that names nothing, so a
    /// token is never created with fewer permissions than its admin believes it has.
    /// </summary>
    public static bool TryParse(IReadOnlyList<string?>? codes, out AdminTokenPermissions permissions)
    {
        permissions = AdminTokenPermissions.None;
        if (codes is null || codes.Count == 0)
        {
            return false;
        }

        foreach (var code in codes)
        {
            var match = Known.FirstOrDefault(k => string.Equals(k.Code, code, StringComparison.Ordinal));
            if (match.Code is null || permissions.HasFlag(match.Permission))
            {
                permissions = AdminTokenPermissions.None;
                return false;
            }

            permissions |= match.Permission;
        }

        return true;
    }
}

/// <summary>
/// Marks an admin route an agent token may call, and the permission it needs for it.
/// </summary>
/// <param name="Required">
/// The permission the token must hold. <see cref="AdminTokenPermissions.None"/> admits any live
/// token.
/// </param>
/// <remarks>
/// A route without this refuses every token. That is the default on purpose: the account tools
/// stay closed to tokens by never being marked, not by remembering to exclude them.
/// </remarks>
public sealed record AdminTokenAccessMetadata(AdminTokenPermissions Required);

public static class AdminTokenRouteExtensions
{
    /// <summary>An agent token holding <paramref name="required"/> may call this route.</summary>
    public static TBuilder AllowAdminToken<TBuilder>(this TBuilder builder, AdminTokenPermissions required)
        where TBuilder : IEndpointConventionBuilder =>
        builder.WithMetadata(new AdminTokenAccessMetadata(required));
}

internal static class AdminTokenAccess
{
    /// <summary>
    /// Whether <paramref name="actor"/> may use <paramref name="endpoint"/> as far as tokens are
    /// concerned. An admin account always may; a token only where the route says so and only
    /// holding what the route asks for.
    /// </summary>
    public static bool IsAllowed(Endpoint? endpoint, AdminActor actor)
    {
        if (actor.Kind != AdminActorKind.AgentToken)
        {
            return actor.Kind == AdminActorKind.Admin;
        }

        return endpoint?.Metadata.GetMetadata<AdminTokenAccessMetadata>() is { } access
            && (actor.TokenPermissions & access.Required) == access.Required;
    }
}
