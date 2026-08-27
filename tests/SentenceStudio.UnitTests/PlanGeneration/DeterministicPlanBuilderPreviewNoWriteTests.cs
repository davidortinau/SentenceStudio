using Xunit;
using FluentAssertions;
using SentenceStudio.Services.Plans;

namespace SentenceStudio.UnitTests.PlanGeneration;

/// <summary>
/// Proves the pure-preview path performs zero database writes.
/// </summary>
/// <remarks>
/// <c>UserProfileRepository.EnsureSmartResourcesAsync</c> is the only write on
/// the plan-generation path, and it is a no-op unless <c>SmartResourceService</c>
/// is registered — so this class builds its own fixture with that service wired
/// up. Every test also uses its OWN user id, because
/// <c>UserProfileRepository._smartResourcesEnsured</c> is a process-wide static
/// cache: sharing an id would let one test's seeding mask another test's write
/// and turn a real regression into a green run.
/// </remarks>
public class DeterministicPlanBuilderPreviewNoWriteTests : IDisposable
{
    private readonly PlanGenerationTestFixture _fixture = PlanGenerationTestFixture.CreateWithSmartResourceSeeding();

    public DeterministicPlanBuilderPreviewNoWriteTests() => _fixture.ClearAllData();

    public void Dispose() => _fixture.Dispose();

    private void SeedUser(string userId)
    {
        _fixture.SeedUserProfile(sessionMinutes: 40, userId: userId);
        _fixture.SeedResource(
            title: $"Resource for {userId}",
            mediaType: "Podcast",
            transcript: "Transcript text",
            userProfileId: userId);
    }

    [Fact]
    public async Task PreviewRequest_PerformsZeroWrites()
    {
        const string userId = "preview-no-write-constrained";
        SeedUser(userId);

        var before = _fixture.CountAllRows();
        var smartBefore = _fixture.CountSmartResources();

        var plan = await _fixture.CreateBuilder()
            .BuildPlanAsync(PlanBuildRequest.Preview(userId, new PlanConstraints { AvailableMinutes = 20 }));

        plan.Should().NotBeNull("a preview still returns a usable plan");
        _fixture.CountSmartResources().Should().Be(smartBefore,
            "a pure preview must not call EnsureSmartResourcesAsync");
        _fixture.CountAllRows().Should().Be(before, "a pure preview writes nothing at all");
    }

    [Fact]
    public async Task PreviewRequest_WithoutConstraints_AlsoPerformsZeroWrites()
    {
        const string userId = "preview-no-write-unconstrained";
        SeedUser(userId);

        var before = _fixture.CountAllRows();
        var smartBefore = _fixture.CountSmartResources();

        var plan = await _fixture.CreateBuilder().BuildPlanAsync(PlanBuildRequest.Preview(userId));

        plan.Should().NotBeNull();
        _fixture.CountSmartResources().Should().Be(smartBefore);
        _fixture.CountAllRows().Should().Be(before);
    }

    [Fact]
    public async Task NonPreviewRequest_StillSeedsSmartResources()
    {
        // The control case: without this, the no-write assertions above could
        // pass because nothing ever writes, rather than because AllowWrites=false
        // suppressed the write.
        const string userId = "preview-control-user";
        SeedUser(userId);

        _fixture.CountSmartResources().Should().Be(0);

        var plan = await _fixture.CreateBuilder()
            .BuildPlanAsync(new PlanBuildRequest { UserProfileId = userId });

        plan.Should().NotBeNull();
        _fixture.CountSmartResources().Should().BeGreaterThan(0,
            "the default write policy preserves today's smart-resource seeding");
    }

    [Fact]
    public async Task LegacyOverload_StillSeedsSmartResources()
    {
        const string userId = "preview-legacy-user";
        SeedUser(userId);

        _fixture.CountSmartResources().Should().Be(0);

        var plan = await _fixture.CreateBuilder().BuildPlanAsync(userId);

        plan.Should().NotBeNull();
        _fixture.CountSmartResources().Should().BeGreaterThan(0,
            "the legacy overload is unchanged by the preview work");
    }
}
