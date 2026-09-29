using System.IdentityModel.Tokens.Jwt;
using System.Net.Http.Headers;
using Microsoft.Extensions.Logging;
using SentenceStudio.Contracts;
using SentenceStudio.Services;

namespace SentenceStudio.Native.iOS.Platform;

public sealed class NativeApiSyncReadiness(
    IAuthService auth,
    IHttpClientFactory clients,
    ILogger<NativeApiSyncReadiness> logger) : INativeSyncReadiness
{
    public async Task<NativeSyncOutcome> SynchronizeAsync(
        string userId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        if (!auth.IsSignedIn)
            return NativeSyncOutcome.Unconfirmed;

        var token = await auth.GetAccessTokenAsync([]);
        if (string.IsNullOrWhiteSpace(token))
            return NativeSyncOutcome.Unconfirmed;
        var claim = new JwtSecurityTokenHandler().ReadJwtToken(token).Claims
            .FirstOrDefault(value => value.Type == AuthClaimTypes.UserProfileId)?.Value;
        if (!string.Equals(claim, userId, StringComparison.Ordinal))
            throw new UnauthorizedAccessException("The sync probe owner does not match the token.");

        using var request = new HttpRequestMessage(
            HttpMethod.Get, $"/api/v1/profile/{Uri.EscapeDataString(userId)}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        HttpResponseMessage response;
        try
        {
            response = await clients.CreateClient("AuthClient")
                .SendAsync(request, cancellationToken);
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new HttpRequestException("Native sync readiness probe timed out.", ex);
        }
        using (response)
        {
            response.EnsureSuccessStatusCode();
            logger.LogInformation(
                "The authenticated owner profile is reachable; CoreSync writes remain unconfirmed.");
            return NativeSyncOutcome.Unconfirmed;
        }
    }
}
