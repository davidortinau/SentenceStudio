using System.Data;
using Microsoft.EntityFrameworkCore;
using SentenceStudio.Abstractions;
using SentenceStudio.Data;
using SentenceStudio.Services.Plans;

namespace SentenceStudio.Native.Adapters;

/// <summary>
/// Serializes the owner/local-day absence check and PlanService's first write on
/// the same SQLite connection. An existing plan is returned without invoking
/// generation, so competing create-if-absent calls cannot regenerate its cards.
/// </summary>
public sealed class NativePlanCreationAdapter(
    NativePlanLeaseFactory leases,
    IUserScopeProvider userScope,
    IPreferencesService preferences) : INativePlanCreation
{
    public async Task<NativePlanCreateResult> CreateIfAbsentAsync(
        string userId, DateOnly localDate, CancellationToken cancellationToken)
    {
        RequireActiveOwner(userId);
        cancellationToken.ThrowIfCancellationRequested();

        using var lease = leases.ForOwner(userId);
        if (lease.LocalDate != localDate)
            throw new InvalidOperationException("Plan creation requires the current user-local date.");
        if (!lease.Database.Database.IsSqlite())
            throw new NotSupportedException("Native plan creation requires SQLite writer serialization.");

        // SQLite's non-deferred transaction acquires the writer reservation
        // before GetTodayAsync. Both competing callers must therefore re-read
        // under that reservation, not race a separate check against a later
        // GenerateTodayAsync call.
        await using var transaction = await lease.Database.Database
            .BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);

        RequireActiveOwner(userId);
        if (!await lease.Database.UserProfiles.AsNoTracking()
                .AnyAsync(profile => profile.Id == userId, cancellationToken))
            throw new UnauthorizedAccessException("Plan creation requires a local owner profile.");

        var existing = await lease.Service.GetTodayAsync(cancellationToken);
        RequireActiveOwner(userId);
        if (existing is not null)
        {
            await transaction.CommitAsync(cancellationToken);
            return new NativePlanCreateResult(existing, Created: false);
        }

        // Older clients can leave active completion rows without their
        // DailyPlan parent. Archive-only days are safe to generate again.
        var dateKey = localDate.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
        if (await lease.Database.DailyPlanCompletions.AsNoTracking()
                .AnyAsync(row => row.UserProfileId == userId && row.Date == dateKey
                    && !row.IsArchived,
                    cancellationToken))
            throw new NotSupportedException(
                "Plan creation refused existing activity rows without a plan.");

        var generated = await lease.GenerateNativeTodayAsync(cancellationToken);
        if (generated.GeneratedForDate != localDate)
            throw new InvalidOperationException("Generated plan has a different user-local date.");
        RequireActiveOwner(userId);

        // Created means the insert committed, not merely that the generator
        // returned a plan while a transaction was still in flight.
        await transaction.CommitAsync(cancellationToken);
        return new NativePlanCreateResult(generated, Created: true);
    }

    private void RequireActiveOwner(string userId)
    {
        if (string.IsNullOrWhiteSpace(userId)
            || !userScope.TryGetUserProfileId(out var activeOwner)
            || !string.Equals(activeOwner, userId, StringComparison.Ordinal)
            || !string.Equals(
                preferences.Get(UserProfileRepository.ActiveProfileIdKey, string.Empty),
                userId, StringComparison.Ordinal))
            throw new UnauthorizedAccessException("Plan creation requires the active owner.");
    }
}
