using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using SentenceStudio.Api.Tests.AppOperations;
using SentenceStudio.Contracts.Plans;
using SentenceStudio.Services.PlanGeneration;
using SentenceStudio.Services.Plans;

namespace SentenceStudio.Api.Tests.Data;

/// <summary>
/// <c>PlanService.RegenerateTodayAsync</c> on PostgreSQL, the path Sam's new plan takes on the
/// server (generator writes allowed, inside the retrying execution strategy). The SQLite tests in
/// <c>PlanServiceTests</c> run the device path, where the generator is read-only.
/// </summary>
public sealed class PlanRegenerationPostgresTests
{
    private const string Learner = "regen-learner";
    private const string Other = "regen-other";
    private static readonly DateOnly Today = new(2026, 11, 16);

    [ApplicationOperationPostgresFact]
    public async Task ItReplacesThePlanAtZero_AsOneUnit_AndLeavesAnotherLearnersPlan()
    {
        await using var database = await ApplicationOperationPostgresHarness.CreateAsync("plan_regen");
        var generator = new Generator();

        await Service(database, Other, generator).GenerateTodayAsync(new GenerateTodaysPlanRequest());
        // Read back, not taken from the generate call: this database stores times without a zone.
        var otherFirst = (await Service(database, Other, generator).GetTodayAsync())!;
        var first = await Service(database, Learner, generator).GenerateTodayAsync(new GenerateTodaysPlanRequest());
        await Service(database, Learner, generator).MarkCompleteAsync(Today, first.Items[0].Id, 7);
        var planIdBefore = (await Service(database, Learner, generator).GetTodaySnapshotAsync()).PlanId;

        var regenerated = await Service(database, Learner, generator).RegenerateTodayAsync();

        regenerated.Should().NotBeNull();
        regenerated!.CompletedCount.Should().Be(0);
        regenerated.Items.Should().OnlyContain(item => item.MinutesSpent == 0 && !item.IsCompleted);
        (await Service(database, Learner, generator).GetTodaySnapshotAsync()).PlanId.Should().NotBe(planIdBefore);

        await using var db = database.NewContext();
        var archived = await db.DailyPlanCompletions.AsNoTracking()
            .Where(c => c.UserProfileId == Learner && c.IsArchived).ToListAsync();
        archived.Should().Contain(c => c.IsCompleted && c.MinutesSpent == 7, "logged practice stays in history");
        (await db.DailyPlans.AsNoTracking().CountAsync(p => p.UserProfileId == Learner)).Should().Be(1);

        var other = await Service(database, Other, generator).GetTodayAsync();
        other!.GeneratedAtUtc.Should().Be(otherFirst.GeneratedAtUtc);
    }

    [ApplicationOperationPostgresFact]
    public async Task WhenGenerationBuildsNothingAfterTheReset_ThePlanIsPutBack()
    {
        await using var database = await ApplicationOperationPostgresHarness.CreateAsync("plan_regen_back");
        var generator = new Generator();
        var first = await Service(database, Learner, generator).GenerateTodayAsync(new GenerateTodaysPlanRequest());
        await Service(database, Learner, generator).MarkCompleteAsync(Today, first.Items[0].Id, 9);
        var stored = (await Service(database, Learner, generator).GetTodayAsync())!;

        generator.EmptyAfter = generator.Calls + 1; // the preview succeeds, the generation is empty
        var regenerated = await Service(database, Learner, generator).RegenerateTodayAsync();

        regenerated.Should().BeNull();
        var still = await Service(database, Learner, generator).GetTodayAsync();
        still.Should().NotBeNull();
        still!.GeneratedAtUtc.Should().Be(stored.GeneratedAtUtc);
        still.Items.Should().Contain(item => item.IsCompleted && item.MinutesSpent == 9);
    }

    private static PlanService Service(ApplicationOperationPostgresHarness database, string learner, Generator generator) =>
        new(
            database.NewRetryingContext(),
            new Scope(learner),
            new FixedDate(),
            generator,
            new EnglishPlanCopyProvider(),
            NullLogger<PlanService>.Instance);

    private sealed class Scope(string learner) : IUserScopeProvider
    {
        public string UserProfileId => learner;

        public bool TryGetUserProfileId(out string userProfileId)
        {
            userProfileId = learner;
            return true;
        }
    }

    private sealed class FixedDate : IPlanDateContext
    {
        private int _ticks;

        public TimeZoneInfo TimeZone => TimeZoneInfo.Utc;

        // Each read moves on a second, so a regeneration has a later time than the plan before it.
        public DateTime UtcNow => new DateTime(2026, 11, 16, 12, 0, 0, DateTimeKind.Utc).AddSeconds(++_ticks);

        public DateOnly UserLocalDate => Today;

        public DateOnly ToUserLocal(DateTime utc) => DateOnly.FromDateTime(utc);

        public DateTime ToUtcMidnight(DateOnly userLocal) => userLocal.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
    }

    private sealed class Generator : IDeterministicPlanGenerator
    {
        public int Calls { get; private set; }

        /// <summary>From this call on, the generator builds nothing.</summary>
        public int? EmptyAfter { get; set; }

        public Task<PlanSkeleton?> GenerateAsync(string? userProfileId = null, CancellationToken ct = default)
        {
            Calls++;
            var empty = EmptyAfter is { } after && Calls > after;
            return Task.FromResult<PlanSkeleton?>(new PlanSkeleton
            {
                Activities = empty
                    ? []
                    : [
                        new PlannedActivity { ActivityType = "Reading", ResourceId = "resource-1", EstimatedMinutes = 10, Priority = 1, Rationale = "test" },
                        new PlannedActivity { ActivityType = "VocabularyReview", EstimatedMinutes = 10, Priority = 2, Rationale = "test" }
                    ],
                TotalMinutes = empty ? 0 : 20,
                ResourceSelectionReason = "test"
            });
        }
    }
}
