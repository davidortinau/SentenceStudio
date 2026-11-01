using System.Net.Http.Json;
using System.Text.Json;
using ElevenLabs;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using SentenceStudio.Abstractions;
using SentenceStudio.Contracts.Speech;
using SentenceStudio.Data;
using SentenceStudio.Models;
using SentenceStudio.Services;
using SentenceStudio.Shared.Models;

namespace SentenceStudio.Native.Adapters;

/// <summary>
/// Source ElevenLabsSpeechService.GenerateTimestampedAudioAsync through the
/// owner-bound API (POST /api/v1/speech/synthesize-timestamped, which itself
/// scopes the resource by the caller). The source default voice "jiyoung",
/// stability 0.5, similarity 0.75 and speed 1.0 are kept, and so is its
/// AppData cache key (timestamped_{resource}_{voice}_{speed:F1}.mp3/.json).
/// </summary>
public sealed class NativeReadingAudioSource(
    IAuthService auth,
    IPreferencesService preferences,
    DeviceUserScopeProvider scope,
    IHttpMessageHandlerFactory handlers,
    IFileSystemService files,
    ILogger<NativeReadingAudioSource> logger,
    Uri apiRoot,
    NativeServerReachability? reachability = null) : INativeReadingAudioSource
{
    public const string SourceVoice = "jiyoung";
    private const int MaxAudioBytes = 32 * 1024 * 1024;

    /// <summary>The server allows up to five minutes for a long transcript.</summary>
    public static TimeSpan Timeout { get; } = TimeSpan.FromMinutes(5);

    public async Task<TimestampedAudio?> GetAsync(
        string owner, LearningResource resource, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(resource);
        cancellationToken.ThrowIfCancellationRequested();
        var stamp = scope.CaptureStamp();
        bool HasOwner() =>
            !string.IsNullOrWhiteSpace(owner)
            && stamp.PublishedOwner == owner && scope.IsCurrent(stamp)
            && scope.TryGetUserProfileId(out var active) && active == owner
            && preferences.Get(UserProfileRepository.ActiveProfileIdKey, string.Empty) == owner
            && auth.IsSignedIn;
        if (!HasOwner() || resource.UserProfileId != owner
            || string.IsNullOrEmpty(resource.Transcript) || string.IsNullOrWhiteSpace(resource.Id))
            throw new UnauthorizedAccessException("Reading audio needs the resource's active owner.");

        var key = $"timestamped_{resource.Id}_{SourceVoice}_{1.0f:F1}";
        var audioPath = Path.Combine(files.AppDataDirectory, key + ".mp3");
        var metaPath = Path.Combine(files.AppDataDirectory, key + ".json");
        if (await ReadCacheAsync(audioPath, metaPath, resource.Transcript, cancellationToken) is { } cached)
            return cached;

        var bearer = new OwnerBoundNativeAiBearerHandler(auth, apiRoot, owner, HasOwner)
        {
            InnerHandler = handlers.CreateHandler(OwnerBoundNativeAiGateway.ClientName)
        };
        using var client = new HttpClient(bearer, disposeHandler: false)
        {
            BaseAddress = apiRoot,
            Timeout = Timeout
        };
        SynthesizeTimestampedResponse? response;
        try
        {
            using var reply = await client.PostAsJsonAsync("/api/v1/speech/synthesize-timestamped",
                new SynthesizeTimestampedRequest
                {
                    ResourceId = resource.Id,
                    VoiceId = SourceVoice,
                    Stability = 0.5f,
                    SimilarityBoost = 0.75f,
                    Speed = 1.0f
                }, cancellationToken);
            reachability?.RecordResponse();
            if (!reply.IsSuccessStatusCode)
            {
                logger.LogWarning("Timestamped speech returned {Status}.", (int)reply.StatusCode);
                return null;
            }
            response = await reply.Content.ReadFromJsonAsync<SynthesizeTimestampedResponse>(cancellationToken);
        }
        catch (HttpRequestException ex) when (ex.StatusCode is null)
        {
            reachability?.RecordFailure();
            throw;
        }
        if (!HasOwner())
            throw new UnauthorizedAccessException("Reading audio owner changed during synthesis.");
        if (response is null || !TryDecode(response.AudioUrl, out var bytes))
            return null;

        var characters = ToCharacters(response.Characters
            .Select(item => (item.Char, item.StartMs / 1000.0, item.EndMs / 1000.0)));
        var audio = new TimestampedAudio
        {
            AudioData = bytes,
            Characters = characters,
            FullTranscript = resource.Transcript,
            Duration = response.DurationSeconds
        };
        await WriteCacheAsync(audioPath, metaPath, audio, cancellationToken);
        return audio;
    }

    internal static bool TryDecode(string? dataUri, out byte[] bytes)
    {
        bytes = [];
        const string prefix = "base64,";
        var start = dataUri?.IndexOf(prefix, StringComparison.Ordinal) ?? -1;
        if (dataUri is null || !dataUri.StartsWith("data:audio/", StringComparison.Ordinal) || start < 0)
            return false;
        try
        {
            bytes = Convert.FromBase64String(dataUri[(start + prefix.Length)..]);
            return bytes.Length is > 0 and <= MaxAudioBytes;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    /// <summary>ElevenLabs characters have JSON-only setters; build them through their own contract.</summary>
    internal static TimestampedTranscriptCharacter[] ToCharacters(
        IEnumerable<(string Character, double Start, double End)> items)
    {
        var json = JsonSerializer.Serialize(items.Select(item => new Dictionary<string, object>
        {
            ["character"] = item.Character,
            ["character_start_times_seconds"] = item.Start,
            ["character_end_times_seconds"] = item.End
        }));
        return JsonSerializer.Deserialize<TimestampedTranscriptCharacter[]>(json) ?? [];
    }

    private async Task<TimestampedAudio?> ReadCacheAsync(
        string audioPath, string metaPath, string transcript, CancellationToken cancellationToken)
    {
        try
        {
            if (!File.Exists(audioPath) || !File.Exists(metaPath))
                return null;
            var meta = JsonSerializer.Deserialize<CacheMeta>(
                await File.ReadAllTextAsync(metaPath, cancellationToken));
            var bytes = await File.ReadAllBytesAsync(audioPath, cancellationToken);
            // A transcript edit invalidates the cached timings.
            if (meta is null || meta.Transcript != transcript || bytes.Length == 0)
                return null;
            return new TimestampedAudio
            {
                AudioData = bytes,
                Characters = meta.Characters ?? [],
                FullTranscript = transcript,
                Duration = meta.Duration
            };
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Cached reading audio could not be read; generating again.");
            return null;
        }
    }

    private async Task WriteCacheAsync(
        string audioPath, string metaPath, TimestampedAudio audio, CancellationToken cancellationToken)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(audioPath)!);
            await File.WriteAllBytesAsync(audioPath, audio.AudioData, cancellationToken);
            await File.WriteAllTextAsync(metaPath, JsonSerializer.Serialize(new CacheMeta
            {
                Characters = audio.Characters,
                Duration = audio.Duration,
                Transcript = audio.FullTranscript
            }), cancellationToken);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Source: continue without caching.
            logger.LogWarning(ex, "Reading audio could not be cached.");
        }
    }

    private sealed class CacheMeta
    {
        public TimestampedTranscriptCharacter[]? Characters { get; set; }
        public double Duration { get; set; }
        public string Transcript { get; set; } = string.Empty;
    }
}

public static class NativeReadingAudioRegistration
{
    public static IServiceCollection AddOwnerBoundNativeReadingAudio(
        this IServiceCollection services, Uri apiRoot)
    {
        ArgumentNullException.ThrowIfNull(services);
        if (!apiRoot.IsAbsoluteUri || apiRoot.Scheme is not ("http" or "https"))
            throw new ArgumentException("Reading audio needs the configured API origin.", nameof(apiRoot));
        services.Replace(ServiceDescriptor.Singleton<INativeReadingAudioSource>(sp =>
            new NativeReadingAudioSource(
                sp.GetRequiredService<IAuthService>(),
                sp.GetRequiredService<IPreferencesService>(),
                sp.GetRequiredService<DeviceUserScopeProvider>(),
                sp.GetRequiredService<IHttpMessageHandlerFactory>(),
                sp.GetRequiredService<IFileSystemService>(),
                sp.GetRequiredService<ILogger<NativeReadingAudioSource>>(),
                apiRoot,
                sp.GetService<NativeServerReachability>())));
        return services;
    }
}
