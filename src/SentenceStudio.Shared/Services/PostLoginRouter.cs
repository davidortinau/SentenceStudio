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
    {
        if (_syncService.IsInitialSyncInProgress)
        {
            _logger.LogInformation("PostLoginRouter: initial sync in progress — deferring route decision");
            return new PostLoginRoute(Path: null, DeferUntilSyncCompletes: true, ShouldMarkOnboarded: false);
        }

        UserProfile? profile = null;
        try
        {
            profile = await _profileRepo.GetAsync();
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
