using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using SentenceStudio.Abstractions;
using SentenceStudio.Data;
using SentenceStudio.Services;

namespace SentenceStudio.Native.Adapters;

/// <summary>
/// Asks the API to re-send the active owner's rows of newly synced tables
/// (POST /api/v1/sync/resend, see <see cref="ISyncResendRequester"/>), with the same
/// owner-bound bearer as the other native API calls.
/// </summary>
public sealed class NativeSyncResendRequester(
    IAuthService auth,
    IPreferencesService preferences,
    DeviceUserScopeProvider scope,
    IHttpMessageHandlerFactory handlers,
    ILogger<NativeSyncResendRequester> logger,
    Uri apiRoot) : ISyncResendRequester
{
    public async Task<bool> RequestAsync(string owner, IReadOnlyCollection<string> tables,
        CancellationToken cancellationToken)
    {
        if (tables.Count == 0)
            return true;
        var stamp = scope.CaptureStamp();
        bool HasOwner() =>
            !string.IsNullOrWhiteSpace(owner)
            && stamp.PublishedOwner == owner && scope.IsCurrent(stamp)
            && scope.TryGetUserProfileId(out var active) && active == owner
            && preferences.Get(UserProfileRepository.ActiveProfileIdKey, string.Empty) == owner
            && auth.IsSignedIn;
        if (!HasOwner())
            return false;

        var bearer = new OwnerBoundNativeAiBearerHandler(auth, apiRoot, owner, HasOwner)
        {
            InnerHandler = handlers.CreateHandler(OwnerBoundNativeAiGateway.ClientName)
        };
        using var client = new HttpClient(bearer, disposeHandler: false)
        {
            BaseAddress = apiRoot,
            Timeout = TimeSpan.FromSeconds(30)
        };
        using var reply = await client.PostAsJsonAsync(SyncResendQueue.Route,
            new SyncResendRequest([.. tables]), cancellationToken);
        if (!reply.IsSuccessStatusCode)
        {
            logger.LogWarning("Sync re-send request returned {Status}.", (int)reply.StatusCode);
            return false;
        }
        return true;
    }
}

public static class NativeSyncResendRegistration
{
    public static IServiceCollection AddOwnerBoundNativeSyncResend(
        this IServiceCollection services, Uri apiRoot)
    {
        ArgumentNullException.ThrowIfNull(services);
        if (!apiRoot.IsAbsoluteUri || apiRoot.Scheme is not ("http" or "https"))
            throw new ArgumentException("Sync re-send needs the configured API origin.", nameof(apiRoot));
        services.Replace(ServiceDescriptor.Singleton<ISyncResendRequester>(sp =>
            new NativeSyncResendRequester(
                sp.GetRequiredService<IAuthService>(),
                sp.GetRequiredService<IPreferencesService>(),
                sp.GetRequiredService<DeviceUserScopeProvider>(),
                sp.GetRequiredService<IHttpMessageHandlerFactory>(),
                sp.GetRequiredService<ILogger<NativeSyncResendRequester>>(),
                apiRoot)));
        return services;
    }
}
