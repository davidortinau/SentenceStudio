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
        // ReadFromJsonAsync throws NotSupportedException for a non-JSON content type.
        catch (Exception ex) when (ex is System.Text.Json.JsonException or NotSupportedException)
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

    /// <summary>
    /// Saves the learner's own editable fields through PUT /api/v1/profile/me. The
    /// server takes the owner from the token, so the call is refused here unless the
    /// token's owner is the expected owner. Email and API keys are not sent.
    /// </summary>
    public async Task<NativeProfileSaveResult> SaveProfileAsync(
        string owner, NativeProfileFields fields, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(owner) || fields is null)
            return new(NativeProfileRemoteOutcome.Rejected);
        var token = await auth.GetAccessTokenAsync([]);
        cancellationToken.ThrowIfCancellationRequested();
        if (!auth.IsSignedIn
            || !string.Equals(NativeAuthPresenter.ParseOwnerClaim(token), owner,
                StringComparison.Ordinal))
        {
            logger.LogWarning("Profile write refused an absent or mismatched token owner.");
            return new(NativeProfileRemoteOutcome.Rejected);
        }

        var client = clients.CreateClient("AuthClient");
        try
        {
            using var write = new HttpRequestMessage(HttpMethod.Put, "/api/v1/profile/me")
            {
                Content = JsonContent.Create(new
                {
                    DisplayName = fields.Name,
                    fields.NativeLanguage,
                    fields.TargetLanguage,
                    fields.DisplayLanguage,
                    fields.PreferredSessionMinutes,
                    TargetCefrLevel = fields.TargetCefrLevel
                })
            };
            write.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var response = await client.SendAsync(write, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("Profile write returned {Status}.", response.StatusCode);
                return new(response.StatusCode is HttpStatusCode.ServiceUnavailable
                    or HttpStatusCode.GatewayTimeout or HttpStatusCode.BadGateway
                    ? NativeProfileRemoteOutcome.TransportFailure
                    : NativeProfileRemoteOutcome.Rejected);
            }
            var saved = await response.Content.ReadFromJsonAsync<SavedProfile>(cancellationToken);
            if (saved is null || !string.Equals(saved.Id, owner, StringComparison.Ordinal)
                || string.IsNullOrWhiteSpace(saved.DisplayName)
                || string.IsNullOrWhiteSpace(saved.NativeLanguage)
                || string.IsNullOrWhiteSpace(saved.TargetLanguage)
                || string.IsNullOrWhiteSpace(saved.DisplayLanguage)
                || string.IsNullOrWhiteSpace(saved.TargetCefrLevel)
                || saved.PreferredSessionMinutes <= 0)
            {
                logger.LogWarning("Profile write response did not echo the signed-in owner's fields.");
                return new(NativeProfileRemoteOutcome.Rejected);
            }
            return new(NativeProfileRemoteOutcome.Saved,
                new NativeProfileFields(saved.DisplayName, saved.NativeLanguage,
                    saved.TargetLanguage, saved.DisplayLanguage,
                    saved.PreferredSessionMinutes, saved.TargetCefrLevel),
                saved.Email);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or NotSupportedException)
        {
            logger.LogWarning("Profile write returned an unreadable body ({FailureType}).",
                ex.GetType().Name);
            return new(NativeProfileRemoteOutcome.Rejected);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            logger.LogWarning("Profile write could not reach the API ({FailureType}).",
                ex.GetType().Name);
            return new(NativeProfileRemoteOutcome.TransportFailure);
        }
    }

    private sealed record RemoteProfile(
        string Id, string? Email, string? OpenAiApiKey, string? ElevenLabsApiKey);

    private sealed record SavedProfile(
        string Id, string? DisplayName, string? Email, string? NativeLanguage,
        string? TargetLanguage, string? DisplayLanguage, string? TargetCefrLevel,
        int PreferredSessionMinutes);
}
