using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using SentenceStudio.Abstractions;

namespace SentenceStudio.Services.Numbers;

/// <summary>
/// Minimal interface for TTS services consumed by NumberAudioCache.
/// Allows testing without coupling to ElevenLabsSpeechService.
/// </summary>
public interface INumberTtsService
{
    Task<Stream> TextToSpeechAsync(string text, string voiceId, float speed, CancellationToken ct = default);
}

/// <summary>
/// Adapter for ElevenLabsSpeechService to implement INumberTtsService.
/// </summary>
public class ElevenLabsNumberTtsAdapter : INumberTtsService
{
    private readonly ElevenLabsSpeechService _speechService;

    public ElevenLabsNumberTtsAdapter(ElevenLabsSpeechService speechService)
    {
        _speechService = speechService;
    }

    public Task<Stream> TextToSpeechAsync(string text, string voiceId, float speed, CancellationToken ct = default)
    {
        return _speechService.TextToSpeechAsync(text, voiceId, speed: speed, cancellationToken: ct);
    }
}

public interface INumberAudioCache
{
    Task<string?> GetOrCreateAsync(string languageCode, string text, CancellationToken ct = default);
    Task PrewarmAsync(string languageCode, IEnumerable<string> texts, IProgress<int>? progress = null, CancellationToken ct = default);
    Task<long> GetCacheSizeBytesAsync(string languageCode);
    Task ClearAsync(string languageCode);
}

public class NumberAudioCache : INumberAudioCache
{
    private readonly INumberTtsService _ttsService;
    private readonly IFileSystemService _fileSystem;
    private readonly ILogger<NumberAudioCache> _logger;
    private readonly SemaphoreSlim _generationSemaphore;
    private readonly ConcurrentDictionary<string, Task<string?>> _pendingGenerations = new();
    private const int MaxConcurrentTtsJobs = 3;

    public NumberAudioCache(
        INumberTtsService ttsService,
        IFileSystemService fileSystem,
        ILogger<NumberAudioCache> logger)
    {
        _ttsService = ttsService;
        _fileSystem = fileSystem;
        _logger = logger;
        _generationSemaphore = new SemaphoreSlim(MaxConcurrentTtsJobs, MaxConcurrentTtsJobs);
    }

    public async Task<string?> GetOrCreateAsync(string languageCode, string text, CancellationToken ct = default)
    {
        var normalizedText = NormalizeText(text);
        var cacheKey = GetCacheKey(languageCode, normalizedText);
        var cacheFilePath = GetCacheFilePath(languageCode, normalizedText);

        // Return immediately if cached
        if (File.Exists(cacheFilePath))
        {
            _logger.LogDebug("🏴‍☠️ NumberAudioCache: Cache hit for {Text} ({Key})", text, cacheKey);
            return cacheFilePath;
        }

        // Check if generation is already pending (idempotency guard)
        if (_pendingGenerations.TryGetValue(cacheKey, out var pendingTask))
        {
            _logger.LogDebug("🏴‍☠️ NumberAudioCache: Waiting for pending generation {Key}", cacheKey);
            return await pendingTask;
        }

        // Start new generation
        var generationTask = GenerateAndCacheAsync(languageCode, normalizedText, cacheFilePath, ct);
        _pendingGenerations[cacheKey] = generationTask;

        try
        {
            var result = await generationTask;
            return result;
        }
        finally
        {
            _pendingGenerations.TryRemove(cacheKey, out _);
        }
    }

    public async Task PrewarmAsync(
        string languageCode,
        IEnumerable<string> texts,
        IProgress<int>? progress = null,
        CancellationToken ct = default)
    {
        var textList = texts.ToList();
        _logger.LogInformation("🏴‍☠️ NumberAudioCache: Starting prewarm for {Count} items ({Language})", textList.Count, languageCode);

        var completed = 0;
        var tasks = new List<Task>();

        foreach (var text in textList)
        {
            if (ct.IsCancellationRequested)
                break;

            // Fire-and-forget with concurrency limit
            var task = Task.Run(async () =>
            {
                try
                {
                    await GetOrCreateAsync(languageCode, text, ct);
                    Interlocked.Increment(ref completed);
                    progress?.Report(completed);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "🏴‍☠️ NumberAudioCache: Prewarm failed for text '{Text}'", text);
                }
            }, ct);

            tasks.Add(task);
        }

        await Task.WhenAll(tasks);
        _logger.LogInformation("🏴‍☠️ NumberAudioCache: Prewarm complete ({Completed}/{Total})", completed, textList.Count);
    }

    public async Task<long> GetCacheSizeBytesAsync(string languageCode)
    {
        var cacheDir = GetCacheDirectory(languageCode);
        if (!Directory.Exists(cacheDir))
            return 0;

        var files = Directory.GetFiles(cacheDir, "*.mp3");
        long totalBytes = 0;

        await Task.Run(() =>
        {
            foreach (var file in files)
            {
                try
                {
                    totalBytes += new FileInfo(file).Length;
                }
                catch
                {
                    // Skip files that can't be accessed
                }
            }
        });

        return totalBytes;
    }

    public async Task ClearAsync(string languageCode)
    {
        var cacheDir = GetCacheDirectory(languageCode);
        if (!Directory.Exists(cacheDir))
            return;

        await Task.Run(() =>
        {
            try
            {
                Directory.Delete(cacheDir, recursive: true);
                _logger.LogInformation("🏴‍☠️ NumberAudioCache: Cleared cache for {Language}", languageCode);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "🏴‍☠️ NumberAudioCache: Failed to clear cache for {Language}", languageCode);
                throw;
            }
        });
    }

    private async Task<string?> GenerateAndCacheAsync(
        string languageCode,
        string normalizedText,
        string cacheFilePath,
        CancellationToken ct)
    {
        await _generationSemaphore.WaitAsync(ct);
        try
        {
            // Double-check after acquiring lock (another task may have created it)
            if (File.Exists(cacheFilePath))
            {
                _logger.LogDebug("🏴‍☠️ NumberAudioCache: Cache created by another task {Path}", cacheFilePath);
                return cacheFilePath;
            }

            // Ensure cache directory exists
            var cacheDir = Path.GetDirectoryName(cacheFilePath);
            if (!string.IsNullOrEmpty(cacheDir) && !Directory.Exists(cacheDir))
            {
                Directory.CreateDirectory(cacheDir);
            }

            // Select appropriate voice for language
            var voiceId = languageCode.ToLowerInvariant() switch
            {
                "ko" => "jiyoung", // Korean female - warm, clear
                "en" => "echo",    // English female
                _ => "echo"        // Default fallback
            };

            _logger.LogDebug("🏴‍☠️ NumberAudioCache: Generating TTS for '{Text}' using voice {Voice}", normalizedText, voiceId);

            // Generate audio with retry logic
            Stream? audioStream = null;
            Exception? lastException = null;

            for (int attempt = 1; attempt <= 2; attempt++)
            {
                try
                {
                    audioStream = await _ttsService.TextToSpeechAsync(normalizedText, voiceId, speed: 1.0f, ct);

                    if (audioStream != null && audioStream != Stream.Null && audioStream.Length > 0)
                    {
                        break; // Success
                    }

                    _logger.LogWarning("🏴‍☠️ NumberAudioCache: TTS returned empty stream (attempt {Attempt}/2)", attempt);
                }
                catch (Exception ex)
                {
                    lastException = ex;
                    _logger.LogWarning(ex, "🏴‍☠️ NumberAudioCache: TTS attempt {Attempt}/2 failed", attempt);

                    if (attempt == 1)
                        await Task.Delay(1000, ct); // Brief delay before retry
                }
            }

            if (audioStream == null || audioStream == Stream.Null || audioStream.Length == 0)
            {
                var errorMsg = lastException != null
                    ? $"TTS failed after 2 attempts: {lastException.Message}"
                    : "TTS returned empty stream after 2 attempts";

                _logger.LogError("🏴‍☠️ NumberAudioCache: {Error} for text '{Text}'", errorMsg, normalizedText);
                return null;
            }

            // Save to file
            using var fileStream = File.Create(cacheFilePath);
            await audioStream.CopyToAsync(fileStream, ct);

            _logger.LogDebug("🏴‍☠️ NumberAudioCache: Cached audio {Size} bytes → {Path}", audioStream.Length, cacheFilePath);
            return cacheFilePath;
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug("🏴‍☠️ NumberAudioCache: Generation cancelled for '{Text}'", normalizedText);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "🏴‍☠️ NumberAudioCache: Unexpected error generating audio for '{Text}'", normalizedText);
            return null;
        }
        finally
        {
            _generationSemaphore.Release();
        }
    }

    private string NormalizeText(string text)
    {
        // Trim and apply Unicode NFC normalization
        return text.Trim().Normalize(NormalizationForm.FormC);
    }

    private string GetCacheKey(string languageCode, string normalizedText)
    {
        return $"{languageCode}:{normalizedText}";
    }

    private string GetCacheFilePath(string languageCode, string normalizedText)
    {
        var cacheDir = GetCacheDirectory(languageCode);
        var hash = ComputeSha256Hash(normalizedText);
        return Path.Combine(cacheDir, $"{hash}.mp3");
    }

    private string GetCacheDirectory(string languageCode)
    {
        return Path.Combine(_fileSystem.AppDataDirectory, "numbers-tts", languageCode);
    }

    private static string ComputeSha256Hash(string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        var hashBytes = SHA256.HashData(bytes);
        return Convert.ToHexString(hashBytes).ToLowerInvariant();
    }
}

/// <summary>
/// No-op implementation for when TTS service is not available.
/// UI should fall back to displaying AudioCue text when cache returns null.
/// </summary>
public class NoOpNumberAudioCache : INumberAudioCache
{
    private readonly ILogger<NoOpNumberAudioCache> _logger;

    public NoOpNumberAudioCache(ILogger<NoOpNumberAudioCache> logger)
    {
        _logger = logger;
    }

    public Task<string?> GetOrCreateAsync(string languageCode, string text, CancellationToken ct = default)
    {
        _logger.LogDebug("🏴‍☠️ NoOpNumberAudioCache: TTS not available, returning null for '{Text}'", text);
        return Task.FromResult<string?>(null);
    }

    public Task PrewarmAsync(string languageCode, IEnumerable<string> texts, IProgress<int>? progress = null, CancellationToken ct = default)
    {
        _logger.LogInformation("🏴‍☠️ NoOpNumberAudioCache: Prewarm skipped (TTS not available)");
        return Task.CompletedTask;
    }

    public Task<long> GetCacheSizeBytesAsync(string languageCode)
    {
        return Task.FromResult(0L);
    }

    public Task ClearAsync(string languageCode)
    {
        return Task.CompletedTask;
    }
}
