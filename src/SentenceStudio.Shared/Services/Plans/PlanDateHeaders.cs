namespace SentenceStudio.Services.Plans;

/// <summary>
/// Canonical HTTP header names for the plan-date contract.
/// </summary>
/// <remarks>
/// The name lives in Shared because both sides of the wire need it: the API resolves the
/// per-request <see cref="IPlanDateContext"/> from it, and the clients in AppLib must send it.
/// AppLib cannot reference the API assembly, so a shared constant is the only way both can agree
/// without one of them hardcoding a literal that the other could silently rename.
/// </remarks>
public static class PlanDateHeaders
{
    /// <summary>
    /// IANA timezone id of the learner making the request, for example <c>America/Chicago</c>.
    /// Windows ids are also accepted by the resolver.
    /// </summary>
    /// <remarks>
    /// Without this header the API cannot know the learner's local calendar date and falls back
    /// to UTC. That fallback is wrong for every learner west of Greenwich after their local
    /// evening: at 21:52 America/Chicago on Aug 14 the UTC date is already Aug 15, so a plan
    /// keyed to Aug 14 looks absent and the coach reports itself unavailable.
    /// </remarks>
    public const string TimeZone = "X-Timezone";
}
