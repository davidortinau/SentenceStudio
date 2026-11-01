using SentenceStudio.Services.Plans;
using SentenceStudio.Shared.Models;

namespace SentenceStudio.Native.Tests;

/// <summary>
/// UserActivity rows from native activities carry UTC instants and an explicit
/// owner, so ProgressService.ComputeStreak puts them on the learner's local day.
/// </summary>
public sealed class NativeUserActivityTests
{
    [Fact]
    public void Utc9EveningSession_LandsOnTheLearnersLocalDay()
    {
        var seoul = TimeZoneInfo.FindSystemTimeZoneById("Asia/Seoul");
        // 21:30 on 1 November in Seoul is 12:30 UTC the same day.
        var instant = new DateTimeOffset(2026, 11, 1, 12, 30, 0, TimeSpan.Zero);
        var context = new PlanDateContext(seoul, () => instant.UtcDateTime);

        var row = NativeUserActivity.Create("owner-a", Activity.Translation, "가요", 85, 70,
            new FixedClock(instant));

        Assert.Equal(DateTimeKind.Utc, row.CreatedAt.Kind);
        Assert.Equal(instant.UtcDateTime, row.CreatedAt);
        Assert.Equal(row.CreatedAt, row.UpdatedAt);
        Assert.Equal(new DateOnly(2026, 11, 1), context.ToUserLocal(row.CreatedAt));
        Assert.Equal(context.UserLocalDate, context.ToUserLocal(row.CreatedAt));
        // The defect: the device-local wall clock read as UTC moves the session to 2 November.
        var localWallClock = TimeZoneInfo.ConvertTimeFromUtc(instant.UtcDateTime, seoul);
        Assert.Equal(new DateOnly(2026, 11, 2), context.ToUserLocal(localWallClock));

        Assert.Equal("owner-a", row.UserProfileId);
        Assert.Equal(nameof(Activity.Translation), row.Activity);
        Assert.Equal("가요", row.Input);
        Assert.Equal(85, row.Accuracy);
        Assert.Equal(70, row.Fluency);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void AbsentOwner_FailsClosed(string? owner) =>
        Assert.ThrowsAny<ArgumentException>(() =>
            NativeUserActivity.Create(owner!, Activity.Clozure, "x", 100));

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
