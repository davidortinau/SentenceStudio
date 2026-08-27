using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SentenceStudio.Data;

namespace SentenceStudio.Application.Practice;

/// <inheritdoc cref="IPracticeHistoryQueries"/>
/// <remarks>
/// Resolves its <see cref="ApplicationDbContext"/> the way every other owner-scoped repository in
/// this assembly does — from a scope it opens per call, joining an ambient unit of work when a
/// caller has already opened one. That keeps the class usable from a singleton on the device head
/// and from a request scope on the server without two registrations that behave differently.
/// </remarks>
public sealed class PracticeHistoryQueries : IPracticeHistoryQueries
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<PracticeHistoryQueries> _logger;

    public PracticeHistoryQueries(IServiceProvider serviceProvider, ILogger<PracticeHistoryQueries> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    public async Task<IReadOnlyList<PracticeCompletionFacts>> GetCompletionsInRangeAsync(
        string userProfileId,
        DateTime startUtcInclusive,
        DateTime endUtcExclusive,
        CancellationToken cancellationToken = default)
    {
        if (!HasOwner(userProfileId, nameof(GetCompletionsInRangeAsync)))
        {
            return [];
        }

        using var lease = DbLease.Open(_serviceProvider);
        return await lease.Db.DailyPlanCompletions
            .AsNoTracking()
            .Where(c => c.UserProfileId == userProfileId
                        && c.Date >= startUtcInclusive
                        && c.Date < endUtcExclusive)
            .Select(c => new PracticeCompletionFacts(c.ActivityType, c.MinutesSpent, c.IsCompleted, c.Date))
            .ToListAsync(cancellationToken);
    }

    public async Task<int> CountActivityAttemptsAsync(
        string userProfileId,
        DateTime startUtcInclusive,
        DateTime endUtcExclusive,
        CancellationToken cancellationToken = default)
    {
        if (!HasOwner(userProfileId, nameof(CountActivityAttemptsAsync)))
        {
            return 0;
        }

        using var lease = DbLease.Open(_serviceProvider);
        return await lease.Db.UserActivities
            .AsNoTracking()
            .CountAsync(
                a => a.UserProfileId == userProfileId
                     && a.CreatedAt >= startUtcInclusive
                     && a.CreatedAt < endUtcExclusive,
                cancellationToken);
    }

    public async Task<IReadOnlyDictionary<string, DateTime>> GetResourceLastUsedAsync(
        string userProfileId,
        CancellationToken cancellationToken = default)
    {
        if (!HasOwner(userProfileId, nameof(GetResourceLastUsedAsync)))
        {
            return new Dictionary<string, DateTime>();
        }

        using var lease = DbLease.Open(_serviceProvider);
        return await lease.Db.DailyPlanCompletions
            .AsNoTracking()
            .Where(c => c.UserProfileId == userProfileId
                        && c.ResourceId != null
                        && c.ResourceId != string.Empty)
            .GroupBy(c => c.ResourceId!)
            .Select(g => new { ResourceId = g.Key, LastDate = g.Max(c => c.Date) })
            .ToDictionaryAsync(x => x.ResourceId, x => x.LastDate, cancellationToken);
    }

    public async Task<DateTime?> GetResourceLastUsedAsync(
        string userProfileId,
        string resourceId,
        CancellationToken cancellationToken = default)
    {
        if (!HasOwner(userProfileId, nameof(GetResourceLastUsedAsync)) || string.IsNullOrWhiteSpace(resourceId))
        {
            return null;
        }

        using var lease = DbLease.Open(_serviceProvider);
        return await lease.Db.DailyPlanCompletions
            .AsNoTracking()
            .Where(c => c.UserProfileId == userProfileId && c.ResourceId == resourceId)
            .MaxAsync(c => (DateTime?)c.Date, cancellationToken);
    }

    public async Task<DailyPlanFacts?> GetPlanForDateAsync(
        string userProfileId,
        DateTime planDateUtc,
        CancellationToken cancellationToken = default)
    {
        if (!HasOwner(userProfileId, nameof(GetPlanForDateAsync)))
        {
            return null;
        }

        using var lease = DbLease.Open(_serviceProvider);
        return await lease.Db.DailyPlans
            .AsNoTracking()
            .Where(p => p.UserProfileId == userProfileId && p.Date == planDateUtc)
            .OrderByDescending(p => p.GeneratedAtUtc)
            .Select(p => new DailyPlanFacts(p.Strategy))
            .FirstOrDefaultAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<PlanItemFacts>> GetPlanItemsForDateAsync(
        string userProfileId,
        DateTime planDateUtc,
        CancellationToken cancellationToken = default)
    {
        if (!HasOwner(userProfileId, nameof(GetPlanItemsForDateAsync)))
        {
            return [];
        }

        using var lease = DbLease.Open(_serviceProvider);
        return await lease.Db.DailyPlanCompletions
            .AsNoTracking()
            .Where(c => c.UserProfileId == userProfileId && c.Date == planDateUtc)
            .Select(c => new PlanItemFacts(c.ActivityType, c.IsCompleted, c.EstimatedMinutes, c.MinutesSpent))
            .ToListAsync(cancellationToken);
    }

    public async Task<DateTime?> GetLastPracticeUtcAsync(
        string userProfileId,
        CancellationToken cancellationToken = default)
    {
        if (!HasOwner(userProfileId, nameof(GetLastPracticeUtcAsync)))
        {
            return null;
        }

        using var lease = DbLease.Open(_serviceProvider);

        // Max date across plan-item completions and free-form activity attempts —
        // the same source of truth PracticeBalanceTool aggregates over a window.
        var lastCompletion = await lease.Db.DailyPlanCompletions
            .AsNoTracking()
            .Where(c => c.UserProfileId == userProfileId)
            .MaxAsync(c => (DateTime?)c.Date, cancellationToken);

        var lastAttempt = await lease.Db.UserActivities
            .AsNoTracking()
            .Where(a => a.UserProfileId == userProfileId)
            .MaxAsync(a => (DateTime?)a.CreatedAt, cancellationToken);

        return (lastCompletion, lastAttempt) switch
        {
            (null, null) => null,
            (null, { } b) => b,
            ({ } a, null) => a,
            ({ } a, { } b) => a > b ? a : b
        };
    }

    private bool HasOwner(string userProfileId, string method)
    {
        if (!string.IsNullOrWhiteSpace(userProfileId))
        {
            return true;
        }

        _logger.LogWarning(
            "PracticeHistoryQueries.{Method} called without an owner — returning empty to prevent cross-tenant data leak.",
            method);
        return false;
    }

    /// <summary>
    /// Borrows the context for one query: the caller's ambient unit of work when there is one,
    /// otherwise a context from a scope this lease owns and disposes.
    /// </summary>
    private readonly struct DbLease : IDisposable
    {
        private readonly IServiceScope? _scope;

        private DbLease(IServiceScope? scope, ApplicationDbContext db)
        {
            _scope = scope;
            Db = db;
        }

        public ApplicationDbContext Db { get; }

        public static DbLease Open(IServiceProvider serviceProvider)
        {
            if (AmbientApplicationDbContext.Current is { } ambient)
            {
                return new DbLease(null, ambient);
            }

            var scope = serviceProvider.CreateScope();
            return new DbLease(scope, scope.ServiceProvider.GetRequiredService<ApplicationDbContext>());
        }

        public void Dispose() => _scope?.Dispose();
    }
}
