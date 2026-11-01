using SentenceStudio.Services.Plans;

namespace SentenceStudio.UnitTests.PlanGeneration;

public sealed class PlanDateContextDaylightSavingTests
{
    [Fact]
    public void DayWhoseMidnightIsSkippedStartsAtTheFirstValidLocalMinute()
    {
        // America/Santiago springs forward at 00:00 on 2026-09-06 (UTC-4 -> UTC-3),
        // so local midnight does not exist; the day opens at 01:00 local = 04:00 UTC.
        var santiago = TimeZoneInfo.FindSystemTimeZoneById("America/Santiago");
        var context = new PlanDateContext(santiago,
            () => new DateTime(2026, 9, 6, 15, 0, 0, DateTimeKind.Utc));

        var start = context.ToUtcMidnight(new DateOnly(2026, 9, 6));

        Assert.Equal(new DateTime(2026, 9, 6, 4, 0, 0, DateTimeKind.Utc), start);
    }

    [Fact]
    public void OrdinaryDayStillStartsAtLocalMidnight()
    {
        var seoul = TimeZoneInfo.FindSystemTimeZoneById("Asia/Seoul");
        var context = new PlanDateContext(seoul,
            () => new DateTime(2026, 10, 31, 3, 0, 0, DateTimeKind.Utc));

        Assert.Equal(new DateTime(2026, 10, 30, 15, 0, 0, DateTimeKind.Utc),
            context.ToUtcMidnight(new DateOnly(2026, 10, 31)));
    }
}
