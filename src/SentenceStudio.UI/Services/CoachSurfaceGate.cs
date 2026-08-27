namespace SentenceStudio.WebUI.Services;

/// <summary>Which coach surface, if any, the shell should mount.</summary>
public enum CoachSurface
{
    /// <summary>No coach surface at all. Nothing coach-related is in the DOM.</summary>
    None = 0,

    /// <summary>The legacy query-backed modal workspace host.</summary>
    LegacyWorkspaceHost,

    /// <summary>The persistent Sam overlay: FAB plus non-modal panel.</summary>
    SamOverlay
}

/// <summary>
/// Decides whether a coach surface may be mounted, and which one.
/// </summary>
/// <remarks>
/// <para>
/// Extracted from the layout because it is the security-relevant half of the layout's markup and
/// a truth table is testable in a way a Razor conditional is not. The rule it encodes is that a
/// coach surface belongs only to a signed-in learner who is past onboarding and not mid-sync —
/// the same three conditions the navigation already uses — and that "we have not been told yet"
/// is not the same as "yes".
/// </para>
/// <para>
/// The defect this replaces mounted the overlay outside the authenticated branch and let the
/// overlay itself assume it must be authenticated because it had been mounted. Two components
/// each trusting the other to have checked is how an unauthenticated shell ended up rendering a
/// coach surface, so the check now lives in one place that both of them call.
/// </para>
/// </remarks>
public static class CoachSurfaceGate
{
    /// <param name="isAuthenticated">Whether the current principal is authenticated.</param>
    /// <param name="isOnboarding">Whether the learner is inside the onboarding flow.</param>
    /// <param name="isSyncing">Whether the initial profile sync is still masking the shell.</param>
    /// <param name="flagsLoaded">Whether coach availability has been read for this learner.</param>
    /// <param name="isSamOverlayAvailable">Whether the server enabled the Sam overlay UX.</param>
    public static CoachSurface Decide(
        bool isAuthenticated,
        bool isOnboarding,
        bool isSyncing,
        bool flagsLoaded,
        bool isSamOverlayAvailable)
    {
        if (!isAuthenticated || isOnboarding || isSyncing)
        {
            return CoachSurface.None;
        }

        // Before availability is known the legacy host renders: it handles its own pre-load state
        // and stays inert until a learner asks for it, whereas the overlay draws a permanently
        // visible control. Once the answer arrives the layout re-renders and this settles.
        return flagsLoaded && isSamOverlayAvailable
            ? CoachSurface.SamOverlay
            : CoachSurface.LegacyWorkspaceHost;
    }

    /// <summary>True when any coach surface may render.</summary>
    public static bool AllowsAnySurface(bool isAuthenticated, bool isOnboarding, bool isSyncing) =>
        Decide(isAuthenticated, isOnboarding, isSyncing, flagsLoaded: false, isSamOverlayAvailable: false)
            != CoachSurface.None;
}
