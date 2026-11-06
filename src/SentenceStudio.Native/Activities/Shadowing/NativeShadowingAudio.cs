using Microsoft.Extensions.Logging;
using SentenceStudio.Abstractions;
using SentenceStudio.Data;
using SentenceStudio.Native.Adapters;
using SentenceStudio.Services;
using SentenceStudio.Shared.Models;

namespace SentenceStudio.Native;

/// <summary>
/// Non-UI platform audio player for one prepared clip (AVAudioPlayer on iOS,
/// MediaPlayer on Android). Rate is applied at playback, preserving pitch.
/// Implementations raise <see cref="Ended"/> when a clip plays to its end.
/// </summary>
public interface INativeShadowingPlayer : IDisposable
{
    /// <summary>Replaces any loaded clip. False when the bytes cannot be decoded.</summary>
    bool Load(byte[] audio);
    bool Play(float rate);
    void Pause();
    /// <summary>Seeks to a fraction 0..1 of the duration.</summary>
    void Seek(double fraction);
    /// <summary>Stops playback and releases the loaded clip.</summary>
    void Stop();
    bool IsLoaded { get; }
    bool IsPlaying { get; }
    double DurationSeconds { get; }
    double PositionSeconds { get; }
    event Action? Ended;
}

/// <summary>Default when a head has no player: every sentence reports failed audio.</summary>
internal sealed class UnavailableNativeShadowingPlayer : INativeShadowingPlayer
{
    public bool Load(byte[] audio) => false;
    public bool Play(float rate) => false;
    public void Pause() { }
    public void Seek(double fraction) { }
    public void Stop() { }
    public bool IsLoaded => false;
    public bool IsPlaying => false;
    public double DurationSeconds => 0;
    public double PositionSeconds => 0;
    public event Action? Ended { add { } remove { } }
    public void Dispose() { }
}

public enum NativeSpeechClipStatus { Ready, Offline, Failed }

public sealed record NativeSpeechClip(NativeSpeechClipStatus Status, byte[]? Audio = null);

public interface INativeShadowingSpeech
{
    /// <summary>
    /// Target-language audio for <paramref name="text"/> with the owner's voice for
    /// <paramref name="language"/>: the owner's cached clip first, else API synthesis
    /// (cached for next time). Offline when uncached and the server is unreachable.
    /// </summary>
    Task<NativeSpeechClip> GetAsync(
        string owner, string text, string language, CancellationToken cancellationToken);

    /// <summary>
    /// The same, for a specific stored voice (a history entry's own voice). A synced history
    /// entry whose clip is not on this device is synthesized again and the entry points at the
    /// new clip (Captain 2026-11-02, docs/sync-all-tables-plan.md D4).
    /// </summary>
    Task<NativeSpeechClip> GetForVoiceAsync(
        string owner, string text, string language, string? voiceId,
        CancellationToken cancellationToken) =>
        GetAsync(owner, text, language, cancellationToken);
}

/// <summary>
/// The native TTS path used by the quiz (activities.md 1.6): owner-scoped
/// StreamHistory cache under AppData/AudioCache, then the owner-bound
/// /api/v1/speech/synthesize call. Synthesized audio is cached per phrase and
/// voice; speed is not part of the cache key because it is applied at playback.
/// </summary>
public sealed class NativeShadowingSpeech(
    NativeActivityOwner owner,
    IConnectivityService connectivity,
    IFileSystemService files,
    SpeechVoicePreferences voices,
    StreamHistoryRepository history,
    ILogger<NativeShadowingSpeech> logger,
    INativeQuizSpeechSource? speech = null,
    IAuthService? auth = null) : INativeShadowingSpeech
{
    private const int MaxAudioBytes = 8 * 1024 * 1024;

    public Task<NativeSpeechClip> GetAsync(
        string userId, string text, string language, CancellationToken cancellationToken) =>
        GetCoreAsync(userId, text, voices.GetVoiceForLanguage(language), cancellationToken);

    public Task<NativeSpeechClip> GetForVoiceAsync(
        string userId, string text, string language, string? voiceId,
        CancellationToken cancellationToken) =>
        GetCoreAsync(userId, text,
            string.IsNullOrWhiteSpace(voiceId) ? voices.GetVoiceForLanguage(language) : voiceId,
            cancellationToken);

    private async Task<NativeSpeechClip> GetCoreAsync(
        string userId, string text, string voice, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(text) || !owner.IsActive(userId))
            return new(NativeSpeechClipStatus.Failed);
        var cached = await history.GetStreamHistoryByPhraseAndVoiceAsync(text, voice);
        if (!owner.IsActive(userId))
            return new(NativeSpeechClipStatus.Failed);
        if (cached?.UserProfileId == userId
            && (CachedAudioPath(cached.AudioFilePath)
                ?? CachedAudioPath(StreamHistory.RegeneratedClipFileName(cached.Id))) is { } cachedPath)
        {
            try
            {
                await using var stream = File.OpenRead(cachedPath);
                if (await ReadLimitedAsync(stream, cancellationToken) is { Length: > 0 } bytes)
                    return new(NativeSpeechClipStatus.Ready, bytes);
            }
            catch (IOException ex)
            {
                logger.LogWarning(ex, "Shadowing could not read a cached clip; synthesizing again.");
            }
        }

        if (!connectivity.IsInternetAvailable || speech is null || auth?.IsSignedIn != true)
            return new(NativeSpeechClipStatus.Offline);
        byte[]? audio;
        bool mustNotKeep;
        try
        {
            using var stream = await speech.SynthesizeAsync(userId, text, voice, cancellationToken);
            audio = stream is null ? null : await ReadLimitedAsync(stream, cancellationToken);
            mustNotKeep = SentenceStudio.Services.Api.SpeechClipStream.MustNotKeep(stream);
        }
        catch (HttpRequestException ex) when (ex.StatusCode is null)
        {
            logger.LogWarning("Shadowing speech server was unreachable.");
            return new(NativeSpeechClipStatus.Offline);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("Shadowing speech synthesis timed out.");
            return new(NativeSpeechClipStatus.Offline);
        }
        catch (Exception ex) when (ex is HttpRequestException or UnauthorizedAccessException
            or InvalidOperationException or System.Text.Json.JsonException)
        {
            // The synthesized text is learner content; only the failure type is logged.
            logger.LogError("Shadowing speech synthesis failed: {Failure}.", ex.GetType().Name);
            return new(NativeSpeechClipStatus.Failed);
        }
        if (audio is not { Length: > 0 } || !owner.IsActive(userId))
            return new(NativeSpeechClipStatus.Failed);
        if (mustNotKeep)
        {
            // The API did not confirm this voice (its default voice, or an older server that does
            // not say): play it, but keep no file or entry that would later serve it as this voice.
            logger.LogWarning("Shadowing played a clip not confirmed in the requested voice without caching it.");
            return new(NativeSpeechClipStatus.Ready, audio);
        }

        // The cache write belongs to the caller's operation: once that is cancelled (Back, a
        // closed page, a disposed presenter) nothing is written, and once started it is not cut
        // short, so no partial clip and no clip without its history entry is left behind.
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var directory = Path.Combine(files.AppDataDirectory, "AudioCache");
            Directory.CreateDirectory(directory);
            if (cached?.UserProfileId == userId)
            {
                // The owner's entry exists but its clip is not on this device (another device
                // made it): keep this copy under the entry's derived name and leave the synced
                // entry unchanged, so devices do not keep sending each other to synthesize again.
                await File.WriteAllBytesAsync(
                    Path.Combine(directory, StreamHistory.RegeneratedClipFileName(cached.Id)),
                    audio, CancellationToken.None);
                return new(NativeSpeechClipStatus.Ready, audio);
            }
            var path = Path.Combine(directory, $"shadow_{Guid.NewGuid():N}.mp3");
            await File.WriteAllBytesAsync(path, audio, CancellationToken.None);
            if (owner.IsActive(userId))
                await history.SaveStreamHistoryAsync(new StreamHistory
                    {
                        UserProfileId = userId,
                        Phrase = text,
                        VoiceId = voice,
                        AudioFilePath = path,
                        CreatedAt = DateTime.UtcNow,
                        UpdatedAt = DateTime.UtcNow
                    });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The clip still plays without a cache entry.
            logger.LogWarning(ex, "Shadowing could not cache a synthesized clip.");
        }
        return new(NativeSpeechClipStatus.Ready, audio);
    }

    /// <summary>
    /// The cached clip inside the current AudioCache directory. iOS moves the app
    /// sandbox between installs, so a stored absolute path is resolved by its file
    /// name under today's AppData directory; nothing outside that directory is read.
    /// </summary>
    private string? CachedAudioPath(string? stored)
    {
        if (string.IsNullOrWhiteSpace(stored))
            return null;
        try
        {
            var directory = Path.GetFullPath(Path.Combine(files.AppDataDirectory, "AudioCache"));
            var name = Path.GetFileName(stored);
            if (string.IsNullOrWhiteSpace(name))
                return null;
            var path = Path.GetFullPath(Path.Combine(directory, name));
            return path.StartsWith(directory + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                && File.Exists(path)
                && new FileInfo(path).Length is > 0 and <= MaxAudioBytes
                    ? path : null;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException
            or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static async Task<byte[]?> ReadLimitedAsync(
        Stream stream, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[32 * 1024];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken)) > 0)
        {
            if (buffer.Length + read > MaxAudioBytes)
                return null;
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }
}
