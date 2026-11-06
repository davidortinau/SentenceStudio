using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using SentenceStudio.Abstractions;
using SentenceStudio.Data;
using SentenceStudio.Native.Features.Import;
using SentenceStudio.Services;
using SentenceStudio.Shared.Models;

namespace SentenceStudio.Native.Adapters;

/// <summary>
/// Runs the source Media Import page's calls for one verified owner. The video's metadata and
/// captions come from the shared <see cref="IYouTubeImportService"/> (YoutubeExplode, on the
/// device as on MAUI); the cleanup and the AI polish from the shared
/// <see cref="TranscriptFormattingService"/>, whose AI goes through the owner-bound API gateway;
/// the resource is written by the shared <see cref="LearningResourceRepository"/> to the owner's
/// device database for that owner. Channels and the import history come from the API, which
/// scopes them to the signed-in learner. The owner must be the published device owner and the
/// profile pointer before each call, or nothing is read or written; reads are checked again after.
/// </summary>
public sealed class NativeYouTubeImport(
    IServiceProvider services,
    DeviceUserScopeProvider userScope,
    IPreferencesService preferences,
    NativePendingWrites pendingWrites,
    IYouTubeImportService youtube,
    TranscriptFormattingService formatting,
    ILogger<NativeYouTubeImport> logger,
    INativeYouTubeApi? api = null) : INativeYouTubeImport
{
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    public async Task<string?> GetTargetLanguageAsync(string owner, CancellationToken cancellationToken)
    {
        RequireOwner(owner);
        using var scope = services.CreateScope();
        var profile = await scope.ServiceProvider.GetRequiredService<UserProfileRepository>()
            .GetByIdAsync(owner, cancellationToken);
        RequireOwner(owner);
        return profile?.TargetLanguage;
    }

    public async Task<string?> GetVideoTitleAsync(
        string owner, string videoUrl, CancellationToken cancellationToken)
    {
        RequireOwner(owner);
        var video = await youtube.GetVideoMetadataAsync(videoUrl);
        cancellationToken.ThrowIfCancellationRequested();
        RequireOwner(owner);
        return video?.Title;
    }

    public async Task<IReadOnlyList<TranscriptTrack>> GetTracksAsync(
        string owner, string videoUrl, CancellationToken cancellationToken)
    {
        RequireOwner(owner);
        var tracks = await youtube.GetAvailableTranscriptsAsync(videoUrl);
        cancellationToken.ThrowIfCancellationRequested();
        RequireOwner(owner);
        return tracks ?? [];
    }

    public async Task<string> DownloadTranscriptAsync(
        string owner, TranscriptTrack track, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(track);
        RequireOwner(owner);
        var raw = await youtube.DownloadTranscriptTextAsync(track);
        cancellationToken.ThrowIfCancellationRequested();
        RequireOwner(owner);
        return formatting.SmartCleanup(raw, track.LanguageCode);
    }

    public async Task<string> PolishAsync(
        string owner, string transcript, string? languageCode, CancellationToken cancellationToken)
    {
        RequireOwner(owner);
        var polished = await formatting.PolishWithAiAsync(transcript, languageCode);
        cancellationToken.ThrowIfCancellationRequested();
        RequireOwner(owner);
        return polished;
    }

    public async Task<NativeYouTubeSaved> SaveResourceAsync(
        string owner, NativeYouTubeResourceDraft draft, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(draft);
        RequireOwner(owner);
        var stamp = userScope.CaptureStamp();
        if (stamp.PublishedOwner != owner)
            throw new UnauthorizedAccessException("YouTube import requires the published device owner.");
        var resource = new LearningResource
        {
            Title = draft.Title,
            MediaUrl = draft.MediaUrl,
            Transcript = draft.Transcript,
            MediaType = "Video",
            Language = draft.Language
        };
        using var scope = services.CreateScope();
        // The owner is passed explicitly: the repository writes only for it (empty refuses).
        var id = await scope.ServiceProvider.GetRequiredService<LearningResourceRepository>()
            .SaveResourceAsync(resource, owner);
        if (string.IsNullOrEmpty(id))
            throw new InvalidOperationException("Save returned no valid resource ID");
        // The row is committed on this device; record it for sync like every native write.
        try
        {
            pendingWrites.MarkPending(owner);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "YouTube import saved but sync tracking was not confirmed.");
        }
        if (IsActiveOwner(owner))
            return new NativeYouTubeSaved(id);
        logger.LogWarning("YouTube import saved, but the device owner changed: no result to show.");
        return new NativeYouTubeSaved(null);
    }

    public Task<IReadOnlyList<MonitoredChannel>> ListChannelsAsync(
        string owner, CancellationToken cancellationToken) =>
        CallAsync<IReadOnlyList<MonitoredChannel>>(owner, async (client, token) =>
            await client.GetFromJsonAsync<List<MonitoredChannel>>("api/channels", Json, token) ?? [],
            cancellationToken);

    public Task UpdateChannelAsync(string owner, MonitoredChannel channel,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(channel);
        return CallAsync(owner, async (client, token) =>
        {
            using var response = await client.PutAsJsonAsync(
                $"api/channels/{Uri.EscapeDataString(channel.Id)}", channel, Json, token);
            await EnsureSuccessAsync(response, token);
            return true;
        }, cancellationToken);
    }

    public Task<IReadOnlyList<NativeYouTubeImportRecord>> ListImportsAsync(
        string owner, CancellationToken cancellationToken) =>
        CallAsync<IReadOnlyList<NativeYouTubeImportRecord>>(owner, async (client, token) =>
            await client.GetFromJsonAsync<List<NativeYouTubeImportRecord>>("api/imports", Json, token)
                ?? [],
            cancellationToken);

    public Task RetryImportAsync(string owner, string importId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(importId);
        return CallAsync(owner, async (client, token) =>
        {
            using var response = await client.PostAsync(
                $"api/imports/{Uri.EscapeDataString(importId)}/retry", content: null, token);
            await EnsureSuccessAsync(response, token);
            return true;
        }, cancellationToken);
    }

    private async Task<T> CallAsync<T>(string owner,
        Func<HttpClient, CancellationToken, Task<T>> call, CancellationToken cancellationToken)
    {
        RequireOwner(owner);
        if (api is null)
            throw new InvalidOperationException("The native API is not configured.");
        var result = await api.CallAsync(owner, call, cancellationToken);
        RequireOwner(owner);
        return result;
    }

    /// <summary>A refused API call says why (the server's text), like the source's exception message.</summary>
    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken token)
    {
        if (response.IsSuccessStatusCode)
            return;
        var detail = (await response.Content.ReadAsStringAsync(token)).Trim().Trim('"');
        throw new HttpRequestException(
            string.IsNullOrWhiteSpace(detail) ? response.ReasonPhrase : detail,
            inner: null, response.StatusCode);
    }

    private void RequireOwner(string owner)
    {
        if (!IsActiveOwner(owner))
        {
            logger.LogWarning("YouTube import refused an absent or changed owner.");
            throw new UnauthorizedAccessException("YouTube import requires the current device owner.");
        }
    }

    private bool IsActiveOwner(string owner) =>
        !string.IsNullOrWhiteSpace(owner)
        && userScope.TryGetUserProfileId(out var active)
        && string.Equals(active, owner, StringComparison.Ordinal)
        && string.Equals(
            preferences.Get(UserProfileRepository.ActiveProfileIdKey, string.Empty),
            owner, StringComparison.Ordinal);
}

/// <summary>An API call as one owner (the owner-bound bearer client).</summary>
public interface INativeYouTubeApi
{
    Task<T> CallAsync<T>(string owner, Func<HttpClient, CancellationToken, Task<T>> call,
        CancellationToken cancellationToken);
}

/// <summary>
/// The API as one owner, like the Sam client: a per-call client whose bearer handler re-verifies,
/// for every request, that the device scope, the profile pointer, the signed-in state and the
/// token's owner claim all name the owner the caller asked for, and that the request goes to the
/// configured API origin. A result that arrives after the owner changed is discarded by throwing.
/// </summary>
public sealed class OwnerBoundNativeYouTubeApi(
    IAuthService auth,
    IPreferencesService preferences,
    DeviceUserScopeProvider scope,
    IHttpMessageHandlerFactory handlers,
    Uri apiRoot) : INativeYouTubeApi
{
    internal const string ClientName = "NativeOwnerYouTubeClient";

    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);

    public async Task<T> CallAsync<T>(string owner,
        Func<HttpClient, CancellationToken, Task<T>> call, CancellationToken cancellationToken)
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
                "YouTube import requests require a current authenticated owner.");

        var guard = new OwnerBoundNativeAiBearerHandler(auth, apiRoot, owner, HasOwner)
        {
            InnerHandler = handlers.CreateHandler(ClientName)
        };
        using var client = new HttpClient(guard, disposeHandler: false)
        {
            BaseAddress = apiRoot,
            Timeout = RequestTimeout
        };
        var result = await call(client, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (!HasOwner())
            throw new UnauthorizedAccessException(
                "YouTube import owner changed before the result could be applied.");
        return result;
    }
}

public static class NativeYouTubeImportRegistration
{
    /// <summary>The API side of YouTube import (channels, history) for a head with an API origin.</summary>
    public static IServiceCollection AddOwnerBoundNativeYouTubeApi(
        this IServiceCollection services, Uri apiRoot)
    {
        ArgumentNullException.ThrowIfNull(services);
        if (apiRoot is not { IsAbsoluteUri: true }
            || apiRoot.Scheme != Uri.UriSchemeHttp
                && apiRoot.Scheme != Uri.UriSchemeHttps)
            throw new ArgumentException("A valid native API origin is required.", nameof(apiRoot));
        services.AddHttpClient(OwnerBoundNativeYouTubeApi.ClientName)
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
            {
                AllowAutoRedirect = false
            });
        services.TryAddSingleton<INativeYouTubeApi>(provider =>
            new OwnerBoundNativeYouTubeApi(
                provider.GetRequiredService<IAuthService>(),
                provider.GetRequiredService<IPreferencesService>(),
                provider.GetRequiredService<DeviceUserScopeProvider>(),
                provider.GetRequiredService<IHttpMessageHandlerFactory>(),
                apiRoot));
        return services;
    }
}
