using System.Globalization;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace SentenceStudio.Api.Feedback.Persistence;

/// <summary>
/// Whether a limited action may proceed, and — when it may not — when it could.
/// </summary>
/// <param name="Allowed">True when the event was recorded and the caller may proceed.</param>
/// <param name="RetryAfter">
/// How long the caller must wait before the request could possibly succeed. Zero when allowed.
/// Never a guess: it is derived from the recorded instants, so retrying earlier is guaranteed to
/// be refused and retrying after it is refused only by a limit that has since been consumed by
/// somebody else.
/// </param>
/// <param name="Reason">A closed code, or null when allowed.</param>
public readonly record struct FeedbackRateDecision(bool Allowed, TimeSpan RetryAfter, string? Reason)
{
    /// <summary>The decision for an action that may proceed.</summary>
    public static FeedbackRateDecision Allow() => new(true, TimeSpan.Zero, null);

    /// <summary>The decision for an action that must wait.</summary>
    public static FeedbackRateDecision Deny(TimeSpan retryAfter) =>
        new(false, retryAfter < TimeSpan.Zero ? TimeSpan.Zero : retryAfter, FeedbackFailureCodes.RateLimited);

    /// <summary>Retry-After, in whole seconds, never below one.</summary>
    public int RetryAfterSeconds =>
        Math.Max(1, (int)Math.Ceiling(RetryAfter.TotalSeconds));
}

/// <summary>Durable, per-owner limits on the two feedback actions that cost something.</summary>
public interface IFeedbackRateLimiter
{
    /// <summary>
    /// Records one event against <paramref name="kind"/> if the limits allow it. The check and the
    /// record are one atomic step; a caller that receives <c>Allowed</c> has already consumed its
    /// budget.
    /// </summary>
    Task<FeedbackRateDecision> TryConsumeAsync(
        string userProfileId, FeedbackRateKind kind, CancellationToken cancellationToken = default);

    /// <summary>
    /// Answers the same question without recording anything. Used to refuse a request early,
    /// before it does work that would have to be undone — never as the only check, because between
    /// this and the consume another replica may have taken the last slot.
    /// </summary>
    Task<FeedbackRateDecision> PeekAsync(
        string userProfileId, FeedbackRateKind kind, CancellationToken cancellationToken = default);
}

/// <summary>
/// The limiter, as a compare-and-swap over one row per (owner, limit).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why not an in-memory limiter.</b> The API runs as several replicas behind a load balancer,
/// and a per-process counter multiplies every limit by the replica count while appearing to work
/// perfectly in a single-instance test. It also resets on every deployment, which is the moment a
/// misbehaving client is most likely to be retrying.
/// </para>
/// <para>
/// <b>Why not an append-only event table.</b> Counting rows and then inserting one is two
/// statements, and two replicas can interleave them so that both observe "one slot left" and both
/// take it. Making that safe requires <c>SERIALIZABLE</c> plus a serialisation-failure retry loop —
/// correct, but it puts the guarantee in an isolation level that a future <c>DbContext</c> change
/// can silently lower.
/// </para>
/// <para>
/// <b>What this does instead.</b> One row per (owner, limit) holding the instants inside the
/// window. A pass reads it, prunes, decides, and writes back with an <c>UPDATE … WHERE Version =
/// @read</c>. The database reports how many rows that matched: one means this caller's decision was
/// made against state nobody has changed since, zero means somebody else committed first and the
/// pass restarts from a fresh read. The guarantee is a row count, not an isolation level.
/// </para>
/// </remarks>
public sealed class FeedbackRateLimiter : IFeedbackRateLimiter
{
    private readonly FeedbackDbContext _db;
    private readonly TimeProvider _time;
    private readonly FeedbackOptions _options;
    private readonly ILogger<FeedbackRateLimiter> _logger;

    public FeedbackRateLimiter(
        FeedbackDbContext db,
        TimeProvider time,
        IOptions<FeedbackOptions> options,
        ILogger<FeedbackRateLimiter> logger)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _options = (options ?? throw new ArgumentNullException(nameof(options))).Value;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task<FeedbackRateDecision> PeekAsync(
        string userProfileId, FeedbackRateKind kind, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(userProfileId))
        {
            return DenyUnowned(kind);
        }

        var policy = PolicyFor(kind);
        var now = _time.GetUtcNow();

        var row = await ReadAsync(userProfileId, kind, cancellationToken).ConfigureAwait(false);
        var stamps = Prune(Parse(row?.RecentTicksCsv), now, policy.Window);

        return Evaluate(stamps, now, policy);
    }

    /// <inheritdoc />
    public async Task<FeedbackRateDecision> TryConsumeAsync(
        string userProfileId, FeedbackRateKind kind, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(userProfileId))
        {
            return DenyUnowned(kind);
        }

        var policy = PolicyFor(kind);

        for (var attempt = 0; attempt < _options.RateLimitCasAttempts; attempt++)
        {
            var now = _time.GetUtcNow();
            var row = await ReadAsync(userProfileId, kind, cancellationToken).ConfigureAwait(false);
            var stamps = Prune(Parse(row?.RecentTicksCsv), now, policy.Window);

            var decision = Evaluate(stamps, now, policy);
            if (!decision.Allowed)
            {
                return decision;
            }

            stamps.Add(now.UtcDateTime.Ticks);
            var serialized = Serialize(stamps);

            if (row is null)
            {
                if (await TryInsertAsync(userProfileId, kind, serialized, now, cancellationToken)
                        .ConfigureAwait(false))
                {
                    return FeedbackRateDecision.Allow();
                }

                // Another replica created the row between the read and the insert. Re-read.
                continue;
            }

            var readVersion = row.Version;
            var updated = await _db.FeedbackRateWindows
                .Where(w => w.UserProfileId == userProfileId
                            && w.Kind == kind
                            && w.Version == readVersion)
                .ExecuteUpdateAsync(
                    set => set
                        .SetProperty(w => w.RecentTicksCsv, serialized)
                        .SetProperty(w => w.UpdatedAtUtc, now.UtcDateTime)
                        .SetProperty(w => w.Version, readVersion + 1),
                    cancellationToken)
                .ConfigureAwait(false);

            if (updated == 1)
            {
                return FeedbackRateDecision.Allow();
            }
        }

        // Every attempt lost. That is contention, not a policy decision, and the honest answer is
        // to refuse: admitting the caller here would be admitting them without a recorded event,
        // which is exactly the over-admission the compare-and-swap exists to prevent.
        _logger.LogWarning(
            "[Feedback] Rate window compare-and-swap exhausted {Attempts} attempts for {Kind}.",
            _options.RateLimitCasAttempts,
            kind);

        return FeedbackRateDecision.Deny(PolicyFor(kind).ContentionBackoff);
    }

    private async Task<FeedbackRateWindow?> ReadAsync(
        string userProfileId, FeedbackRateKind kind, CancellationToken cancellationToken)
    {
        // AsNoTracking, and the tracker is cleared, so a retry re-reads the database rather than
        // handing back the stale instance the previous attempt loaded.
        _db.ChangeTracker.Clear();

        return await _db.FeedbackRateWindows
            .AsNoTracking()
            .SingleOrDefaultAsync(
                w => w.UserProfileId == userProfileId && w.Kind == kind, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<bool> TryInsertAsync(
        string userProfileId,
        FeedbackRateKind kind,
        string serialized,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        _db.FeedbackRateWindows.Add(new FeedbackRateWindow
        {
            UserProfileId = userProfileId,
            Kind = kind,
            RecentTicksCsv = serialized,
            UpdatedAtUtc = now.UtcDateTime,
            Version = 1
        });

        try
        {
            await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (DbUpdateException)
        {
            // The composite primary key rejected it: somebody else created this owner's window
            // first. Not an error — it is the database arbitrating, which is what it is for.
            _db.ChangeTracker.Clear();
            return false;
        }
    }

    /// <summary>
    /// The decision for a window whose contents are already pruned to <paramref name="policy"/>.
    /// </summary>
    /// <remarks>
    /// Both constraints are evaluated, and the wait returned is the longer of the blocking ones.
    /// Returning the shorter would be a lie the client can detect by trying: a caller told to wait
    /// out the cooldown, who then retries into an exhausted daily window, has been sent back for
    /// nothing.
    /// </remarks>
    private static FeedbackRateDecision Evaluate(
        List<long> stamps, DateTimeOffset now, RatePolicy policy)
    {
        var retryAfter = TimeSpan.Zero;

        if (policy.Cooldown > TimeSpan.Zero && stamps.Count > 0)
        {
            var newest = new DateTimeOffset(stamps[^1], TimeSpan.Zero);
            var cooldownEnds = newest + policy.Cooldown;
            if (cooldownEnds > now)
            {
                retryAfter = cooldownEnds - now;
            }
        }

        if (stamps.Count >= policy.Limit)
        {
            // The window frees a slot when its oldest event falls out, not when the newest does.
            var oldest = new DateTimeOffset(stamps[0], TimeSpan.Zero);
            var windowFrees = oldest + policy.Window;
            var wait = windowFrees > now ? windowFrees - now : TimeSpan.FromSeconds(1);
            if (wait > retryAfter)
            {
                retryAfter = wait;
            }
        }

        return retryAfter > TimeSpan.Zero
            ? FeedbackRateDecision.Deny(retryAfter)
            : FeedbackRateDecision.Allow();
    }

    /// <summary>
    /// An owner-less caller is refused, not exempted.
    /// </summary>
    /// <remarks>
    /// The same rule the repositories follow: an empty scope means "no data", never "all data". A
    /// limiter that treated a missing owner as unlimited would turn any path that lost the claim
    /// into an unmetered one.
    /// </remarks>
    private FeedbackRateDecision DenyUnowned(FeedbackRateKind kind)
    {
        _logger.LogWarning(
            "[Feedback] Rate limiter called with no owner for {Kind} — refusing.", kind);
        return FeedbackRateDecision.Deny(PolicyFor(kind).Window);
    }

    private RatePolicy PolicyFor(FeedbackRateKind kind) => kind switch
    {
        FeedbackRateKind.Preview => new RatePolicy(
            _options.MaxPreviewsPerWindow, _options.PreviewWindow, TimeSpan.Zero),
        FeedbackRateKind.Submit => new RatePolicy(
            _options.MaxSubmitsPerWindow, _options.SubmitWindow, _options.SubmitCooldown),

        // An unclassified kind is refused with the widest wait rather than admitted. Adding a
        // member without a policy must fail closed.
        _ => new RatePolicy(0, TimeSpan.FromHours(24), TimeSpan.Zero)
    };

    internal static List<long> Parse(string? csv)
    {
        var result = new List<long>();
        if (string.IsNullOrEmpty(csv))
        {
            return result;
        }

        foreach (var range in csv.AsSpan().Split(','))
        {
            var slice = csv.AsSpan()[range].Trim();
            if (slice.IsEmpty)
            {
                continue;
            }

            if (long.TryParse(slice, NumberStyles.None, CultureInfo.InvariantCulture, out var ticks)
                && ticks > 0
                && ticks <= DateTime.MaxValue.Ticks)
            {
                result.Add(ticks);
            }
        }

        result.Sort();
        return result;
    }

    internal static List<long> Prune(List<long> stamps, DateTimeOffset now, TimeSpan window)
    {
        var cutoff = now.UtcDateTime.Ticks - window.Ticks;
        var kept = new List<long>(stamps.Count);
        foreach (var ticks in stamps)
        {
            // Strictly newer than the cutoff, and never in the future: a clock skew that recorded
            // a stamp ahead of now would otherwise hold the window open indefinitely.
            if (ticks > cutoff && ticks <= now.UtcDateTime.Ticks)
            {
                kept.Add(ticks);
            }
        }

        return kept;
    }

    internal static string Serialize(List<long> stamps)
    {
        if (stamps.Count == 0)
        {
            return string.Empty;
        }

        var builder = new StringBuilder(stamps.Count * 19);
        for (var i = 0; i < stamps.Count; i++)
        {
            if (i > 0)
            {
                builder.Append(',');
            }

            builder.Append(stamps[i].ToString(CultureInfo.InvariantCulture));
        }

        return builder.ToString();
    }

    private readonly record struct RatePolicy(int Limit, TimeSpan Window, TimeSpan Cooldown)
    {
        /// <summary>What to tell a caller that lost every compare-and-swap attempt.</summary>
        public TimeSpan ContentionBackoff => TimeSpan.FromSeconds(1);
    }
}
