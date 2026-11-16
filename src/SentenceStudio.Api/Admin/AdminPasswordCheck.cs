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
/// <para>
/// <b>Once counted, always settled.</b> Counting first means a check that stops half way leaves
/// a wrong guess on the account whatever the password was. So the caller's cancellation is
/// honoured only before the attempt is counted. From there the check runs to its verdict on its
/// own clock (<see cref="SettleTimeout"/>), even when the caller has stopped waiting, and an
/// attempt that reaches no verdict (a database fault, the clock running out) is taken back off
/// the count. A password that was looked at and found wrong stays counted whatever fails after
/// it. (The web app waits 15 seconds. Without this, a slow database would cost an admin an
/// attempt for each right password it timed out on, and five of them a lockout.)
/// </para>
/// <para>
/// One case is left as it falls: when the counting statement itself fails, nobody knows whether
/// the database applied it. It is not taken back, because taking back an attempt that was never
/// counted would remove somebody's real wrong guess.
/// </para>
/// <para>
/// <b>The count never goes below zero.</b> An attempt that finds the account locked takes itself
/// back off the count. In a burst, the attempt that earns the lock also resets the count, so the
/// attempts counted before it and refused after it have nothing left to take back; subtracting
/// anyway would leave a negative count, which is that many extra guesses once the lock ends.
/// Taking back cannot tell whose attempt it removes: after a reset in between it can remove a
/// later one. That needs a fault at the right moment, which a caller cannot order, and costs one
/// counted guess.
/// </para>
/// <para>
/// <b>Every write here changes the account's concurrency stamp.</b> Identity's own writes (a
/// failed sign-in, for one) save the whole account row as it was read, under that stamp. Without
/// a new stamp, a sign-in that read the account before these statements and saved after them
/// would put the old count and the old lock back: wrong guesses made here would be forgotten, and
/// a lock earned here erased. With it, that stale save is refused.
/// </para>
/// </remarks>
internal sealed class AdminPasswordCheck(
    ApplicationDbContext db,
    UserManager<ApplicationUser> users,
    IOptions<IdentityOptions> identity,
    TimeProvider time,
    ILogger<AdminPasswordCheck> logger)
{
    /// <summary>How long a counted attempt may take to reach its verdict.</summary>
    internal static readonly TimeSpan SettleTimeout = TimeSpan.FromSeconds(10);

    /// <summary>How long taking back an attempt that reached no verdict may take.</summary>
    internal static readonly TimeSpan UndoTimeout = TimeSpan.FromSeconds(5);

    public async Task<AdminPasswordCheckResult> CheckAsync(
        string userId, string password, CancellationToken cancellationToken)
    {
        // The last moment the caller can leave without a trace: see the remarks.
        cancellationToken.ThrowIfCancellationRequested();

        var lockout = identity.Value.Lockout;
        var now = time.GetUtcNow();
        var account = db.Users.Where(u => u.Id == userId);

        // Not the caller's token, from the count on. A cancellation that lands while the count is
        // on the wire can still be committed by the database, and then nothing would settle it.
        using var settleClock = new CancellationTokenSource(SettleTimeout, time);
        var settle = settleClock.Token;

        var countStamp = NewStamp();
        var counted = await account.ExecuteUpdateAsync(
            update => update
                .SetProperty(u => u.AccessFailedCount, u => u.AccessFailedCount + 1)
                .SetProperty(u => u.ConcurrencyStamp, countStamp),
            settle);
        if (counted == 0)
        {
            return new AdminPasswordCheckResult(AdminPasswordVerdict.NoAccount);
        }

        var foundWrong = false;
        try
        {
            var state = await account
                .AsNoTracking()
                .Select(u => new { u.AccessFailedCount, u.LockoutEnd })
                .SingleAsync(settle);

            if (state.LockoutEnd > now)
            {
                // Already locked: this attempt was never going to be looked at, so it does not
                // count toward the next lockout either.
                await UncountAsync(account, settle);
                logger.LogWarning("Admin password check refused for user {UserId}: account locked out", userId);
                return new AdminPasswordCheckResult(AdminPasswordVerdict.LockedOut);
            }

            if (state.AccessFailedCount > lockout.MaxFailedAccessAttempts)
            {
                // More attempts are in flight than the limit allows. This one is not verified.
                await LockAsync(account, now + lockout.DefaultLockoutTimeSpan, settle);
                logger.LogWarning("Admin password check refused for user {UserId}: too many attempts at once", userId);
                return new AdminPasswordCheckResult(AdminPasswordVerdict.LockedOut);
            }

            var user = await users.FindByIdAsync(userId);
            if (user is null)
            {
                return new AdminPasswordCheckResult(AdminPasswordVerdict.NoAccount);
            }

            var correct = await users.CheckPasswordAsync(user, password);

            // Reading the account and hashing the password do not watch the clock above. What
            // the verdict has to write gets a clock of its own, so that a slow look at the
            // password cannot leave a lock unwritten or a right password counted.
            using var verdictClock = new CancellationTokenSource(SettleTimeout, time);

            if (!correct)
            {
                foundWrong = true;
                if (state.AccessFailedCount >= lockout.MaxFailedAccessAttempts)
                {
                    await LockAsync(account, now + lockout.DefaultLockoutTimeSpan, verdictClock.Token);
                }

                logger.LogWarning("Admin password check failed for user {UserId}", userId);
                return new AdminPasswordCheckResult(AdminPasswordVerdict.Incorrect);
            }

            var clearStamp = NewStamp();
            await account.ExecuteUpdateAsync(
                update => update
                    .SetProperty(u => u.AccessFailedCount, 0)
                    .SetProperty(u => u.ConcurrencyStamp, clearStamp),
                verdictClock.Token);
            return new AdminPasswordCheckResult(AdminPasswordVerdict.Correct, user.SecurityStamp);
        }
        catch when (!foundWrong)
        {
            // No verdict, or a right password whose count could not be cleared. A password that
            // was looked at and found wrong keeps its count whatever failed afterwards.
            await UndoCountAsync(account, userId);
            throw;
        }
    }

    /// <summary>A fresh concurrency stamp. Callers take it into a local so the statement carries a value.</summary>
    private static string NewStamp() => Guid.NewGuid().ToString();

    /// <summary>Takes one attempt back off the count, never below zero (see the remarks).</summary>
    private static Task UncountAsync(IQueryable<ApplicationUser> account, CancellationToken cancellationToken)
    {
        var stamp = NewStamp();
        return account
            .Where(u => u.AccessFailedCount > 0)
            .ExecuteUpdateAsync(
                update => update
                    .SetProperty(u => u.AccessFailedCount, u => u.AccessFailedCount - 1)
                    .SetProperty(u => u.ConcurrencyStamp, stamp),
                cancellationToken);
    }

    /// <summary>
    /// Takes back an attempt that reached no verdict. Never throws: the fault that ended the
    /// check is the one the caller must see.
    /// </summary>
    private async Task UndoCountAsync(IQueryable<ApplicationUser> account, string userId)
    {
        try
        {
            using var undoClock = new CancellationTokenSource(UndoTimeout, time);
            await UncountAsync(account, undoClock.Token);
        }
        catch (Exception ex)
        {
            logger.LogError(ex,
                "Admin password check for user {UserId} reached no verdict and its counted attempt could not be taken back",
                userId);
        }
    }

    /// <summary>Locks the account until <paramref name="until"/> and starts its count again, as Identity does.</summary>
    private static Task LockAsync(IQueryable<ApplicationUser> account, DateTimeOffset until, CancellationToken cancellationToken)
    {
        var stamp = NewStamp();
        return account.ExecuteUpdateAsync(
            update => update
                .SetProperty(u => u.LockoutEnd, until)
                .SetProperty(u => u.AccessFailedCount, 0)
                .SetProperty(u => u.ConcurrencyStamp, stamp),
            cancellationToken);
    }
}
