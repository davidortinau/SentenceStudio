using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SentenceStudio.Data;
using SentenceStudio.Shared.Models;

namespace SentenceStudio.Api.Admin;

internal enum AdminPasswordVerdict
{
    Incorrect = 0,
    Correct = 1,
    LockedOut = 2,
    NoAccount = 3
}

/// <param name="SecurityStamp">The account's stamp as read with the password, when it was correct.</param>
internal readonly record struct AdminPasswordCheckResult(AdminPasswordVerdict Verdict, string? SecurityStamp = null);

/// <summary>
/// Checks an admin's password for the password-again proof, with a lockout that holds when the
/// guesses arrive in parallel.
/// </summary>
/// <remarks>
/// <para>
/// <b>Count first, verify second.</b> The attempt is added to the account's failed-access count
/// with one atomic statement before the password is looked at, and the password is looked at only
/// when the count read back is still within Identity's limit. However many requests race, no more
/// than that limit are ever verified between lockouts.
/// </para>
/// <para>
/// Identity's own sequence (check the password, then <c>AccessFailedAsync</c>) reads the count,
/// adds one in memory and writes it back under an optimistic concurrency stamp. Of a hundred
/// simultaneous wrong guesses one write wins and ninety-nine are refused as concurrency
/// failures, so the hundred guesses cost one. That is tolerable behind the sign-in routes'
/// per-address limiter; it is not for a route whose callers already hold an admin's session.
/// </para>
/// <para>
/// The count, the lock and the limit are Identity's own (<c>AccessFailedCount</c>,
/// <c>LockoutEnd</c>, <c>IdentityOptions.Lockout</c>), so a lock earned here also refuses sign-in
/// and one earned at sign-in also refuses here.
/// </para>
/// </remarks>
internal sealed class AdminPasswordCheck(
    ApplicationDbContext db,
    UserManager<ApplicationUser> users,
    IOptions<IdentityOptions> identity,
    TimeProvider time,
    ILogger<AdminPasswordCheck> logger)
{
    public async Task<AdminPasswordCheckResult> CheckAsync(
        string userId, string password, CancellationToken cancellationToken)
    {
        var lockout = identity.Value.Lockout;
        var now = time.GetUtcNow();
        var account = db.Users.Where(u => u.Id == userId);

        var counted = await account.ExecuteUpdateAsync(
            update => update.SetProperty(u => u.AccessFailedCount, u => u.AccessFailedCount + 1),
            cancellationToken);
        if (counted == 0)
        {
            return new AdminPasswordCheckResult(AdminPasswordVerdict.NoAccount);
        }

        var state = await account
            .AsNoTracking()
            .Select(u => new { u.AccessFailedCount, u.LockoutEnd })
            .SingleAsync(cancellationToken);

        if (state.LockoutEnd > now)
        {
            // Already locked: this attempt was never going to be looked at, so it does not count
            // toward the next lockout either.
            await account.ExecuteUpdateAsync(
                update => update.SetProperty(u => u.AccessFailedCount, u => u.AccessFailedCount - 1),
                cancellationToken);
            logger.LogWarning("Admin password check refused for user {UserId}: account locked out", userId);
            return new AdminPasswordCheckResult(AdminPasswordVerdict.LockedOut);
        }

        if (state.AccessFailedCount > lockout.MaxFailedAccessAttempts)
        {
            // More attempts are in flight than the limit allows. This one is not verified.
            await LockAsync(account, now + lockout.DefaultLockoutTimeSpan, cancellationToken);
            logger.LogWarning("Admin password check refused for user {UserId}: too many attempts at once", userId);
            return new AdminPasswordCheckResult(AdminPasswordVerdict.LockedOut);
        }

        var user = await users.FindByIdAsync(userId);
        if (user is null)
        {
            return new AdminPasswordCheckResult(AdminPasswordVerdict.NoAccount);
        }

        if (!await users.CheckPasswordAsync(user, password))
        {
            if (state.AccessFailedCount >= lockout.MaxFailedAccessAttempts)
            {
                await LockAsync(account, now + lockout.DefaultLockoutTimeSpan, cancellationToken);
            }

            logger.LogWarning("Admin password check failed for user {UserId}", userId);
            return new AdminPasswordCheckResult(AdminPasswordVerdict.Incorrect);
        }

        await account.ExecuteUpdateAsync(
            update => update.SetProperty(u => u.AccessFailedCount, 0), cancellationToken);
        return new AdminPasswordCheckResult(AdminPasswordVerdict.Correct, user.SecurityStamp);
    }

    /// <summary>Locks the account until <paramref name="until"/> and starts its count again, as Identity does.</summary>
    private static Task LockAsync(IQueryable<ApplicationUser> account, DateTimeOffset until, CancellationToken cancellationToken) =>
        account.ExecuteUpdateAsync(
            update => update
                .SetProperty(u => u.LockoutEnd, until)
                .SetProperty(u => u.AccessFailedCount, 0),
            cancellationToken);
}
