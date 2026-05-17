namespace SentenceStudio.Services.Plans;

/// <summary>
/// Default <see cref="IPlanDateContext"/> backed by a supplied
/// <see cref="TimeZoneInfo"/>. The API constructs one per request from the
/// <c>X-Timezone</c> header; the MAUI client constructs one (re-read on
/// demand) over <c>TimeZoneInfo.Local</c>.
/// </summary>
public sealed class PlanDateContext : IPlanDateContext
{
    private readonly DateOnly _userLocalDate;
    private readonly DateTime _utcNowSnapshot;

    public PlanDateContext(TimeZoneInfo timeZone, Func<DateTime>? utcNowProvider = null)
    {
        TimeZone = timeZone ?? throw new ArgumentNullException(nameof(timeZone));
        var nowProvider = utcNowProvider ?? (() => DateTime.UtcNow);

        _utcNowSnapshot = DateTime.SpecifyKind(nowProvider(), DateTimeKind.Utc);
        var local = TimeZoneInfo.ConvertTimeFromUtc(_utcNowSnapshot, TimeZone);
        _userLocalDate = DateOnly.FromDateTime(local);
    }

    public TimeZoneInfo TimeZone { get; }
    public DateTime UtcNow => _utcNowSnapshot;
    public DateOnly UserLocalDate => _userLocalDate;

    public DateOnly ToUserLocal(DateTime utc)
    {
        var utcKind = DateTime.SpecifyKind(utc, DateTimeKind.Utc);
        var local = TimeZoneInfo.ConvertTimeFromUtc(utcKind, TimeZone);
        return DateOnly.FromDateTime(local);
    }

    public DateTime ToUtcMidnight(DateOnly userLocal)
    {
        var localMidnight = userLocal.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
        return TimeZoneInfo.ConvertTimeToUtc(localMidnight, TimeZone);
    }
}
