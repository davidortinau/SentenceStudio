namespace SentenceStudio.Api.Coach.Persistence.History;

/// <summary>
/// The trusted owner of a durable coach history record.
/// </summary>
/// <remarks>
/// <para>
/// <b>v1 authority is <see cref="UserProfileId"/> and nothing else.</b> Every history query
/// filters on that value alone. <see cref="TenantId"/> is carried so rows written today can be
/// classified when tenancy becomes an authority boundary, but no store reads it, no index
/// includes it, and no ciphertext is bound to it. Treating a nullable, never-validated column
/// as an authority key is how a partially-populated tenant field turns into a cross-tenant
/// read.
/// </para>
/// <para>
/// The value must come from the server's request scope. A store never derives an owner, never
/// accepts one from a request body, and never falls back to "all rows" when the owner is
/// missing — <see cref="IsEmpty"/> is answered with the empty result, matching the multi-tenant
/// scoping rule the repositories already follow.
/// </para>
/// </remarks>
public readonly record struct CoachOwner
{
    private CoachOwner(string userProfileId, string? tenantId)
    {
        UserProfileId = userProfileId;
        TenantId = tenantId;
    }

    /// <summary>The owning learner. The only ownership authority in v1.</summary>
    public string UserProfileId { get; init; }

    /// <summary>
    /// Forward-compatibility classification only. Never an authority value, never a key, and
    /// never part of a protection purpose.
    /// </summary>
    public string? TenantId { get; init; }

    /// <summary>True when no trusted learner is present, so every store must return "no data".</summary>
    public bool IsEmpty => string.IsNullOrWhiteSpace(UserProfileId);

    /// <summary>
    /// Builds an owner from a trusted user profile id. Throws when the id is missing, because a
    /// caller that cannot name the owner has a bug the store cannot repair.
    /// </summary>
    public static CoachOwner ForUser(string userProfileId, string? tenantId = null)
    {
        if (string.IsNullOrWhiteSpace(userProfileId))
        {
            throw new ArgumentException("A coach history owner requires a trusted user profile id.", nameof(userProfileId));
        }

        return new CoachOwner(userProfileId.Trim(), Normalize(tenantId));
    }

    /// <summary>
    /// Builds an owner without throwing. Returns false when the id is missing so a caller can
    /// degrade to the empty result rather than 500 a request.
    /// </summary>
    public static bool TryCreate(string? userProfileId, string? tenantId, out CoachOwner owner)
    {
        if (string.IsNullOrWhiteSpace(userProfileId))
        {
            owner = default;
            return false;
        }

        owner = new CoachOwner(userProfileId.Trim(), Normalize(tenantId));
        return true;
    }

    private static string? Normalize(string? tenantId) =>
        string.IsNullOrWhiteSpace(tenantId) ? null : tenantId.Trim();
}
