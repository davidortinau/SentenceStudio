using Microsoft.Extensions.Logging;
using SentenceStudio.Abstractions;
using SentenceStudio.Data;
using SentenceStudio.Shared.Models;

namespace SentenceStudio.WebUI.Services;

public enum UiSpeechClipStatus { Ready, Offline, Failed }

/// <summary>
/// One clip for playback. <see cref="Entry"/> is the owner's StreamHistory row
/// that now points at the clip (null when no row could be stored);
/// <see cref="Synthesized"/> is true when the clip was made just now.
/// </summary>
public sealed record UiSpeechClip(
    UiSpeechClipStatus Status,
    byte[]? Audio = null,
    StreamHistory? Entry = null,
    bool Synthesized = false);

/// <summary>
/// The owner's StreamHistory clip cache for the Razor activities (WebApp and
/// MAUI): a phrase in a voice is synthesized once and played from
/// AppData/AudioCache afterwards. All reads and writes go through the
/// owner-scoped <see cref="StreamHistoryRepository"/>.
/// <para>
/// A row's stored absolute path is resolved by its file name under today's
/// AudioCache directory: iOS moves the app container on reinstall, and a row
/// synced from another device names that device's path. Nothing outside the
/// AudioCache directory is read. When the owner's row exists but its clip is
/// not on this device, the clip is synthesized again in that row's voice and
/// kept under a name derived from the row ID
/// (<see cref="StreamHistory.RegeneratedClipFileName"/>); the shared row is not
/// changed and no second row is added (Captain 2026-11-02,
/// docs/sync-all-tables-plan.md D4; native NativeShadowingSpeech).
/// </para>
/// </summary>
public sealed class UiSpeechClipCache(
    StreamHistoryRepository history,
    IFileSystemService files,
    ILogger logger)
{
    public const string DirectoryName = "AudioCache";
    public const long MaxAudioBytes = 8 * 1024 * 1024;

    /// <summary>The cached clip's path under today's AudioCache, or null when absent, empty, too large or outside it.</summary>
    public static string? Resolve(string? appDataDirectory, string? storedPath, long maxBytes = MaxAudioBytes)
    {
        if (string.IsNullOrWhiteSpace(appDataDirectory) || string.IsNullOrWhiteSpace(storedPath))
            return null;
        try
        {
            var directory = Path.GetFullPath(Path.Combine(appDataDirectory, DirectoryName));
            var name = Path.GetFileName(storedPath);
            if (string.IsNullOrWhiteSpace(name) || name is "." or "..")
                return null;
            var path = Path.GetFullPath(Path.Combine(directory, name));
            if (!path.StartsWith(directory + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                || !File.Exists(path))
                return null;
            var length = new FileInfo(path).Length;
            return length > 0 && length <= maxBytes ? path : null;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException
            or UnauthorizedAccessException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>
    /// The owner's clip of <paramref name="phrase"/> in <paramref name="voiceId"/>:
    /// the cached clip when this device has it, else synthesized (when online)
    /// and cached. Offline when there is no cached clip and no connection;
    /// Failed when synthesis returns no audio.
    /// </summary>
    public async Task<UiSpeechClip> GetOrCreateAsync(
        string phrase,
        string voiceId,
        bool isOnline,
        string filePrefix,
        Func<string, CancellationToken, Task<Stream>> synthesize,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(phrase);
        ArgumentNullException.ThrowIfNull(synthesize);
        cancellationToken.ThrowIfCancellationRequested();

        var cached = await history.GetStreamHistoryByPhraseAndVoiceAsync(phrase, voiceId);
        if (cached is not null && await ReadEntryAsync(cached, cancellationToken) is { } audio)
            return new UiSpeechClip(UiSpeechClipStatus.Ready, audio, cached);

        return await SynthesizeAsync(phrase, voiceId, cached, isOnline, filePrefix, synthesize,
            cancellationToken);
    }

    /// <summary>
    /// The clip of one owned history <paramref name="entry"/>: its cached clip,
    /// else synthesized again in the entry's own voice (or
    /// <paramref name="fallbackVoiceId"/> when the entry has none) and the
    /// entry pointed at it.
    /// </summary>
    public async Task<UiSpeechClip> GetForEntryAsync(
        StreamHistory entry,
        string fallbackVoiceId,
        bool isOnline,
        string filePrefix,
        Func<string, CancellationToken, Task<Stream>> synthesize,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(synthesize);
        cancellationToken.ThrowIfCancellationRequested();

        if (await ReadEntryAsync(entry, cancellationToken) is { } audio)
            return new UiSpeechClip(UiSpeechClipStatus.Ready, audio, entry);
        if (string.IsNullOrWhiteSpace(entry.Phrase))
            return new UiSpeechClip(UiSpeechClipStatus.Failed);

        var voiceId = string.IsNullOrWhiteSpace(entry.VoiceId) ? fallbackVoiceId : entry.VoiceId;
        if (string.IsNullOrWhiteSpace(voiceId))
            return new UiSpeechClip(UiSpeechClipStatus.Failed);
        return await SynthesizeAsync(entry.Phrase, voiceId, entry, isOnline, filePrefix, synthesize,
            cancellationToken);
    }

    private async Task<UiSpeechClip> SynthesizeAsync(
        string phrase,
        string voiceId,
        StreamHistory? existing,
        bool isOnline,
        string filePrefix,
        Func<string, CancellationToken, Task<Stream>> synthesize,
        CancellationToken cancellationToken)
    {
        if (!isOnline)
            return new UiSpeechClip(UiSpeechClipStatus.Offline);

        byte[]? audio;
        try
        {
            await using var stream = await synthesize(voiceId, cancellationToken);
            audio = await ReadLimitedAsync(stream, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // The phrase is learner content; only the failure type is logged.
            logger.LogError("Speech synthesis failed: {Failure}.", ex.GetType().Name);
            return new UiSpeechClip(UiSpeechClipStatus.Failed);
        }

        // ElevenLabsSpeechService returns an empty stream when synthesis fails.
        if (audio is not { Length: > 0 })
        {
            logger.LogWarning("Speech synthesis returned no audio.");
            return new UiSpeechClip(UiSpeechClipStatus.Failed);
        }

        StreamHistory? stored = null;
        try
        {
            var directory = Path.Combine(files.AppDataDirectory, DirectoryName);
            Directory.CreateDirectory(directory);
            if (existing is not null)
            {
                // The owner's row exists but its clip is not on this device: keep this copy
                // under the row's own derived name and leave the shared row as it is.
                await File.WriteAllBytesAsync(
                    Path.Combine(directory, StreamHistory.RegeneratedClipFileName(existing.Id)),
                    audio, cancellationToken);
                return new UiSpeechClip(UiSpeechClipStatus.Ready, audio, existing, Synthesized: true);
            }

            var path = Path.Combine(directory, $"{filePrefix}_{Guid.NewGuid():N}.mp3");
            await File.WriteAllBytesAsync(path, audio, cancellationToken);

            var row = new StreamHistory
            {
                Phrase = phrase,
                VoiceId = voiceId,
                AudioFilePath = path,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            };
            if (await history.SaveStreamHistoryAsync(row) > 0)
            {
                stored = row;
            }
            else
            {
                // No owner, or the store refused: the clip plays once and leaves no file behind.
                logger.LogWarning("A synthesized clip could not be recorded in the owner's audio history.");
                DeleteUnrecordedClip(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The clip still plays without a cache entry.
            logger.LogWarning(ex, "A synthesized clip could not be cached.");
        }

        return new UiSpeechClip(UiSpeechClipStatus.Ready, audio, stored, Synthesized: true);
    }

    /// <summary>Removes the file this call just wrote when no row references it.</summary>
    private void DeleteUnrecordedClip(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "An unrecorded synthesized clip could not be removed.");
        }
    }

    /// <summary>The entry's own clip, else this client's regenerated copy of it.</summary>
    private async Task<byte[]?> ReadEntryAsync(StreamHistory entry, CancellationToken cancellationToken) =>
        await ReadCachedAsync(entry.AudioFilePath, cancellationToken)
            ?? (string.IsNullOrWhiteSpace(entry.Id) ? null
                : await ReadCachedAsync(StreamHistory.RegeneratedClipFileName(entry.Id), cancellationToken));

    private async Task<byte[]?> ReadCachedAsync(string? storedPath, CancellationToken cancellationToken)
    {
        if (Resolve(files.AppDataDirectory, storedPath) is not { } path)
            return null;
        try
        {
            await using var stream = File.OpenRead(path);
            return await ReadLimitedAsync(stream, cancellationToken) is { Length: > 0 } audio ? audio : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "A cached clip could not be read; synthesizing again.");
            return null;
        }
    }

    private static async Task<byte[]?> ReadLimitedAsync(Stream stream, CancellationToken cancellationToken)
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
