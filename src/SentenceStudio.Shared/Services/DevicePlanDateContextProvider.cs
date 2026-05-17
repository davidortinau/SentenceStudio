using SentenceStudio.Services.Plans;

namespace SentenceStudio.Services;

/// <summary>
/// MAUI client implementation of <see cref="IPlanDateContext"/>. Builds a
/// fresh context on each <see cref="Current"/> read so DST transitions and
/// timezone changes (traveling user) take effect immediately without
/// re-registering the singleton.
/// </summary>
public sealed class DevicePlanDateContextProvider
{
    /// <summary>
    /// Returns a snapshot context anchored at the current
    /// <see cref="TimeZoneInfo.Local"/> and <see cref="DateTime.UtcNow"/>.
    /// </summary>
    public IPlanDateContext Current() => new PlanDateContext(TimeZoneInfo.Local);
}
