#nullable enable
using System.IdentityModel.Tokens.Jwt;
using System.Net.Http;
using System.Net.Http.Json;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;
using SentenceStudio.Abstractions;
using SentenceStudio.Contracts;
using SentenceStudio.Data;

namespace SentenceStudio.Services;

/// <summary>Reads the learner a sign-in token names (the API's user-profile claim).</summary>
public static class JwtOwnerClaim
{
    /// <summary>
    /// The owner of an unexpired token that names exactly one profile, else null. The token is
    /// not validated here; the server validates it on every request.
    /// </summary>
    public static string? Parse(string? jwt)
    {
        if (string.IsNullOrWhiteSpace(jwt))
            return null;
        try
        {
            var token = new JwtSecurityTokenHandler().ReadJwtToken(jwt);
            var owners = token.Claims
                .Where(claim => claim.Type == AuthClaimTypes.UserProfileId)
                .ToArray();
            return token.ValidTo > DateTime.UtcNow && owners.Length == 1
                && !string.IsNullOrWhiteSpace(owners[0].Value)
                ? owners[0].Value : null;
        }
        catch (Exception ex) when (ex is ArgumentException or SecurityTokenException)
        {
            return null;
        }
    }
}

/// <summary>
/// The sync owner of a MAUI head: the learner the current sign-in token names, only while the
/// active profile is that same learner. Anything else (signed out, an expired or unreadable
/// token, a different active profile) is no owner, and sync sends nothing.
/// </summary>
public sealed class SignedInSyncOwnerSource(IAuthService auth, IPreferencesService preferences)
    : ISyncOwnerSource
{
    public async Task<SyncOwnerCredentials?> GetAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!auth.IsSignedIn)
            return null;
        // May refresh an expiring token; the owner is read from the token actually sent.
        var token = await auth.GetAccessTokenAsync([]);
        cancellationToken.ThrowIfCancellationRequested();
        var owner = JwtOwnerClaim.Parse(token);
        if (owner is null || !Guid.TryParse(owner, out var id) || id == Guid.Empty
            || !auth.IsSignedIn
            || !string.Equals(preferences.Get(UserProfileRepository.ActiveProfileIdKey, string.Empty),
                owner, StringComparison.Ordinal))
            return null;
        return new SyncOwnerCredentials(owner, token!);
    }
}

/// <summary>
/// Asks the API to re-send the signed-in learner's rows of newly synced tables
/// (POST /api/v1/sync/resend, <see cref="ISyncResendRequester"/>) through the sync HTTP client,
/// whose <see cref="SyncOwnerHeaderHandler"/> adds the verified bearer.
/// </summary>
public sealed class OwnerCheckedSyncResendRequester(
    IHttpClientFactory clients, ILogger<OwnerCheckedSyncResendRequester> logger)
    : ISyncResendRequester
{
    public async Task<bool> RequestAsync(string owner, IReadOnlyCollection<string> tables,
        CancellationToken cancellationToken)
    {
        if (tables.Count == 0)
            return true;
        if (string.IsNullOrWhiteSpace(owner))
            return false;
        using var request = new HttpRequestMessage(HttpMethod.Post, SyncResendQueue.Route)
        {
            Content = JsonContent.Create(new SyncResendRequest([.. tables]))
        };
        // The handler refuses to send if the signed-in learner is no longer this owner.
        request.Options.Set(SyncOwnerHeaderHandler.ExpectedOwner, owner);
        using var client = clients.CreateClient(ServiceCollectionExtentions.SyncHttpClientName);
        using var reply = await client.SendAsync(request, cancellationToken);
        if (!reply.IsSuccessStatusCode)
        {
            logger.LogWarning("Sync re-send request returned {Status}.", (int)reply.StatusCode);
            return false;
        }
        return true;
    }
}
