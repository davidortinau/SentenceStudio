using Microsoft.Extensions.Logging;
using SentenceStudio.Data;

namespace SentenceStudio.Services;

/// <summary>
/// Decides where the user should land after a successful login.
/// Centralizes the post-login routing rule so it's testable and consistent
/// between LoginPage navigation and MainLayout's onboarding-redirect fallback.
/// </summary>
public interface IPostLoginRouter
{
    /// <summary>
    /// Resolves the post-login destination based on local profile state and in-flight sync.
    /// </summary>
    /// <returns>
    /// A <see cref="PostLoginRoute"/> describing where to navigate.
    /// When <see cref="PostLoginRoute.DeferUntilSyncCompletes"/> is true, the caller should
    /// wait for <see cref="ISyncService.InitialSyncCompleted"/> before re-deciding.
    /// </returns>
    Task<PostLoginRoute> DecideRouteAsync();

    /// <summary>
    /// Resolves the post-login destination using an explicit user profile id, bypassing
    /// the <c>IPreferencesService</c>-based lookup. Required for the Blazor Web App
    /// interactive circuit pass where <c>IHttpContextAccessor.HttpContext</c> is null
    /// and the per-user preference cannot be resolved from request state.
    /// </summary>
    /// <param name="activeProfileId">
    /// The active user's <see cref="UserProfile.Id"/>, typically read from the
    /// <c>user_profile_id</c> claim on the cascaded <c>AuthenticationState</c>.
    /// When null or empty, falls back to <see cref="DecideRouteAsync()"/>.
    /// </param>
    Task<PostLoginRoute> DecideRouteAsync(string? activeProfileId);
}

/// <summary>
/// Result of a post-login routing decision.
/// </summary>
/// <param name="Path">Target URL ("/" or "/onboarding"), or null when the decision is deferred.</param>
/// <param name="DeferUntilSyncCompletes">True if the caller should wait for the initial sync to complete before navigating.</param>
/// <param name="ShouldMarkOnboarded">True if the caller should set the "is_onboarded" preference to true after handling the result.</param>
public record PostLoginRoute(string? Path, bool DeferUntilSyncCompletes, bool ShouldMarkOnboarded);

public class PostLoginRouter : IPostLoginRouter
{
    private readonly ISyncService _syncService;
    private readonly UserProfileRepository _profileRepo;
    private readonly ILogger<PostLoginRouter> _logger;

    public PostLoginRouter(
        ISyncService syncService,
        UserProfileRepository profileRepo,
        ILogger<PostLoginRouter> logger)
    {
        _syncService = syncService;
        _profileRepo = profileRepo;
        _logger = logger;
    }

    public async Task<PostLoginRoute> DecideRouteAsync()
        => await DecideRouteAsync(activeProfileId: null);

    public async Task<PostLoginRoute> DecideRouteAsync(string? activeProfileId)
    {
        if (_syncService.IsInitialSyncInProgress)
        {
            _logger.LogInformation("PostLoginRouter: initial sync in progress — deferring route decision");
            return new PostLoginRoute(Path: null, DeferUntilSyncCompletes: true, ShouldMarkOnboarded: false);
        }

        UserProfile? profile = null;
        try
        {
            // When the caller supplied an explicit profile id (e.g. from a Blazor
            // cascaded AuthenticationState claim), bypass the IPreferencesService
            // lookup entirely. This is required for the Blazor Web App interactive
            // circuit pass where HttpContext is null and per-user state can't be
            // resolved from request state.
            profile = string.IsNullOrEmpty(activeProfileId)
                ? await _profileRepo.GetAsync()
                : await _profileRepo.GetByIdAsync(activeProfileId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "PostLoginRouter: failed to load local profile — falling back to onboarding");
        }

        var hasPopulatedProfile = profile is not null
            && !string.IsNullOrEmpty(profile.TargetLanguage)
            && !string.IsNullOrEmpty(profile.NativeLanguage);

        if (hasPopulatedProfile)
        {
            _logger.LogInformation("PostLoginRouter: populated profile found — routing to dashboard");
            return new PostLoginRoute(Path: "/", DeferUntilSyncCompletes: false, ShouldMarkOnboarded: true);
        }

        _logger.LogInformation("PostLoginRouter: profile missing or incomplete — routing to onboarding");
        return new PostLoginRoute(Path: "/onboarding", DeferUntilSyncCompletes: false, ShouldMarkOnboarded: false);
    }
}
