using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SentenceStudio.Api.Admin.Persistence;
using SentenceStudio.Api.Auth;
using SentenceStudio.Data;

namespace SentenceStudio.Api.Admin;

/// <summary>Why a caller is not an admin. Logged, never returned: every refusal is the same 404.</summary>
internal enum AdminDenial
{
    None = 0,
    NotAuthenticated,
    DevelopmentIdentity,
    NoAccount,
    EmailNotConfirmed,
    NeitherRoleNorListed,
    RoleWithoutList,
    ListedWithoutRole,
    TokenNotLive,
    TokenCreatorNotAdmin,
    TokenCreatorPasswordChanged
}

internal readonly record struct AdminAccessResult(AdminActor? Actor, AdminDenial Denial);

/// <summary>
/// Decides whether the caller is an admin, from the database and the server's settings.
/// </summary>
/// <remarks>
/// <para>
/// An admin is a signed-in account with a confirmed email that holds the <see cref="RoleName"/>
/// role in the database <em>and</em> whose email is on <c>Admin:Emails</c>. Either alone gives
/// nothing: a role row written by mistake or by an attacker with database access is inert without
/// the setting, and a setting that names an address is inert until the role is granted by hand.
/// </para>
/// <para>
/// Nothing is taken from the token except the account id. The role, the email and the email's
/// confirmation are read from the database on every request, and the list from the live options,
/// so removing either half takes effect on the next request rather than when a token expires.
/// </para>
/// <para>
/// An agent token is admitted while it is live, the admin who created it still is one by the
/// same check, and that admin's password has not changed since. A token therefore stops with its
/// creator: an empty list still means nobody, and nothing, is admitted, and an admin who changes
/// a password they fear was stolen ends every token made with it.
/// </para>
/// </remarks>
internal sealed class AdminAccessEvaluator(
    ApplicationDbContext db,
    AdminAgentTokenService tokens,
    IOptionsMonitor<AdminOptions> options,
    ILogger<AdminAccessEvaluator> logger)
{
    /// <summary>The Identity role an admin holds. Granted only by <c>scripts/admin-grant.sh</c>.</summary>
    public const string RoleName = "Admin";

    private static readonly string NormalizedRoleName = RoleName.ToUpperInvariant();

    public async Task<AdminAccessResult> EvaluateAsync(ClaimsPrincipal user, CancellationToken cancellationToken)
    {
        if (user.Identity?.IsAuthenticated != true)
        {
            return new AdminAccessResult(null, AdminDenial.NotAuthenticated);
        }

        // The Development fallback signs in every anonymous request as one fixed principal. It
        // names no account, so it can never be an admin, whatever a local database contains.
        if (string.Equals(user.Identity.AuthenticationType, DevAuthHandler.SchemeName, StringComparison.Ordinal))
        {
            return new AdminAccessResult(null, AdminDenial.DevelopmentIdentity);
        }

        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId))
        {
            return new AdminAccessResult(null, AdminDenial.NotAuthenticated);
        }

        return await EvaluateAccountAsync(userId, cancellationToken);
    }

    /// <summary>
    /// Decides whether <paramref name="presented"/> is a live agent token whose creator is still
    /// an admin. The request's own sign-in, if it has one, plays no part.
    /// </summary>
    public async Task<AdminAccessResult> EvaluateTokenAsync(string? presented, CancellationToken cancellationToken)
    {
        var token = await tokens.FindLiveAsync(presented, cancellationToken);
        if (token is null)
        {
            return new AdminAccessResult(null, AdminDenial.TokenNotLive);
        }

        var denial = await EvaluateTokenCreatorAsync(token, cancellationToken);
        if (denial != AdminDenial.None)
        {
            logger.LogWarning("Agent token {TokenId} refused: {Denial}.", token.Id, denial);
            return new AdminAccessResult(null, denial);
        }

        await tokens.TouchAsync(token, cancellationToken);

        // No email and no stamp: a token names no person and can hold no password-again proof.
        return new AdminAccessResult(
            new AdminActor(AdminActorKind.AgentToken, token.Id, Email: null, token.Name, StampDigest: null)
            {
                TokenPermissions = token.Permissions,
                TokenExpiresAtUtc = token.ExpiresAtUtc
            },
            AdminDenial.None);
    }

    /// <summary>
    /// Whether the admin who created <paramref name="token"/> still stands behind it:
    /// <see cref="AdminDenial.None"/> when they are an admin now and their password is the one
    /// they had when they created it.
    /// </summary>
    /// <param name="forDisplay">
    /// True when nobody is asking to be admitted: the Tokens page is working out what to show.
    /// Nothing is logged then, or each load of that page would read as a refused access attempt.
    /// </param>
    public async Task<AdminDenial> EvaluateTokenCreatorAsync(
        AdminAgentToken token, CancellationToken cancellationToken, bool forDisplay = false)
    {
        var creator = await EvaluateAccountAsync(token.CreatedByUserId, cancellationToken, logDenials: !forDisplay);
        if (creator.Actor is null)
        {
            return AdminDenial.TokenCreatorNotAdmin;
        }

        // Both are digests of a security stamp, not secrets, so an ordinary comparison is enough.
        return string.Equals(creator.Actor.StampDigest, token.CreatedByStampDigest, StringComparison.Ordinal)
            && token.CreatedByStampDigest.Length > 0
            ? AdminDenial.None
            : AdminDenial.TokenCreatorPasswordChanged;
    }

    private async Task<AdminAccessResult> EvaluateAccountAsync(
        string userId, CancellationToken cancellationToken, bool logDenials = true)
    {
        var account = await db.Users
            .AsNoTracking()
            .Where(u => u.Id == userId)
            .Select(u => new
            {
                u.Id,
                u.Email,
                u.EmailConfirmed,
                u.DisplayName,
                u.SecurityStamp,
                HoldsRole = db.UserRoles.Any(ur =>
                    ur.UserId == u.Id
                    && db.Roles.Any(r => r.Id == ur.RoleId && r.NormalizedName == NormalizedRoleName))
            })
            .SingleOrDefaultAsync(cancellationToken);

        if (account is null)
        {
            return new AdminAccessResult(null, AdminDenial.NoAccount);
        }

        var email = AdminOptions.NormalizeEmail(account.Email);
        var listed = email is not null
            && options.CurrentValue.Emails.Any(entry =>
                string.Equals(AdminOptions.NormalizeEmail(entry), email, StringComparison.Ordinal));

        if (!account.HoldsRole || !listed)
        {
            var denial = (account.HoldsRole, listed) switch
            {
                (true, false) => AdminDenial.RoleWithoutList,
                (false, true) => AdminDenial.ListedWithoutRole,
                _ => AdminDenial.NeitherRoleNorListed
            };

            // An ordinary learner reaching an admin route is not an event (the web app asks on
            // every page load whether to show the link). Half an admin is: it is either a grant in
            // progress or somebody holding one of the two keys.
            if (logDenials && denial is not AdminDenial.NeitherRoleNorListed)
            {
                logger.LogWarning("Admin access refused for user {UserId}: {Denial}", account.Id, denial);
            }

            return new AdminAccessResult(null, denial);
        }

        // The list names addresses, so the address has to be one the account has proved it owns.
        if (!account.EmailConfirmed)
        {
            if (logDenials)
            {
                logger.LogWarning("Admin access refused for user {UserId}: {Denial}", account.Id, AdminDenial.EmailNotConfirmed);
            }

            return new AdminAccessResult(null, AdminDenial.EmailNotConfirmed);
        }

        return new AdminAccessResult(
            new AdminActor(
                AdminActorKind.Admin, account.Id, account.Email, account.DisplayName,
                AdminStepUpService.DigestOf(account.SecurityStamp)),
            AdminDenial.None);
    }
}
