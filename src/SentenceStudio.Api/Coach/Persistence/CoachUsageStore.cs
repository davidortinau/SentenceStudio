using Microsoft.EntityFrameworkCore;

namespace SentenceStudio.Api.Coach.Persistence;

/// <summary>EF Core implementation of <see cref="ICoachUsageStore"/>.</summary>
public sealed class CoachUsageStore : ICoachUsageStore
{
    private readonly CoachDbContext _db;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<CoachUsageStore> _logger;

    public CoachUsageStore(CoachDbContext db, TimeProvider timeProvider, ILogger<CoachUsageStore> logger)
    {
        _db = db;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    private IQueryable<CoachUsage> Owned(string userProfileId) =>
        _db.CoachUsages.Where(u => u.UserProfileId == userProfileId);

    private bool HasUser(string userProfileId, string operation)
    {
        if (!string.IsNullOrWhiteSpace(userProfileId))
        {
            return true;
        }

        _logger.LogWarning("[Coach] {Operation} called with no active user id — returning no data.", operation);
        return false;
    }

    public async Task<CoachUsage?> RecordRunAsync(
        string userProfileId,
        DateOnly localDate,
        long inputTokens,
        long outputTokens,
        decimal estimatedCostUsd,
        CancellationToken cancellationToken = default)
    {
        if (!HasUser(userProfileId, nameof(RecordRunAsync)))
        {
            return null;
        }

        var now = _timeProvider.GetUtcNow().UtcDateTime;
        var row = await Owned(userProfileId)
            .FirstOrDefaultAsync(u => u.LocalDate == localDate, cancellationToken);

        if (row is null)
        {
            row = new CoachUsage
            {
                Id = Guid.NewGuid().ToString(),
                UserProfileId = userProfileId,
                LocalDate = localDate,
                WeekKey = CoachNormalizedJson.WeekKey(localDate),
                CreatedAt = now
            };
            _db.CoachUsages.Add(row);
        }

        row.RunCount += 1;
        row.InputTokens += inputTokens;
        row.OutputTokens += outputTokens;
        row.EstimatedCostUsd += estimatedCostUsd;
        row.UpdatedAt = now;

        await _db.SaveChangesAsync(cancellationToken);
        return row;
    }

    public async Task<CoachUsageTotals> GetDailyTotalsAsync(string userProfileId, DateOnly localDate, CancellationToken cancellationToken = default)
    {
        if (!HasUser(userProfileId, nameof(GetDailyTotalsAsync)))
        {
            return CoachUsageTotals.Empty;
        }

        var rows = await Owned(userProfileId)
            .Where(u => u.LocalDate == localDate)
            .ToListAsync(cancellationToken);

        return Aggregate(rows);
    }

    public async Task<CoachUsageTotals> GetWeeklyTotalsAsync(string userProfileId, DateOnly localDate, CancellationToken cancellationToken = default)
    {
        if (!HasUser(userProfileId, nameof(GetWeeklyTotalsAsync)))
        {
            return CoachUsageTotals.Empty;
        }

        var weekKey = CoachNormalizedJson.WeekKey(localDate);
        var rows = await Owned(userProfileId)
            .Where(u => u.WeekKey == weekKey)
            .ToListAsync(cancellationToken);

        return Aggregate(rows);
    }

    private static CoachUsageTotals Aggregate(IReadOnlyCollection<CoachUsage> rows)
    {
        if (rows.Count == 0)
        {
            return CoachUsageTotals.Empty;
        }

        return new CoachUsageTotals(
            rows.Sum(r => r.RunCount),
            rows.Sum(r => r.InputTokens),
            rows.Sum(r => r.OutputTokens),
            rows.Sum(r => r.EstimatedCostUsd));
    }
}
