namespace SentenceStudio.Services.Plans;

/// <summary>
/// Per-request authoritative source of the user's local "today" and current
/// instant. The single rule for daily-plan turnover: <see cref="UserLocalDate"/>
/// is the ONLY value any plan-generation code is allowed to read for "today."
/// Plan code must not call <c>DateTime.UtcNow.Date</c>, <c>DateTime.Today</c>,
/// <c>DateTime.Now.Date</c>, or <c>DateTimeOffset.Now.LocalDateTime.Date</c> —
/// see <c>BannedSymbols.txt</c> for the build-time guard.
/// </summary>
public interface IPlanDateContext
{
    /// <summary>The user's IANA timezone for this request / session.</summary>
    TimeZoneInfo TimeZone { get; }

    /// <summary>Current UTC instant. Use this instead of <see cref="DateTime.UtcNow"/>.</summary>
    DateTime UtcNow { get; }

    /// <summary>
    /// Today in the user's local timezone, computed once per construction as
    /// <c>TimeZoneInfo.ConvertTimeFromUtc(UtcNow, TimeZone).Date</c>.
    /// </summary>
    DateOnly UserLocalDate { get; }

    /// <summary>Convert a UTC instant to the user's local calendar date.</summary>
    DateOnly ToUserLocal(DateTime utc);

    /// <summary>
    /// Convert a user-local calendar date to the UTC midnight that opens that
    /// local date (i.e. the user's start-of-day in UTC).
    /// </summary>
    DateTime ToUtcMidnight(DateOnly userLocal);
}
