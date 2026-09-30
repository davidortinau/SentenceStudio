using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using SentenceStudio.Abstractions;
using SentenceStudio.Data;
using SentenceStudio.Services;

namespace SentenceStudio.Native.Adapters;

/// <summary>
/// Confirms readiness only while the device scope, profile preference, and
/// authenticated JWT agree, after a non-swallowing CoreSync run and a separate
/// server response that echoes the same profile owner.
/// A local profile, connectivity check, or legacy TriggerSyncAsync return is
/// never sufficient.
/// </summary>
public sealed class OwnerBoundNativeSyncReadiness(
    IAuthService auth,
    IPreferencesService preferences,
    DeviceUserScopeProvider scope,
    IConnectivityService connectivity,
    IRemoteSyncCompletion sync,
    IHttpClientFactory clients,
    ILogger<OwnerBoundNativeSyncReadiness> logger) : INativeSyncReadiness
{
    public async Task<NativeSyncOutcome> SynchronizeAsync(
        string userId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(userId))
            throw new UnauthorizedAccessException("Sync requires an authenticated owner.");
        cancellationToken.ThrowIfCancellationRequested();

        var inheritedLease = scope.TryGetLeasedReadinessOwner(out var leasedOwner);
        var stamp = scope.CaptureStamp();
        if (inheritedLease
            ? !string.Equals(leasedOwner, userId, StringComparison.Ordinal)
            : !string.Equals(stamp.PublishedOwner, userId, StringComparison.Ordinal))
            return NativeSyncOutcome.Unconfirmed;

        using var ownedLease = inheritedLease
            ? null
            : scope.TryBeginReadiness(userId, stamp);
        if (!inheritedLease && ownedLease is null)
            return NativeSyncOutcome.Unconfirmed;

        try
        {
            if (await GetAuthenticatedTokenAsync(userId, cancellationToken) is null)
                return NativeSyncOutcome.Unconfirmed;
            if (!connectivity.IsInternetAvailable)
                return NativeSyncOutcome.Offline;

            // The shared operation waits on the same semaphore as TriggerSyncAsync,
            // actually awaits CoreSync, and propagates provider failures.
            await sync.SynchronizeForReadinessAsync(cancellationToken);

            // Auth or the active device scope may have changed while CoreSync ran.
            // Send the presently verified JWT explicitly: AuthClient has no
            // handler that could silently replace this bearer after validation.
            var token = await GetAuthenticatedTokenAsync(userId, cancellationToken);
            if (token is null)
                return NativeSyncOutcome.Unconfirmed;

            using var request = new HttpRequestMessage(
                HttpMethod.Get, $"/api/v1/profile/{Uri.EscapeDataString(userId)}");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var response = await clients.CreateClient("AuthClient").SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (response.StatusCode != HttpStatusCode.OK)
                return NativeSyncOutcome.Unconfirmed;

            await using var body = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var profile = await JsonDocument.ParseAsync(
                body, cancellationToken: cancellationToken);
            if (profile.RootElement.ValueKind != JsonValueKind.Object
                || !profile.RootElement.TryGetProperty("id", out var id)
                || id.ValueKind != JsonValueKind.String
                || !string.Equals(id.GetString(), userId, StringComparison.Ordinal))
                return NativeSyncOutcome.Unconfirmed;

            // Even a matching server response cannot authorize a different
            // device session after a sign-out/owner switch during the HTTP request.
            var authenticatedAfterProbe =
                await GetAuthenticatedTokenAsync(userId, cancellationToken) is not null;
            cancellationToken.ThrowIfCancellationRequested();
            return authenticatedAfterProbe
                ? NativeSyncOutcome.Confirmed
                : NativeSyncOutcome.Unconfirmed;
        }
        catch (Exception ex) when (ex is not OperationCanceledException
            || !cancellationToken.IsCancellationRequested)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // CoreSync/HTTP exception messages may contain request details;
            // never log a token, URL, response body, or exception message.
            logger.LogWarning("Native sync readiness remains unconfirmed ({FailureType}).",
                ex.GetType().Name);
            return NativeSyncOutcome.Unconfirmed;
        }
    }

    private async Task<string?> GetAuthenticatedTokenAsync(
        string userId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!HasActiveOwner(userId))
            return null;

        // This can refresh an expiring JWT; do not trust an optimistic local
        // profile pointer or an expired cached principal as authentication.
        var token = await auth.GetAccessTokenAsync([]);
        cancellationToken.ThrowIfCancellationRequested();
        if (!auth.IsSignedIn || string.IsNullOrWhiteSpace(token)
            || !HasActiveOwner(userId))
            return null;

        return string.Equals(
                NativeAuthPresenter.ParseOwnerClaim(token), userId, StringComparison.Ordinal)
            ? token
            : null;
    }

    private bool HasActiveOwner(string userId) =>
        scope.TryGetLeasedReadinessOwner(out var activeOwner)
        && string.Equals(activeOwner, userId, StringComparison.Ordinal)
        && string.Equals(
            preferences.Get(UserProfileRepository.ActiveProfileIdKey, string.Empty),
            userId, StringComparison.Ordinal);
}

/// <summary>
/// The existing CoreSync bearer handler permits unauthenticated requests on
/// token failure. This final handler refuses native sync unless the request
/// still belongs to the current owner's revocable readiness lease.
/// </summary>
public sealed class OwnerBoundNativeSyncBearerHandler(
    IAuthService auth,
    IPreferencesService preferences,
    DeviceUserScopeProvider scope) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!scope.TryGetLeasedReadinessOwner(out var owner) || !HasOwner(owner))
            throw new UnauthorizedAccessException("Native sync requires a verified owner.");

        var token = await auth.GetAccessTokenAsync([]);
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(token)
            || !string.Equals(
                NativeAuthPresenter.ParseOwnerClaim(token), owner, StringComparison.Ordinal)
            || !HasOwner(owner))
            throw new UnauthorizedAccessException("Native sync owner changed before sending.");

        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Remove(SharedSyncRegistration.NativeOwnerHeader);
        request.Headers.Add(SharedSyncRegistration.NativeOwnerHeader, owner);
        return await base.SendAsync(request, cancellationToken);
    }

    private bool HasOwner(string owner) =>
        auth.IsSignedIn
        && scope.TryGetLeasedReadinessOwner(out var currentOwner)
        && string.Equals(currentOwner, owner, StringComparison.Ordinal)
        && string.Equals(
            preferences.Get(UserProfileRepository.ActiveProfileIdKey, string.Empty),
            owner, StringComparison.Ordinal);
}

/// <summary>
/// Explicit opt-in for heads with a real Shared SyncService and its configured
/// AuthClient. Heads without CoreSync keep the safe default readiness port.
/// </summary>
public static class OwnerBoundNativeSyncReadinessRegistration
{
    public static IServiceCollection AddOwnerBoundNativeSyncReadiness(this IServiceCollection services)
    {
        services.TryAddTransient<AuthenticatedHttpMessageHandler>();
        services.TryAddTransient<OwnerBoundNativeSyncBearerHandler>();
        services.AddHttpClient("HttpClientToServer")
            .AddHttpMessageHandler<OwnerBoundNativeSyncBearerHandler>();
        services.TryAddSingleton<IRemoteSyncCompletion>(serviceProvider =>
            serviceProvider.GetRequiredService<ISyncService>() is SyncService realSync
                ? realSync
                : throw new NotSupportedException(
                    "Owner-bound sync readiness requires the real Shared CoreSync SyncService."));
        services.Replace(ServiceDescriptor.Singleton<
            INativeSyncReadiness, OwnerBoundNativeSyncReadiness>());
        return services;
    }
}
