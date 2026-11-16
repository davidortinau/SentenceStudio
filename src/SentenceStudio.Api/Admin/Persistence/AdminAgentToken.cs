namespace SentenceStudio.Api.Admin.Persistence;

/// <summary>
/// One token the maintenance agent signs in to the admin API with.
/// </summary>
/// <remarks>
/// <para>
/// <b>The token itself is not stored.</b> It is shown once, when it is created; this row keeps a
/// SHA-256 digest of its secret half, which is 256 random bits, so the digest cannot be turned
/// back into a token and needs no salt or slow hash. A copy of this table lets nobody sign in.
/// </para>
/// <para>
/// A token is live while it is not revoked, not expired, and the admin who created it is still an
/// admin. It carries only the permissions it was created with, and no route that changes an
/// account accepts a token at all.
/// </para>
/// </remarks>
public sealed class AdminAgentToken
{
    /// <summary>The public half of the token. Names the token in the audit log.</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>What the admin called it, for example <c>weekday check</c>.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Uppercase hex SHA-256 of the token's secret half.</summary>
    public string SecretHash { get; set; } = string.Empty;

    public AdminTokenPermissions Permissions { get; set; }

    /// <summary>The Identity user id of the admin who created it.</summary>
    public string CreatedByUserId { get; set; } = string.Empty;

    public DateTime CreatedAtUtc { get; set; }

    public DateTime ExpiresAtUtc { get; set; }

    /// <summary>When an admin revoked it. Null while it has not been revoked.</summary>
    public DateTime? RevokedAtUtc { get; set; }

    /// <summary>
    /// When it last signed in, to within <see cref="AdminAgentTokenLimits.LastUsedResolution"/>.
    /// </summary>
    public DateTime? LastUsedAtUtc { get; set; }
}

/// <summary>Bounds for <see cref="AdminAgentToken"/>.</summary>
public static class AdminAgentTokenLimits
{
    /// <summary>32 hex characters: 128 random bits.</summary>
    public const int IdLength = 32;

    public const int MaxNameLength = 64;

    /// <summary>64 hex characters: a SHA-256 digest.</summary>
    public const int SecretHashLength = 64;

    /// <summary>How long a token lasts (the Captain's default, 2026-11-15).</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromDays(30);

    /// <summary>A sign-in rewrites <see cref="AdminAgentToken.LastUsedAtUtc"/> at most this often.</summary>
    public static readonly TimeSpan LastUsedResolution = TimeSpan.FromMinutes(5);

    /// <summary>How many tokens may be live at once. One task needs one.</summary>
    public const int MaxLiveTokens = 10;
}
