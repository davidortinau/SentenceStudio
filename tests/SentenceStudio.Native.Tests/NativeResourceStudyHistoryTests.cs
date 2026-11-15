using Microsoft.Extensions.DependencyInjection;
using SentenceStudio.Data;
using SentenceStudio.Services.Progress;
using SentenceStudio.Shared.Models;

namespace SentenceStudio.Native.Tests;

/// <summary>
/// The native heads' resource study history (issue #219) on the real native SQLite slice: the
/// trusted device owner's facts only, nothing once that owner changes, nothing without one.
/// </summary>
public sealed class NativeResourceStudyHistoryTests
{
    private static readonly DateTime Day = new(2026, 11, 10, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task ReadsTheSignedInOwnersHistory()
    {
        using var fixture = await SeededAsync();
        fixture.Activate(NativeSliceFixture.UserA);
        var history = fixture.Provider.GetRequiredService<NativeResourceStudyHistory>();

        var state = await history.LoadAsync(fixture.ResourceId(NativeSliceFixture.UserA));

        Assert.Equal(NativeResourceStudyPhase.Ready, state.Phase);
        var summary = Assert.IsType<ResourceStudySummary>(state.Summary);
        Assert.Equal(1, summary.SessionCount);
        Assert.Equal(12, summary.TotalMinutes);
        Assert.Equal(PlanActivityType.Reading, summary.LastActivity);
        Assert.Equal(Day.Date, summary.LastStudiedDate);
        Assert.Equal(4, summary.WordCount);
        Assert.Equal(4, summary.KnownCount);
        Assert.Same(state, history.State);
    }

    [Fact]
    public async Task ShowsNothingOnceTheOwnerChangesAndRefusesAnotherOwnersResource()
    {
        using var fixture = await SeededAsync();
        fixture.Activate(NativeSliceFixture.UserA);
        var history = fixture.Provider.GetRequiredService<NativeResourceStudyHistory>();
        await history.LoadAsync(fixture.ResourceId(NativeSliceFixture.UserA));

        fixture.Activate(NativeSliceFixture.UserB);

        Assert.Equal(NativeResourceStudyPhase.Idle, history.State.Phase);
        Assert.Null(history.State.Summary);
        var state = await history.LoadAsync(fixture.ResourceId(NativeSliceFixture.UserA));
        Assert.Equal(NativeResourceStudyPhase.Unavailable, state.Phase);
        Assert.Null(state.Summary);
        var own = await history.LoadAsync(fixture.ResourceId(NativeSliceFixture.UserB));
        Assert.Equal(NativeResourceStudyPhase.Ready, own.Phase);
        Assert.Equal(0, own.Summary!.SessionCount);
        Assert.Equal(0, own.Summary.KnownCount);
        Assert.Equal(4, own.Summary.UnknownCount);
    }

    [Fact]
    public async Task ReadsNothingWithoutAnOwner()
    {
        using var fixture = await SeededAsync();
        fixture.LoseOwner();
        var history = fixture.Provider.GetRequiredService<NativeResourceStudyHistory>();

        var state = await history.LoadAsync(fixture.ResourceId(NativeSliceFixture.UserA));

        Assert.Equal(NativeResourceStudyPhase.Idle, state.Phase);
        Assert.Null(state.Summary);
    }

    private static async Task<NativeSliceFixture> SeededAsync()
    {
        var fixture = new NativeSliceFixture();
        await fixture.SeedAsync(NativeSliceFixture.UserA, known: true);
        await fixture.SeedAsync(NativeSliceFixture.UserB);
        using var scope = fixture.Scope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        db.DailyPlanCompletions.AddRange(
            Completion(NativeSliceFixture.UserA, fixture.ResourceId(NativeSliceFixture.UserA), 12),
            // The other learner's session carrying the first learner's resource id.
            Completion(NativeSliceFixture.UserB, fixture.ResourceId(NativeSliceFixture.UserA), 30));
        await db.SaveChangesAsync();
        return fixture;
    }

    private static DailyPlanCompletion Completion(string owner, string resourceId, int minutes) =>
        new()
        {
            Id = Guid.NewGuid().ToString(), UserProfileId = owner, ResourceId = resourceId,
            PlanItemId = Guid.NewGuid().ToString(), ActivityType = nameof(PlanActivityType.Reading),
            Date = Day, MinutesSpent = minutes, EstimatedMinutes = 10, IsCompleted = true,
            CompletedAt = Day.AddHours(8), CreatedAt = Day, UpdatedAt = Day.AddHours(8),
        };
}
