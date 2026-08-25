using FluentAssertions;
using Microsoft.Extensions.Logging;
using SentenceStudio.Services.Plans;
using SentenceStudio.UnitTests.Logging;
using Xunit;

namespace SentenceStudio.UnitTests.PlanGeneration;

/// <summary>
/// Privacy guard for the deterministic plan path.
/// </summary>
/// <remarks>
/// <para>
/// Earned from an Aspire E2E finding: <c>DeterministicPlanBuilder</c> logged the
/// raw <c>userProfileId</c> at Information level in "Starting deterministic plan
/// generation", and the Learning Coach preview/apply flow runs through that same
/// builder. The pilot learner's profile id therefore showed up in the Aspire
/// dashboard even though coach telemetry must never record a user or tenant id.
/// </para>
/// <para>
/// The assertions check the rendered message AND the structured key/value state,
/// because a structured sink (Aspire, OpenTelemetry) exports the state fields
/// even when the message template hides them.
/// </para>
/// </remarks>
public class DeterministicPlanBuilderLoggingPrivacyTests : IClassFixture<PlanGenerationTestFixture>, IDisposable
{
    /// <summary>
    /// Deliberately unmistakable, so a substring match cannot pass by accident
    /// and cannot collide with a GUID the builder legitimately logs.
    /// </summary>
    private const string PilotProfileId = "pilot-profile-id-zzz-9f3c1b";

    /// <summary>Learner-owned resource identity. Neither may reach telemetry.</summary>
    private const string PilotResourceId = "pilot-resource-id-zzz-4a7e2d";

    private const string PilotResourceTitle = "Pilot Learner Private Podcast Title zzz";

    private readonly PlanGenerationTestFixture _fixture;

    public DeterministicPlanBuilderLoggingPrivacyTests(PlanGenerationTestFixture fixture)
    {
        _fixture = fixture;
        _fixture.ClearAllData();
        _fixture.Logs.Clear();
    }

    public void Dispose() { }

    private void SeedPilotLearner(int sessionMinutes = 40)
    {
        _fixture.SeedUserProfile(sessionMinutes, userId: PilotProfileId);
        _fixture.SeedResource(
            id: PilotResourceId,
            title: PilotResourceTitle,
            mediaType: "Podcast",
            transcript: "Transcript text",
            vocabWordCount: 6,
            userProfileId: PilotProfileId);
        _fixture.SeedSkill();
    }

    /// <summary>Every learner-owned secret that must never reach a log.</summary>
    private static readonly string[] Secrets = [PilotProfileId, PilotResourceId, PilotResourceTitle];

    /// <summary>
    /// Strict guard: NOTHING logged during the run may carry the profile id.
    /// The coach preview path must satisfy this, because coach telemetry may
    /// never record a user or tenant id from any component.
    /// </summary>
    private void AssertNoProfileIdAnywhere()
    {
        Offenders(_ => true).Should().BeEmpty(
            "no log message or structured field on the coach preview path may carry a raw profile id, resource id, or resource title");
    }

    /// <summary>
    /// Resource identity is never logged by ANY component on this path, so the
    /// resource guard stays strict even on the write-enabled path where the
    /// Data-layer profile-id leak below still exists.
    /// </summary>
    private void AssertNoResourceIdentityAnywhere()
    {
        var offenders = _fixture.Logs.Entries
            .Where(e => e.AllText().Any(t =>
                t.Contains(PilotResourceId, StringComparison.OrdinalIgnoreCase) ||
                t.Contains(PilotResourceTitle, StringComparison.OrdinalIgnoreCase)))
            .Select(e => $"{e.Level} [{e.Category}] {e.Message}")
            .ToList();

        offenders.Should().BeEmpty("a resource id or title is learner-owned content and may never be logged");
    }

    /// <summary>
    /// Guard scoped to the plan services this lane owns
    /// (<c>SentenceStudio.Services.*</c>).
    /// </summary>
    /// <remarks>
    /// The legacy write-enabled path also reaches
    /// <c>UserProfileRepository.EnsureSmartResourcesAsync</c>, which logs
    /// "…for profile {ProfileId}" at Debug/Warning. That is a separate
    /// (Data-layer) lane and is NOT reachable from the coach, which always
    /// builds with <c>AllowWrites=false</c> — the strict guard above proves it.
    /// </remarks>
    private void AssertNoProfileIdInPlanServices()
    {
        Offenders(e => e.Category.StartsWith("SentenceStudio.Services.", StringComparison.Ordinal))
            .Should().BeEmpty("no plan service may log a raw profile id");
    }

    private List<string> Offenders(Func<CapturedLogEntry, bool> scope) =>
        _fixture.Logs.Entries
            .Where(scope)
            .Where(e => e.AllText().Any(t =>
                Secrets.Any(secret => t.Contains(secret, StringComparison.OrdinalIgnoreCase))))
            .Select(e => $"{e.Level} [{e.Category}] {e.Message}")
            .ToList();

    [Fact]
    public async Task CoachPreview_DoesNotLogTheRawProfileId()
    {
        SeedPilotLearner();
        _fixture.Logs.Clear();

        var plan = await _fixture.CreateBuilder().BuildPlanAsync(
            PlanBuildRequest.Preview(PilotProfileId, new PlanConstraints { AvailableMinutes = 20 }));

        plan.Should().NotBeNull("the preview must still work — this change is logging-only");
        _fixture.Logs.Entries.Should().NotBeEmpty("the builder logs its progress");
        AssertNoProfileIdAnywhere();
    }

    [Fact]
    public async Task StartingGenerationLog_KeepsItsNonSensitiveFields()
    {
        SeedPilotLearner();
        _fixture.Logs.Clear();

        await _fixture.CreateBuilder().BuildPlanAsync(
            PlanBuildRequest.Preview(PilotProfileId, new PlanConstraints { AvailableMinutes = 20 }));

        var start = _fixture.Logs.Entries
            .Single(e => e.Message.StartsWith("Starting deterministic plan generation", StringComparison.Ordinal));

        start.State.Should().Contain(p => p.Key == "Constrained" && p.Value == "True");
        start.State.Should().Contain(p => p.Key == "AllowWrites" && p.Value == "False");
        start.State.Should().Contain(p => p.Key == "Scoped" && p.Value == "True");
        start.State.Should().NotContain(p => p.Key == "UserProfileId",
            "the identifier field itself is gone, not merely hidden from the template");
        AssertNoProfileIdAnywhere();
    }

    [Fact]
    public async Task UnconstrainedGeneration_DoesNotLogTheRawProfileId()
    {
        SeedPilotLearner();
        _fixture.Logs.Clear();

        var plan = await _fixture.CreateBuilder().BuildPlanAsync(PilotProfileId);

        plan.Should().NotBeNull();
        AssertNoProfileIdInPlanServices();
        AssertNoResourceIdentityAnywhere();
    }

    [Fact]
    public async Task CloserSelectionLogs_DoNotCarryTheRawProfileId()
    {
        // A long session reaches STEP 4, whose closer-selection logs previously
        // emitted userProfileId at Information level on every plan build.
        SeedPilotLearner(sessionMinutes: 60);
        _fixture.Logs.Clear();

        await _fixture.CreateBuilder().BuildPlanAsync(PlanBuildRequest.Preview(PilotProfileId));

        _fixture.Logs.Entries
            .Should().Contain(e => e.Message.Contains("STEP 4 closer", StringComparison.Ordinal),
                "the closer-selection branch must actually run for this assertion to mean anything");
        AssertNoProfileIdAnywhere();
    }

    [Fact]
    public async Task InvalidConstraints_DoNotLogTheRawProfileId()
    {
        SeedPilotLearner();
        _fixture.Logs.Clear();

        var plan = await _fixture.CreateBuilder().BuildPlanAsync(
            PlanBuildRequest.Preview(PilotProfileId, new PlanConstraints { AvailableMinutes = 500 }));

        plan.Should().BeNull();
        _fixture.Logs.Entries.Should().Contain(e => e.Message.Contains("Plan constraints rejected", StringComparison.Ordinal));
        AssertNoProfileIdAnywhere();
    }

    [Fact]
    public async Task MissingProfile_DoesNotLogTheRawProfileId()
    {
        // No profile seeded: the "no user profile found" warning used to echo
        // the very id the caller supplied.
        _fixture.Logs.Clear();

        var plan = await _fixture.CreateBuilder().BuildPlanAsync(PlanBuildRequest.Preview(PilotProfileId));

        plan.Should().BeNull();
        _fixture.Logs.Entries.Should().Contain(e => e.Message.Contains("No user profile found", StringComparison.Ordinal));
        AssertNoProfileIdAnywhere();
    }

    [Fact]
    public async Task NoFeasiblePlan_DoesNotLogTheRawProfileId()
    {
        // No vocabulary at all, so there is no review block to fall back on.
        _fixture.SeedUserProfile(40, userId: PilotProfileId);
        _fixture.SeedResource(
            title: "Pilot Resource",
            mediaType: "Podcast",
            transcript: "Transcript text",
            userProfileId: PilotProfileId);
        _fixture.SeedSkill();
        _fixture.Logs.Clear();

        // 3 minutes with no vocabulary leaves no feasible block.
        var plan = await _fixture.CreateBuilder().BuildPlanAsync(
            PlanBuildRequest.Preview(PilotProfileId, new PlanConstraints { AvailableMinutes = 3 }));

        plan.Should().BeNull();
        _fixture.Logs.Entries.Should().Contain(e => e.Message.Contains("No feasible plan", StringComparison.Ordinal));
        AssertNoProfileIdAnywhere();
    }

    [Fact]
    public async Task SelectedResourceLog_CarriesShapeOnly()
    {
        SeedPilotLearner();
        _fixture.Logs.Clear();

        var plan = await _fixture.CreateBuilder().BuildPlanAsync(PlanBuildRequest.Preview(PilotProfileId));

        plan!.PrimaryResource.Should().NotBeNull("the resource must still be selected — this change is logging-only");

        var selected = _fixture.Logs.Entries
            .Single(e => e.Message.StartsWith("Selected primary resource", StringComparison.Ordinal));

        selected.State.Should().Contain(p => p.Key == "ResourceSelected" && p.Value == "True");
        selected.State.Should().Contain(p => p.Key == "HasAudio");
        selected.State.Should().Contain(p => p.Key == "HasTranscript");
        selected.State.Should().Contain(p => p.Key == "DaysSinceLastUse",
            "the bounded, timestamp-derived last-use field stays useful");
        selected.State.Should().NotContain(p => p.Key == "ResourceTitle");
        selected.State.Should().NotContain(p => p.Key == "ResourceId");

        AssertNoProfileIdAnywhere();
    }

    [Fact]
    public async Task CoachPreview_DoesNotLogResourceIdOrTitle()
    {
        SeedPilotLearner();
        _fixture.Logs.Clear();

        var plan = await _fixture.CreateBuilder().BuildPlanAsync(
            PlanBuildRequest.Preview(PilotProfileId, new PlanConstraints { AvailableMinutes = 30 }));

        plan.Should().NotBeNull();
        plan!.PrimaryResource!.Id.Should().Be(PilotResourceId, "the plan itself still carries the resource");
        _fixture.Logs.Entries.Should().Contain(e => e.Message.Contains("Vocab review needed", StringComparison.Ordinal));
        AssertNoResourceIdentityAnywhere();
        AssertNoProfileIdAnywhere();
    }

    [Fact]
    public async Task WriteEnabledBuild_DoesNotLogResourceIdOrTitle()
    {
        SeedPilotLearner(sessionMinutes: 60);
        _fixture.Logs.Clear();

        // AllowWrites=true is the legacy Today's Plan path. Coach never uses it,
        // but it shares every log site, so it must not reintroduce the leak.
        var plan = await _fixture.CreateBuilder().BuildPlanAsync(
            new PlanBuildRequest { UserProfileId = PilotProfileId, AllowWrites = true });

        plan.Should().NotBeNull();
        AssertNoResourceIdentityAnywhere();
        AssertNoProfileIdInPlanServices();
    }

    [Fact]
    public async Task ResourceWithoutCompatibleActivities_DoesNotLogResourceIdOrTitle()
    {
        // A transcript-free podcast under an audio ban leaves no input activity,
        // which trips the "no compatible input activities" warning.
        _fixture.SeedUserProfile(40, userId: PilotProfileId);
        _fixture.SeedResource(
            id: PilotResourceId,
            title: PilotResourceTitle,
            mediaType: "Podcast",
            transcript: null,
            userProfileId: PilotProfileId);
        _fixture.SeedSkill();
        _fixture.Logs.Clear();

        await _fixture.CreateBuilder().BuildPlanAsync(
            PlanBuildRequest.Preview(PilotProfileId, new PlanConstraints { AudioAllowed = false }));

        _fixture.Logs.Entries.Should().Contain(
            e => e.Message.Contains("no compatible input activities", StringComparison.Ordinal));
        AssertNoResourceIdentityAnywhere();
        AssertNoProfileIdAnywhere();
    }

    [Fact]
    public void TheGuardItselfDetectsALeak()
    {
        // Sanity check for the assertion helper: if a log ever carries the id
        // again, AssertNoProfileIdInLogs must fail rather than silently pass.
        var provider = new CapturingLoggerProvider();
        var logger = provider.CreateLogger("test");
        logger.LogInformation("leaking {UserProfileId}", PilotProfileId);

        logger.LogInformation("leaking {ResourceTitle} {ResourceId}", PilotResourceTitle, PilotResourceId);

        foreach (var secret in Secrets)
        {
            provider.Entries
                .Any(e => e.AllText().Any(t => t.Contains(secret, StringComparison.OrdinalIgnoreCase)))
                .Should().BeTrue($"the guard must detect '{secret}' if it is ever logged again");
        }
    }
}
