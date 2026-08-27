using SentenceStudio.Contracts.Coach;

namespace SentenceStudio.Api.Coach.Runtime;

/// <summary>
/// Why the coach is not reachable for a caller. The zero value denies.
/// </summary>
public enum CoachAvailabilityDenialReason
{
    /// <summary>The caller has no resolvable user profile. Fail closed.</summary>
    MissingUserScope = 0,

    /// <summary>The <c>Coach:Enabled</c> flag is false.</summary>
    FeatureDisabled,

    /// <summary>The learner is not named in <c>Coach:AllowedUserProfileIds</c>.</summary>
    OutsideCohort,

    /// <summary>The coach is reachable. No denial applies.</summary>
    None
}

/// <summary>
/// The typed result of a coach availability check.
/// </summary>
/// <param name="IsAllowed">True when the caller may reach coach routes.</param>
/// <param name="Reason">Why the caller was denied, or <see cref="CoachAvailabilityDenialReason.None"/>.</param>
/// <param name="State">
/// The client-facing state to report. This decision layer never reports
/// <see cref="CoachAvailabilityState.LimitReached"/> or <see cref="CoachAvailabilityState.ResumeAvailable"/>;
/// budget and session lookups refine an <see cref="CoachAvailabilityState.Available"/> result later.
/// </param>
public readonly record struct CoachAvailabilityDecision(
    bool IsAllowed,
    CoachAvailabilityDenialReason Reason,
    CoachAvailabilityState State);

/// <summary>
/// Decides whether a learner may reach the coach at all, based only on the feature flag and the
/// pilot cohort. Budget, session, and expiry checks belong to <see cref="ICoachBudgetService"/>
/// and the session store, not here.
/// </summary>
/// <remarks>
/// Implementations must not log, trace, or otherwise emit the user profile id. A denial is
/// reported as a typed reason so callers can map it to a 404 or a notice without carrying an
/// identifier into telemetry.
/// </remarks>
public interface ICoachAvailabilityPolicy
{
    /// <summary>
    /// Evaluates reachability for a resolved user profile id.
    /// </summary>
    /// <param name="userProfileId">
    /// The authenticated <c>user_profile_id</c>. Null, empty, or whitespace denies with
    /// <see cref="CoachAvailabilityDenialReason.MissingUserScope"/>.
    /// </param>
    CoachAvailabilityDecision Evaluate(string? userProfileId);
}
