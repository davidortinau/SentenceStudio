using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using SentenceStudio.Contracts.Coach;

namespace SentenceStudio.Api.Coach.Runtime;

/// <summary>
/// Configuration-driven <see cref="ICoachAvailabilityPolicy"/>.
/// </summary>
/// <remarks>
/// <para>
/// Order of checks is deliberate and fails closed at each step: no user scope, then feature flag,
/// then cohort. The feature flag is read through <see cref="IOptionsMonitor{TOptions}"/> so an
/// operator can flip the kill switch through configuration reload without a restart.
/// </para>
/// <para>
/// The development cohort sentinel (<see cref="CoachOptions.DevAllSentinel"/>) is honoured here
/// and only here, and only when the host is running in Development. Startup validation already
/// refuses to boot a non-Development host whose cohort names the sentinel; this is the second
/// gate, because configuration can be reloaded after startup and a reload does not re-run
/// <c>ValidateOnStart</c>. An absent <see cref="IHostEnvironment"/> is treated as
/// non-Development, so the strict answer is also the default answer.
/// </para>
/// <para>
/// This type takes no logger on purpose. The only inputs it could log are the learner's profile id
/// and the cohort list, and neither may leave the process. Callers log the typed
/// <see cref="CoachAvailabilityDenialReason"/> instead.
/// </para>
/// </remarks>
public sealed class CoachAvailabilityPolicy : ICoachAvailabilityPolicy
{
    private readonly IOptionsMonitor<CoachOptions> _options;
    private readonly bool _isDevelopment;

    /// <summary>Creates the policy over a live options monitor.</summary>
    /// <param name="options">The live coach options.</param>
    /// <param name="environment">
    /// The host environment. Null is treated as non-Development, which is the fail-closed answer:
    /// the development cohort sentinel is then ignored.
    /// </param>
    public CoachAvailabilityPolicy(IOptionsMonitor<CoachOptions> options, IHostEnvironment? environment = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _isDevelopment = environment?.IsDevelopment() ?? false;
    }

    /// <inheritdoc />
    public CoachAvailabilityDecision Evaluate(string? userProfileId)
    {
        if (string.IsNullOrWhiteSpace(userProfileId))
        {
            return new CoachAvailabilityDecision(
                IsAllowed: false,
                CoachAvailabilityDenialReason.MissingUserScope,
                CoachAvailabilityState.Disabled);
        }

        var options = _options.CurrentValue;

        if (!options.Enabled)
        {
            return new CoachAvailabilityDecision(
                IsAllowed: false,
                CoachAvailabilityDenialReason.FeatureDisabled,
                CoachAvailabilityState.Disabled);
        }

        if (!options.IsInCohort(userProfileId, allowDevelopmentSentinel: _isDevelopment))
        {
            return new CoachAvailabilityDecision(
                IsAllowed: false,
                CoachAvailabilityDenialReason.OutsideCohort,
                CoachAvailabilityState.OutsideCohort);
        }

        return new CoachAvailabilityDecision(
            IsAllowed: true,
            CoachAvailabilityDenialReason.None,
            CoachAvailabilityState.Available);
    }
}
