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

    public async Task<NativeSpeechClip> GetAsync(
        string userId, string text, string language, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(text) || !owner.IsActive(userId))
            return new(NativeSpeechClipStatus.Failed);
        var voice = voices.GetVoiceForLanguage(language);
        var cached = await history.GetStreamHistoryByPhraseAndVoiceAsync(text, voice);
        if (!owner.IsActive(userId))
            return new(NativeSpeechClipStatus.Failed);
        if (cached?.UserProfileId == userId && IsCachedAudio(cached.AudioFilePath))
        {
            try
            {
                await using var stream = File.OpenRead(cached.AudioFilePath!);
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
        try
        {
            using var stream = await speech.SynthesizeAsync(userId, text, voice, cancellationToken);
            audio = stream is null ? null : await ReadLimitedAsync(stream, cancellationToken);
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

        try
        {
            var directory = Path.Combine(files.AppDataDirectory, "AudioCache");
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, $"shadow_{Guid.NewGuid():N}.mp3");
            await File.WriteAllBytesAsync(path, audio, cancellationToken);
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

    private bool IsCachedAudio(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;
        var directory = Path.GetFullPath(
            Path.Combine(files.AppDataDirectory, "AudioCache")) + Path.DirectorySeparatorChar;
        try
        {
            return Path.GetFullPath(path).StartsWith(directory, StringComparison.Ordinal)
                && File.Exists(path)
                && new FileInfo(path).Length is > 0 and <= MaxAudioBytes;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException
            or UnauthorizedAccessException)
        {
            return false;
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
