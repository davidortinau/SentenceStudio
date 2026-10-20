using System.Net.Http.Headers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using SentenceStudio.Abstractions;
using SentenceStudio.Data;
using SentenceStudio.Services;
using SentenceStudio.Services.Api;

namespace SentenceStudio.Native.Adapters;

public sealed class OwnerBoundNativeAiGateway(
    IAuthService auth,
    IPreferencesService preferences,
    DeviceUserScopeProvider scope,
    IHttpMessageHandlerFactory handlers,
    ILogger<AiGatewayClient> aiLogger,
    Uri apiRoot) : IOwnerBoundNativeAiGateway
{
    internal const string ClientName = "NativeOwnerAiClient";

    public async Task<T?> SendPromptAsync<T>(
        string owner, string prompt, CancellationToken cancellationToken)
    {
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
                "Native AI requests require a current authenticated owner.");

        var guard = new OwnerBoundNativeAiBearerHandler(
            auth, apiRoot, owner, HasOwner)
        {
            InnerHandler = handlers.CreateHandler(ClientName)
        };
        // HttpMessageHandlerFactory owns the pooled inner pipeline.
        using var client = new HttpClient(guard, disposeHandler: false)
        {
            BaseAddress = apiRoot
        };
        var gateway = new AiGatewayClient(new AiApiClient(client), aiLogger);
        var response = await gateway.SendPromptAsync<T>(
            prompt, cancellationToken: cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (!HasOwner())
            throw new UnauthorizedAccessException(
                "Native AI owner changed before the result could be applied.");
        return response;
    }
}

internal sealed class OwnerBoundNativeAiBearerHandler(
    IAuthService auth, Uri apiRoot, string owner, Func<bool> hasOwner)
    : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var destination = request.RequestUri;
        if (destination is not { IsAbsoluteUri: true }
            || destination.Scheme != apiRoot.Scheme
            || !string.Equals(destination.Host, apiRoot.Host,
                StringComparison.OrdinalIgnoreCase)
            || destination.Port != apiRoot.Port
            || !hasOwner())
            throw new UnauthorizedAccessException(
                "Native AI request destination or owner is unavailable.");

        var token = await auth.GetAccessTokenAsync([]);
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(token) || !auth.IsSignedIn
            || !hasOwner()
            || !string.Equals(NativeAuthPresenter.ParseOwnerClaim(token),
                owner, StringComparison.Ordinal))
            throw new UnauthorizedAccessException(
                "Native AI token does not match the current owner.");

        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var response = await base.SendAsync(request, cancellationToken);
        if (!hasOwner())
        {
            response.Dispose();
            throw new UnauthorizedAccessException(
                "Native AI owner changed during the request.");
        }
        return response;
    }
}

public static class OwnerBoundNativeAiGatewayRegistration
{
    public static IServiceCollection AddOwnerBoundNativeAiGateway(
        this IServiceCollection services, Uri apiRoot)
    {
        ArgumentNullException.ThrowIfNull(services);
        if (apiRoot is not { IsAbsoluteUri: true }
            || apiRoot.Scheme != Uri.UriSchemeHttp
                && apiRoot.Scheme != Uri.UriSchemeHttps)
            throw new ArgumentException("A valid native API origin is required.", nameof(apiRoot));

        services.AddHttpClient(OwnerBoundNativeAiGateway.ClientName)
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
            {
                AllowAutoRedirect = false
            });
        services.TryAddSingleton<IOwnerBoundNativeAiGateway>(provider =>
            new OwnerBoundNativeAiGateway(
                provider.GetRequiredService<IAuthService>(),
                provider.GetRequiredService<IPreferencesService>(),
                provider.GetRequiredService<DeviceUserScopeProvider>(),
                provider.GetRequiredService<IHttpMessageHandlerFactory>(),
                provider.GetRequiredService<ILogger<AiGatewayClient>>(),
                apiRoot));
        return services;
    }
}
