using Microsoft.Extensions.Hosting;
using SentenceStudio.Api.Coach.Runtime;
using SentenceStudio.Contracts.Coach;

namespace SentenceStudio.Api.Tests.Coach;

/// <summary>
/// Cohort and kill-switch rules for <see cref="CoachAvailabilityPolicy"/>.
/// Every path must fail closed and must report a typed reason rather than an identifier.
/// </summary>
public class CoachAvailabilityPolicyTests
{
    private const string InCohort = "profile-in-cohort";
    private const string NotInCohort = "profile-not-in-cohort";

    private static (CoachAvailabilityPolicy Policy, TestOptionsMonitor<CoachOptions> Monitor) CreatePolicy(
        bool enabled,
        params string[] cohort)
        => CreatePolicyIn(environmentName: null, enabled, cohort);

    /// <summary>
    /// Builds the policy for a named host environment. Separate name rather than an overload:
    /// <c>CreatePolicy(true, "profile-in-cohort")</c> would silently bind the cohort id to an
    /// environment parameter, which is exactly the kind of quiet mis-wiring these tests exist to
    /// catch.
    /// </summary>
    private static (CoachAvailabilityPolicy Policy, TestOptionsMonitor<CoachOptions> Monitor) CreatePolicyIn(
        string? environmentName,
        bool enabled,
        params string[] cohort)
    {
        var monitor = new TestOptionsMonitor<CoachOptions>(new CoachOptions
        {
            Enabled = enabled,
            AllowedUserProfileIds = cohort.ToList()
        });

        IHostEnvironment? environment = environmentName is null
            ? null
            : new PolicyStubEnvironment(environmentName);

        return (new CoachAvailabilityPolicy(monitor, environment), monitor);
    }

    private sealed class PolicyStubEnvironment(string environmentName) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = environmentName;
        public string ApplicationName { get; set; } = "SentenceStudio.Api.Tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } =
            new Microsoft.Extensions.FileProviders.NullFileProvider();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Evaluate_WithoutUserScope_DeniesBeforeReadingConfiguration(string? userProfileId)
    {
        var (policy, _) = CreatePolicy(enabled: true, InCohort);

        var decision = policy.Evaluate(userProfileId);

        decision.IsAllowed.Should().BeFalse();
        decision.Reason.Should().Be(CoachAvailabilityDenialReason.MissingUserScope);
        decision.State.Should().Be(CoachAvailabilityState.Disabled);
    }

    [Fact]
    public void Evaluate_WhenFeatureDisabled_DeniesEvenForACohortMember()
    {
        var (policy, _) = CreatePolicy(enabled: false, InCohort);

        var decision = policy.Evaluate(InCohort);

        decision.IsAllowed.Should().BeFalse();
        decision.Reason.Should().Be(CoachAvailabilityDenialReason.FeatureDisabled);
        decision.State.Should().Be(CoachAvailabilityState.Disabled);
    }

    [Fact]
    public void Evaluate_WhenEnabledWithEmptyCohort_DeniesEveryone()
    {
        var (policy, _) = CreatePolicy(enabled: true);

        var decision = policy.Evaluate(InCohort);

        decision.IsAllowed.Should().BeFalse("an empty cohort must fail closed rather than open the pilot to all learners");
        decision.Reason.Should().Be(CoachAvailabilityDenialReason.OutsideCohort);
        decision.State.Should().Be(CoachAvailabilityState.OutsideCohort);
    }

    [Fact]
    public void Evaluate_ForALearnerOutsideTheCohort_Denies()
    {
        var (policy, _) = CreatePolicy(enabled: true, InCohort);

        var decision = policy.Evaluate(NotInCohort);

        decision.IsAllowed.Should().BeFalse();
        decision.Reason.Should().Be(CoachAvailabilityDenialReason.OutsideCohort);
        decision.State.Should().Be(CoachAvailabilityState.OutsideCohort);
    }

    [Fact]
    public void Evaluate_ForACohortMember_Allows()
    {
        var (policy, _) = CreatePolicy(enabled: true, "other-profile", InCohort);

        var decision = policy.Evaluate(InCohort);

        decision.IsAllowed.Should().BeTrue();
        decision.Reason.Should().Be(CoachAvailabilityDenialReason.None);
        decision.State.Should().Be(CoachAvailabilityState.Available);
    }

    [Fact]
    public void Evaluate_NeverReportsLimitOrResumeStates()
    {
        var (policy, _) = CreatePolicy(enabled: true, InCohort);

        var decision = policy.Evaluate(InCohort);

        decision.State.Should().NotBe(CoachAvailabilityState.LimitReached);
        decision.State.Should().NotBe(CoachAvailabilityState.ResumeAvailable);
    }

    [Fact]
    public void Evaluate_ObservesAKillSwitchFlipWithoutARestart()
    {
        var (policy, monitor) = CreatePolicy(enabled: true, InCohort);
        policy.Evaluate(InCohort).IsAllowed.Should().BeTrue();

        monitor.Set(new CoachOptions { Enabled = false, AllowedUserProfileIds = new List<string> { InCohort } });

        var decision = policy.Evaluate(InCohort);
        decision.IsAllowed.Should().BeFalse();
        decision.Reason.Should().Be(CoachAvailabilityDenialReason.FeatureDisabled);
    }

    [Fact]
    public void DenialReason_DefaultsToDeny()
        => default(CoachAvailabilityDenialReason).Should().Be(CoachAvailabilityDenialReason.MissingUserScope);

    [Fact]
    public void Decision_DefaultIsNotAllowed()
        => default(CoachAvailabilityDecision).IsAllowed.Should().BeFalse();

    // ------------------------------------------------------------------
    // Development cohort sentinel (__dev_all__)
    //
    // The sentinel admits every authenticated user, so it is honoured only in Development. These
    // tests fix that boundary from both sides: Development admits, everything else does not, and
    // no environment at all is treated as "not Development".
    // ------------------------------------------------------------------

    [Fact]
    public void Evaluate_WithDevAllSentinel_InDevelopment_AdmitsAnyAuthenticatedUser()
    {
        var (policy, _) = CreatePolicyIn(Environments.Development, enabled: true, CoachOptions.DevAllSentinel);

        var decision = policy.Evaluate("any-random-profile-guid");

        decision.IsAllowed.Should().BeTrue();
        decision.Reason.Should().Be(CoachAvailabilityDenialReason.None);
        decision.State.Should().Be(CoachAvailabilityState.Available);
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    [InlineData("Testing")]
    [InlineData("development")] // Environment names are compared case-insensitively by IsDevelopment; this must still match.
    public void Evaluate_WithDevAllSentinel_OutsideDevelopment_DoesNotActivateTheSentinel(string environmentName)
    {
        var (policy, _) = CreatePolicyIn(environmentName, enabled: true, CoachOptions.DevAllSentinel);

        var decision = policy.Evaluate("any-random-profile-guid");

        if (string.Equals(environmentName, Environments.Development, StringComparison.OrdinalIgnoreCase))
        {
            decision.IsAllowed.Should().BeTrue("IsDevelopment is case-insensitive");
            return;
        }

        decision.IsAllowed.Should().BeFalse(
            "the sentinel admits everyone and is Development-only");
        decision.Reason.Should().Be(CoachAvailabilityDenialReason.OutsideCohort);
        decision.State.Should().Be(CoachAvailabilityState.OutsideCohort);
    }

    [Fact]
    public void Evaluate_WithDevAllSentinel_WithNoEnvironment_FailsClosed()
    {
        // A host that did not supply an environment is not Development. The strict answer is the
        // default answer, so forgetting to wire the environment cannot loosen the cohort.
        var (policy, _) = CreatePolicyIn(environmentName: null, enabled: true, CoachOptions.DevAllSentinel);

        var decision = policy.Evaluate("any-random-profile-guid");

        decision.IsAllowed.Should().BeFalse();
        decision.Reason.Should().Be(CoachAvailabilityDenialReason.OutsideCohort);
    }

    [Fact]
    public void Evaluate_WithDevAllSentinel_StillRejectsMissingUserScope()
    {
        var (policy, _) = CreatePolicyIn(Environments.Development, enabled: true, CoachOptions.DevAllSentinel);

        var decision = policy.Evaluate(null);

        decision.IsAllowed.Should().BeFalse("sentinel admits any *authenticated* user, not null/empty");
        decision.Reason.Should().Be(CoachAvailabilityDenialReason.MissingUserScope);
    }

    [Fact]
    public void Evaluate_WithDevAllSentinel_StillRespectsKillSwitch()
    {
        // Even in Development, and even with the wildcard sentinel present, Coach:Enabled=false
        // wins. Order of checks: scope, then kill switch, then cohort.
        var (policy, _) = CreatePolicyIn(Environments.Development, enabled: false, CoachOptions.DevAllSentinel);

        var decision = policy.Evaluate("any-profile");

        decision.IsAllowed.Should().BeFalse();
        decision.Reason.Should().Be(CoachAvailabilityDenialReason.FeatureDisabled);
    }

    [Fact]
    public void Evaluate_WithDevAllSentinel_KillSwitchFlipRevokesAdmissionInDevelopment()
    {
        var (policy, monitor) = CreatePolicyIn(Environments.Development, enabled: true, CoachOptions.DevAllSentinel);
        policy.Evaluate("any-profile").IsAllowed.Should().BeTrue();

        monitor.Set(new CoachOptions
        {
            Enabled = false,
            AllowedUserProfileIds = new List<string> { CoachOptions.DevAllSentinel }
        });

        policy.Evaluate("any-profile").Reason.Should().Be(CoachAvailabilityDenialReason.FeatureDisabled);
    }

    [Fact]
    public void Evaluate_ExplicitCohortIds_WorkIdenticallyInEveryEnvironment()
    {
        // The sentinel change must not touch the ordinary path: a named profile id is admitted
        // in Production exactly as it is in Development, and a stranger is denied in both.
        foreach (var environmentName in new[] { Environments.Development, Environments.Production })
        {
            var (policy, _) = CreatePolicyIn(environmentName, enabled: true, InCohort);

            policy.Evaluate(InCohort).IsAllowed.Should().BeTrue(
                "an explicitly named cohort id is admitted in {0}", environmentName);
            policy.Evaluate(NotInCohort).IsAllowed.Should().BeFalse(
                "a profile outside the cohort is denied in {0}", environmentName);
        }
    }

    [Fact]
    public void Evaluate_ExplicitCohortAlongsideSentinel_OutsideDevelopment_AdmitsOnlyTheNamedId()
    {
        // A cohort that carries both a real id and the sentinel must degrade to the real id
        // outside Development, not to "everyone" and not to "nobody".
        var (policy, _) = CreatePolicyIn(
            Environments.Production, enabled: true, CoachOptions.DevAllSentinel, InCohort);

        policy.Evaluate(InCohort).IsAllowed.Should().BeTrue();
        policy.Evaluate(NotInCohort).IsAllowed.Should().BeFalse();
    }

    [Fact]
    public void Evaluate_ProductionDefaults_DenyEvenWithDevAllSentinelAbsent()
    {
        // Production defaults: Enabled=false, empty cohort
        var (policy, _) = CreatePolicyIn(Environments.Production, enabled: false);

        var decision = policy.Evaluate("any-profile");

        decision.IsAllowed.Should().BeFalse("production defaults must deny everyone");
    }
}
