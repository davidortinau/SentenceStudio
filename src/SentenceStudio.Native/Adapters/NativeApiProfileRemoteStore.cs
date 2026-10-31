using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.Extensions.Logging;
using SentenceStudio.Services;

namespace SentenceStudio.Native.Adapters;

/// <summary>
/// Native clients download UserProfile but never upload it, so an onboarding
/// choice saved only to SQLite would be lost on the next profile download.
/// This writes the choice through the established owner-checked
/// PUT /api/v1/profile/{id} path first; the local row is updated only after
/// the server accepted it.
/// </summary>
public sealed class NativeApiProfileRemoteStore(
    IHttpClientFactory clients,
    IAuthService auth,
    ILogger<NativeApiProfileRemoteStore> logger) : INativeProfileRemoteStore
{
    public async Task<NativeProfileRemoteOutcome> SaveOnboardingAsync(
        string owner, NativeOnboardingDraft draft, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(owner) || draft is null)
            return NativeProfileRemoteOutcome.Rejected;
        var token = await auth.GetAccessTokenAsync([]);
        cancellationToken.ThrowIfCancellationRequested();
        if (!auth.IsSignedIn
            || !string.Equals(NativeAuthPresenter.ParseOwnerClaim(token), owner,
                StringComparison.Ordinal))
        {
            logger.LogWarning("Onboarding profile write refused an absent or mismatched token owner.");
            return NativeProfileRemoteOutcome.Rejected;
        }

        var client = clients.CreateClient("AuthClient");
        var path = $"/api/v1/profile/{Uri.EscapeDataString(owner)}";
        try
        {
            // PUT replaces Email and the OpenAI key, so the current server
            // values are read first and sent back unchanged.
            using var read = new HttpRequestMessage(HttpMethod.Get, path);
            read.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var current = await client.SendAsync(read, cancellationToken);
            if (!current.IsSuccessStatusCode)
            {
                logger.LogWarning("Onboarding profile read returned {Status}.", current.StatusCode);
                return NativeProfileRemoteOutcome.Rejected;
            }
            // Known race: the OpenAI/ElevenLabs keys are read here and written back
            // by the PUT below; a key changed on another client between the two
            // calls would be overwritten. The API has no partial update.
            var existing = await current.Content.ReadFromJsonAsync<RemoteProfile>(
                cancellationToken);
            if (existing is null || !string.Equals(existing.Id, owner, StringComparison.Ordinal))
            {
                logger.LogWarning("Onboarding profile read did not echo the signed-in owner.");
                return NativeProfileRemoteOutcome.Rejected;
            }

            using var write = new HttpRequestMessage(HttpMethod.Put, path)
            {
                Content = JsonContent.Create(new
                {
                    DisplayName = draft.Name.Trim(),
                    Email = string.IsNullOrWhiteSpace(existing.Email)
                        ? auth.UserName : existing.Email,
                    draft.NativeLanguage,
                    draft.TargetLanguage,
                    DisplayLanguage = "English",
                    TargetCefrLevel = draft.TargetCefrLevel,
                    draft.PreferredSessionMinutes,
                    existing.OpenAiApiKey,
                    existing.ElevenLabsApiKey
                })
            };
            write.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var saved = await client.SendAsync(write, cancellationToken);
            if (saved.IsSuccessStatusCode)
                return NativeProfileRemoteOutcome.Saved;
            logger.LogWarning("Onboarding profile write returned {Status}.", saved.StatusCode);
            return saved.StatusCode is HttpStatusCode.ServiceUnavailable
                or HttpStatusCode.GatewayTimeout or HttpStatusCode.BadGateway
                ? NativeProfileRemoteOutcome.TransportFailure
                : NativeProfileRemoteOutcome.Rejected;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (System.Text.Json.JsonException ex)
        {
            logger.LogWarning("Onboarding profile read returned an unreadable body ({FailureType}).",
                ex.GetType().Name);
            return NativeProfileRemoteOutcome.Rejected;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            logger.LogWarning("Onboarding profile write could not reach the API ({FailureType}).",
                ex.GetType().Name);
            return NativeProfileRemoteOutcome.TransportFailure;
        }
    }

    private sealed record RemoteProfile(
        string Id, string? Email, string? OpenAiApiKey, string? ElevenLabsApiKey);
}
