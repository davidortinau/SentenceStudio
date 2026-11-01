using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SentenceStudio.Abstractions;
using SentenceStudio.Data;
using SentenceStudio.Services;
using SentenceStudio.Services.Api;

namespace SentenceStudio.Native.Adapters;

/// <summary>
/// Reuses the existing <see cref="CoachApiClient"/> over a per-call client whose
/// bearer handler re-verifies, for every request, that the device scope, the
/// active-profile preference, the signed-in state, and the JWT owner claim all
/// name the owner the caller asked for. A result that arrives after the owner
/// changed is discarded by throwing, never returned.
/// </summary>
public sealed class OwnerBoundNativeSamClient(
    IAuthService auth,
    IPreferencesService preferences,
    DeviceUserScopeProvider scope,
    IHttpMessageHandlerFactory handlers,
    Uri apiRoot) : INativeSamClient
{
    internal const string ClientName = "NativeOwnerSamClient";

    // Server bounds a turn at 45 s; allow headroom like the AppLib registration.
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(75);

    public bool IsConfigured => true;

    public async Task<T> CallAsync<T>(
        string owner,
        Func<ICoachApiClient, CancellationToken, Task<T>> call,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(call);
        cancellationToken.ThrowIfCancellationRequested();
        var stamp = scope.CaptureStamp();
        bool HasOwner() =>
            !string.IsNullOrWhiteSpace(owner)
            && string.Equals(stamp.PublishedOwner, owner, StringComparison.Ordinal)
            && scope.IsCurrent(stamp)
            && scope.TryGetUserProfileId(out var active)
            && string.Equals(active, owner, StringComparison.Ordinal)
            && string.Equals(
                preferences.Get(UserProfileRepository.ActiveProfileIdKey, string.Empty),
                owner, StringComparison.Ordinal)
            && auth.IsSignedIn;

        if (!HasOwner())
            throw new UnauthorizedAccessException(
                "Sam requests require a current authenticated owner.");

        var guard = new OwnerBoundNativeAiBearerHandler(auth, apiRoot, owner, HasOwner)
        {
            InnerHandler = handlers.CreateHandler(ClientName)
        };
        using var client = new HttpClient(guard, disposeHandler: false)
        {
            BaseAddress = apiRoot,
            Timeout = RequestTimeout
        };
        var result = await call(new CoachApiClient(client), cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (!HasOwner())
            throw new UnauthorizedAccessException(
                "Sam owner changed before the result could be applied.");
        return result;
    }
}

public static class OwnerBoundNativeSamClientRegistration
{
    public static IServiceCollection AddOwnerBoundNativeSam(
        this IServiceCollection services, Uri apiRoot)
    {
        ArgumentNullException.ThrowIfNull(services);
        if (apiRoot is not { IsAbsoluteUri: true }
            || apiRoot.Scheme != Uri.UriSchemeHttp
                && apiRoot.Scheme != Uri.UriSchemeHttps)
            throw new ArgumentException("A valid native API origin is required.", nameof(apiRoot));

        // Sam reads Today's Plan, progress, and practice windows for the learner's local day. The
        // API builds that day from X-Timezone and falls back to UTC without it, so after UTC
        // midnight a device plan dated today looked absent ("no plan for today yet"). The handler
        // resolves the device IPlanDateContext per request; it never overrides an explicit header.
        services.TryAddTransient<PlanTimeZoneHeaderHandler>();
        services.AddHttpClient(OwnerBoundNativeSamClient.ClientName)
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
            {
                AllowAutoRedirect = false
            })
            .AddHttpMessageHandler<PlanTimeZoneHeaderHandler>();
        services.TryAddSingleton<INativeSamClient>(provider =>
            new OwnerBoundNativeSamClient(
                provider.GetRequiredService<IAuthService>(),
                provider.GetRequiredService<IPreferencesService>(),
                provider.GetRequiredService<DeviceUserScopeProvider>(),
                provider.GetRequiredService<IHttpMessageHandlerFactory>(),
                apiRoot));
        return services;
    }
}
