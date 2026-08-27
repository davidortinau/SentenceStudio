using SentenceStudio.Contracts.Coach;
using SentenceStudio.Services.Api;
using SentenceStudio.WebUI.Services;

namespace SentenceStudio.UI.Tests.Coach;

/// <summary>
/// Proves the MainLayout host-selection lifecycle: before flags load, the legacy
/// CoachWorkspaceHost renders (IsSamOverlayAvailable is false). After async
/// EnsureLoadedAsync completes, the Loaded event fires, and the flag becomes true —
/// so MainLayout re-renders and mounts SamOverlayHost instead.
///
/// Direct MainLayout bUnit coverage is infeasible (too many cascading dependencies),
/// so we test the extracted selection logic: CoachFeatureFlags + Loaded event contract.
/// </summary>
public class CoachHostSelectionLifecycleTests
{
    private static FakeCoachApiClient SamEnabledClient() => new()
    {
        Availability = new CoachAvailabilityResponse
        {
            IsAvailable = true,
            State = CoachAvailabilityState.Available,
            CanEditPlan = true,
            IsSamOverlayAvailable = true
        }
    };

    private static FakeCoachApiClient SamDisabledClient() => new()
    {
        Availability = new CoachAvailabilityResponse
        {
            IsAvailable = true,
            State = CoachAvailabilityState.Available,
            CanEditPlan = true,
            IsSamOverlayAvailable = false
        }
    };

    /// <summary>
    /// Before EnsureLoadedAsync, IsSamOverlayAvailable defaults to false.
    /// This is the initial synchronous read MainLayout performs.
    /// </summary>
    [Fact]
    public void BeforeLoad_SamOverlayFlagIsFalse()
    {
        var flags = new CoachFeatureFlags(SamEnabledClient());

        flags.HasLoaded.Should().BeFalse();
        flags.IsSamOverlayAvailable.Should().BeFalse();
    }

    /// <summary>
    /// After EnsureLoadedAsync with isSamOverlayAvailable=true, the flag is true
    /// and the Loaded event has fired — enabling MainLayout to re-render and switch
    /// from CoachWorkspaceHost to SamOverlayHost.
    /// </summary>
    [Fact]
    public async Task AfterLoad_SamOverlayEnabled_FlagIsTrueAndLoadedEventFires()
    {
        var flags = new CoachFeatureFlags(SamEnabledClient());
        var loadedFired = false;
        flags.Loaded += () => loadedFired = true;

        await flags.EnsureLoadedAsync();

        flags.HasLoaded.Should().BeTrue();
        flags.IsSamOverlayAvailable.Should().BeTrue();
        loadedFired.Should().BeTrue("MainLayout subscribes to Loaded to re-render");
    }

    /// <summary>
    /// When the server says isSamOverlayAvailable=false, the legacy host remains after load.
    /// </summary>
    [Fact]
    public async Task AfterLoad_SamOverlayDisabled_FlagStaysFalse()
    {
        var flags = new CoachFeatureFlags(SamDisabledClient());
        var loadedFired = false;
        flags.Loaded += () => loadedFired = true;

        await flags.EnsureLoadedAsync();

        flags.HasLoaded.Should().BeTrue();
        flags.IsSamOverlayAvailable.Should().BeFalse();
        loadedFired.Should().BeTrue("re-render still fires to settle the layout");
    }

    /// <summary>
    /// EnsureLoadedAsync is idempotent — no duplicate API calls, no duplicate events.
    /// </summary>
    [Fact]
    public async Task EnsureLoadedAsync_CalledTwice_OnlyOneApiCallAndOneEvent()
    {
        var client = SamEnabledClient();
        var flags = new CoachFeatureFlags(client);
        var loadedCount = 0;
        flags.Loaded += () => loadedCount++;

        await flags.EnsureLoadedAsync();
        await flags.EnsureLoadedAsync();

        client.AvailabilityCalls.Should().Be(1);
        loadedCount.Should().Be(1);
    }

    /// <summary>
    /// Apply (used by CoachWorkspaceState.RefreshAvailabilityAsync) also fires Loaded,
    /// so if availability is loaded by that path first, MainLayout still re-renders.
    /// </summary>
    [Fact]
    public void Apply_FiresLoadedEvent()
    {
        var flags = new CoachFeatureFlags(SamDisabledClient());
        var loadedFired = false;
        flags.Loaded += () => loadedFired = true;

        flags.Apply(new CoachAvailabilityResponse
        {
            IsAvailable = true,
            State = CoachAvailabilityState.Available,
            IsSamOverlayAvailable = true
        });

        flags.IsSamOverlayAvailable.Should().BeTrue();
        loadedFired.Should().BeTrue();
    }

    /// <summary>
    /// When the API is unreachable, flags default to disabled (no crash, no blank state).
    /// CoachWorkspaceHost renders as fallback.
    /// </summary>
    [Fact]
    public async Task ApiFailure_FallsBackToLegacyHost()
    {
        var client = SamEnabledClient();
        client.OnGetAvailability = () => throw new HttpRequestException("api down");
        var flags = new CoachFeatureFlags(client);

        await flags.EnsureLoadedAsync();

        flags.HasLoaded.Should().BeTrue();
        flags.IsSamOverlayAvailable.Should().BeFalse("a gate that crashes is worse than one that closes");
    }

    /// <summary>
    /// Simulates the MainLayout host-selection logic at three lifecycle points:
    /// 1. Initial sync render → legacy host (flags not loaded)
    /// 2. After EnsureLoadedAsync with SamOverlay=true → SamOverlayHost
    /// 3. After EnsureLoadedAsync with SamOverlay=false → legacy host
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task HostSelectionLogic_MatchesMainLayoutBranching(bool samOverlayEnabled)
    {
        var client = samOverlayEnabled ? SamEnabledClient() : SamDisabledClient();
        var flags = new CoachFeatureFlags(client);

        // Phase 1: synchronous check before load — always legacy
        var selectsSam = flags.HasLoaded && flags.IsSamOverlayAvailable;
        selectsSam.Should().BeFalse("before load, legacy host always renders");

        // Phase 2: after async load
        await flags.EnsureLoadedAsync();
        selectsSam = flags.HasLoaded && flags.IsSamOverlayAvailable;
        selectsSam.Should().Be(samOverlayEnabled);
    }
}
