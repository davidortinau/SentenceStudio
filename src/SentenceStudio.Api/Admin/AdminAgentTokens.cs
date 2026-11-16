using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using SentenceStudio.Api.Admin.Persistence;

namespace SentenceStudio.Api.Admin;

/// <summary>A token as it is shown the one time it is created.</summary>
internal sealed record AdminIssuedToken(AdminAgentToken Row, string Token);

/// <summary>
/// Creates, checks and revokes the tokens the maintenance agent signs in to the admin API with.
/// </summary>
/// <remarks>
/// <para>
/// A token is <c>ssadm_</c>, a 32-character id, an underscore and a 43-character secret. The id
/// finds the row and names the token in the audit log; the secret is 256 random bits, of which
/// only a SHA-256 digest is stored. It travels in <see cref="HeaderName"/>, not in
/// <c>Authorization</c>, which belongs to account sign-in.
/// </para>
/// <para>
/// This is the token's own sign-in path. A request that carries the header is judged as a token
/// and nothing else: a bad token is refused even when the same request also carries a valid
/// account sign-in.
/// </para>
/// </remarks>
internal sealed class AdminAgentTokenService(AdminDbContext db, TimeProvider time, ILogger<AdminAgentTokenService> logger)
{
    /// <summary>The request header a token travels in.</summary>
    public const string HeaderName = "X-Admin-Token";

    public const string Prefix = "ssadm_";

    private const char Separator = '_';
    private const int SecretBytes = 32;

    /// <summary>The length of 32 random bytes in unpadded base64url.</summary>
    private const int SecretLength = 43;

    private static readonly int TokenLength =
        Prefix.Length + AdminAgentTokenLimits.IdLength + 1 + SecretLength;

    /// <summary>
    /// Creates a token for <paramref name="createdByUserId"/> and returns it with its row. The
    /// token cannot be read back afterwards.
    /// </summary>
    /// <param name="createdByStampDigest">
    /// The digest of the creator's security stamp, as the admin policy read it for this request.
    /// </param>
    public async Task<AdminIssuedToken> CreateAsync(
        string createdByUserId, string createdByStampDigest, string name, AdminTokenPermissions permissions,
        CancellationToken cancellationToken)
    {
        var id = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(AdminAgentTokenLimits.IdLength / 2));
        var secret = Base64UrlEncode(RandomNumberGenerator.GetBytes(SecretBytes));
        var now = time.GetUtcNow().UtcDateTime;

        var row = new AdminAgentToken
        {
            Id = id,
            Name = name,
            SecretHash = HashOf(secret),
            Permissions = permissions,
            CreatedByUserId = createdByUserId,
            CreatedByStampDigest = createdByStampDigest,
            CreatedAtUtc = now,
            ExpiresAtUtc = now.Add(AdminAgentTokenLimits.Lifetime)
        };

        db.AgentTokens.Add(row);
        await db.SaveChangesAsync(cancellationToken);

        return new AdminIssuedToken(row, string.Concat(Prefix, id, Separator.ToString(), secret));
    }

    /// <summary>
    /// The row behind <paramref name="presented"/> when it is a real token that is neither revoked
    /// nor expired; otherwise null. Says nothing about its creator: see
    /// <see cref="AdminAccessEvaluator.EvaluateTokenAsync"/>.
    /// </summary>
    public async Task<AdminAgentToken?> FindLiveAsync(string? presented, CancellationToken cancellationToken)
    {
        if (!TryParse(presented, out var id, out var secret))
        {
            return null;
        }

        var row = await db.AgentTokens.AsNoTracking().SingleOrDefaultAsync(t => t.Id == id, cancellationToken);
        if (row is null
            || !CryptographicOperations.FixedTimeEquals(
                Encoding.ASCII.GetBytes(HashOf(secret)), Encoding.ASCII.GetBytes(row.SecretHash)))
        {
            return null;
        }

        if (row.RevokedAtUtc is not null || row.ExpiresAtUtc <= time.GetUtcNow().UtcDateTime)
        {
            logger.LogWarning("Agent token {TokenId} was presented after it was revoked or expired.", row.Id);
            return null;
        }

        return row;
    }

    /// <summary>
    /// Notes that the token signed in. Written at most once per
    /// <see cref="AdminAgentTokenLimits.LastUsedResolution"/>, and a failure here does not fail
    /// the request it rode on: the audit log is the record of what the token did.
    /// </summary>
    public async Task TouchAsync(AdminAgentToken token, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow().UtcDateTime;
        var stale = now.Subtract(AdminAgentTokenLimits.LastUsedResolution);
        if (token.LastUsedAtUtc is { } last && last > stale)
        {
            return;
        }

        try
        {
            await db.AgentTokens
                .Where(t => t.Id == token.Id && (t.LastUsedAtUtc == null || t.LastUsedAtUtc <= stale))
                .ExecuteUpdateAsync(update => update.SetProperty(t => t.LastUsedAtUtc, (DateTime?)now), cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Agent token {TokenId}: its last-used time could not be written.", token.Id);
        }
    }

    /// <summary>Every token, newest first. Never the secret or its digest.</summary>
    public async Task<List<AdminAgentToken>> ListAsync(CancellationToken cancellationToken) =>
        await db.AgentTokens.AsNoTracking()
            .OrderByDescending(t => t.CreatedAtUtc)
            .ThenBy(t => t.Id)
            .ToListAsync(cancellationToken);

    /// <summary>How many tokens could sign in right now.</summary>
    public async Task<int> CountLiveAsync(CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow().UtcDateTime;
        return await db.AgentTokens.CountAsync(t => t.RevokedAtUtc == null && t.ExpiresAtUtc > now, cancellationToken);
    }

    /// <summary>
    /// Revokes the token. Returns the row as it now stands, or null when there is no such token.
    /// Revoking a token that is already revoked changes nothing and is not an error.
    /// </summary>
    public async Task<AdminAgentToken?> RevokeAsync(string id, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow().UtcDateTime;
        await db.AgentTokens
            .Where(t => t.Id == id && t.RevokedAtUtc == null)
            .ExecuteUpdateAsync(update => update.SetProperty(t => t.RevokedAtUtc, (DateTime?)now), cancellationToken);

        return await db.AgentTokens.AsNoTracking().SingleOrDefaultAsync(t => t.Id == id, cancellationToken);
    }

    private static bool TryParse(string? presented, out string id, out string secret)
    {
        id = secret = string.Empty;
        if (presented is null
            || presented.Length != TokenLength
            || !presented.StartsWith(Prefix, StringComparison.Ordinal)
            || presented[Prefix.Length + AdminAgentTokenLimits.IdLength] != Separator)
        {
            return false;
        }

        id = presented.Substring(Prefix.Length, AdminAgentTokenLimits.IdLength);
        secret = presented[(Prefix.Length + AdminAgentTokenLimits.IdLength + 1)..];
        return id.All(c => c is (>= '0' and <= '9') or (>= 'a' and <= 'f'))
            && secret.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');
    }

    private static string HashOf(string secret) =>
        Convert.ToHexString(SHA256.HashData(Encoding.ASCII.GetBytes(secret)));

    private static string Base64UrlEncode(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
