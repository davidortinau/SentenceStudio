using SentenceStudio.WebUI.Services;

namespace SentenceStudio.UI.Tests.Coach;

/// <summary>
/// The full truth table for whether a coach surface may be mounted at all.
/// </summary>
/// <remarks>
/// This is the security-relevant half of the shell's markup, so it is pinned as a table rather
/// than as a handful of happy paths. The defect it replaces mounted the overlay unconditionally,
/// outside the layout's authenticated branch, and the overlay then reported itself authenticated
/// because it had been mounted — a circular assurance that neither component actually checked.
/// </remarks>
public class CoachSurfaceGateTests
{
    [Theory]
    // Not signed in: nothing, whatever the flags say.
    [InlineData(false, false, false, false, false, CoachSurface.None)]
    [InlineData(false, false, false, true, true, CoachSurface.None)]
    // Onboarding: the learner has no plan and no coach yet.
    [InlineData(true, true, false, true, true, CoachSurface.None)]
    // Mid-sync: the shell is masked and the profile is not settled.
    [InlineData(true, false, true, true, true, CoachSurface.None)]
    // Both at once, which the router can produce.
    [InlineData(true, true, true, true, true, CoachSurface.None)]
    // Entitled, availability unknown: the legacy host, which handles its own pre-load.
    [InlineData(true, false, false, false, false, CoachSurface.LegacyWorkspaceHost)]
    // Entitled, availability known but the overlay is off for this deployment.
    [InlineData(true, false, false, true, false, CoachSurface.LegacyWorkspaceHost)]
    // Entitled and the overlay is on.
    [InlineData(true, false, false, true, true, CoachSurface.SamOverlay)]
    public void The_gate_decides_exactly_one_surface(
        bool isAuthenticated,
        bool isOnboarding,
        bool isSyncing,
        bool flagsLoaded,
        bool samAvailable,
        CoachSurface expected)
    {
        CoachSurfaceGate
            .Decide(isAuthenticated, isOnboarding, isSyncing, flagsLoaded, samAvailable)
            .Should().Be(expected);
    }

    /// <summary>
    /// A flag that says "the overlay is available" cannot re-open the gate for a signed-out shell.
    /// </summary>
    /// <remarks>
    /// Stated on its own because a cached flag is exactly what survives a sign-out on a persistent
    /// scope, and "availability is still true from the previous learner" must not be able to draw
    /// anything.
    /// </remarks>
    [Fact]
    public void A_stale_availability_flag_cannot_mount_a_surface_for_a_signed_out_shell()
    {
        CoachSurfaceGate
            .Decide(isAuthenticated: false, isOnboarding: false, isSyncing: false,
                    flagsLoaded: true, isSamOverlayAvailable: true)
            .Should().Be(CoachSurface.None);
    }

    [Fact]
    public void No_surface_is_allowed_before_authentication_resolves()
    {
        CoachSurfaceGate.AllowsAnySurface(isAuthenticated: false, isOnboarding: false, isSyncing: false)
            .Should().BeFalse();

        CoachSurfaceGate.AllowsAnySurface(isAuthenticated: true, isOnboarding: false, isSyncing: false)
            .Should().BeTrue();
    }
}
