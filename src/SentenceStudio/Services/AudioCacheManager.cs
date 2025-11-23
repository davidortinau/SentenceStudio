using System.Collections.Concurrent;
using SentenceStudio.Models;
using Microsoft.Extensions.Logging;

namespace SentenceStudio.Services;

public class AudioCacheConfig
{
    public int CacheAheadCount { get; set; } = 5;        // Sentences to cache ahead
    public int MaxConcurrentJobs { get; set; } = 2;       // Concurrent TTS generations
    public int MaxCacheSizeMB { get; set; } = 50;         // Max cache size per resource
    public bool EnableBackgroundCaching { get; set; } = true;
    public TimeSpan CacheTimeout { get; set; } = TimeSpan.FromMinutes(30);
}

public class AudioCacheManager : IDisposable
{
    private readonly ElevenLabsSpeechService _speechService;
    private readonly ILogger<AudioCacheManager> _logger;
    private readonly AudioCacheConfig _config;
    private readonly ConcurrentQueue<int> _preloadQueue = new();
    private readonly SemaphoreSlim _generationSemaphore;
    private CancellationTokenSource _backgroundCts = new();
    private readonly ConcurrentDictionary<int, string> _audioCache = new();
    private readonly ConcurrentDictionary<int, Task> _pendingGenerations = new();

    private Task? _backgroundTask;
    private LearningResource? _currentResource;
    private List<string> _sentences = new();
    private bool _disposed = false;

    public AudioCacheManager(ElevenLabsSpeechService speechService, ILogger<AudioCacheManager> logger, AudioCacheConfig? config = null)
    {
        _speechService = speechService;
        _logger = logger;
        _config = config ?? new AudioCacheConfig();
        _generationSemaphore = new SemaphoreSlim(_config.MaxConcurrentJobs, _config.MaxConcurrentJobs);
    }

    public event EventHandler<int>? SentenceCached;
    public event EventHandler<string>? CacheError;

    public bool IsCurrentSentenceReady(int index) => _audioCache.ContainsKey(index);
    public int CachedSentencesAhead(int currentIndex) =>
        Enumerable.Range(currentIndex + 1, _config.CacheAheadCount)
                  .Count(i => i < _sentences.Count && _audioCache.ContainsKey(i));

    public bool IsBackgroundCaching => _backgroundTask?.IsCompleted == false;

    public void Initialize(LearningResource resource, List<string> sentences)
    {
        _currentResource = resource;
        _sentences = sentences;

        // Clear previous cache and start background worker
        _audioCache.Clear();
        _pendingGenerations.Clear();
        ClearQueue();

        if (_config.EnableBackgroundCaching)
        {
            _backgroundTask = Task.Run(() => BackgroundCacheWorker(_backgroundCts.Token));
        }
    }

    public async Task<string?> GetAudioPathAsync(int sentenceIndex, bool prioritize = false)
    {
        if (sentenceIndex < 0 || sentenceIndex >= _sentences.Count)
            return null;

        // Return immediately if cached
        if (_audioCache.TryGetValue(sentenceIndex, out var cachedPath))
        {
            // Queue next sentences for background caching
            QueueNextSentences(sentenceIndex + 1);
            return cachedPath;
        }

        // Check if generation is already pending
        if (_pendingGenerations.TryGetValue(sentenceIndex, out var pendingTask))
        {
            try
            {
                await pendingTask;
                return _audioCache.TryGetValue(sentenceIndex, out var path) ? path : null;
            }
            catch (Exception ex)
            {
                CacheError?.Invoke(this, $"Failed to generate audio for sentence {sentenceIndex}: {ex.Message}");
                return null;
            }
        }

        // Generate immediately for current sentence (blocking)
        if (prioritize)
        {
            try
            {
                var path = await GenerateAudioForSentence(sentenceIndex);
                QueueNextSentences(sentenceIndex + 1);
                return path;
            }
            catch (Exception ex)
            {
                CacheError?.Invoke(this, $"Failed to generate priority audio for sentence {sentenceIndex}: {ex.Message}");
                return null;
            }
        }

        // Queue for background generation
        _preloadQueue.Enqueue(sentenceIndex);
        QueueNextSentences(sentenceIndex + 1);

        return null; // Will be available later via background processing
    }

    private void QueueNextSentences(int startIndex)
    {
        for (int i = startIndex; i < Math.Min(startIndex + _config.CacheAheadCount, _sentences.Count); i++)
        {
            if (!_audioCache.ContainsKey(i) && !_pendingGenerations.ContainsKey(i))
            {
                _preloadQueue.Enqueue(i);
            }
        }
    }

    private async Task BackgroundCacheWorker(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                if (_preloadQueue.TryDequeue(out var sentenceIndex))
                {
                    // Skip if already cached or being generated
                    if (_audioCache.ContainsKey(sentenceIndex) || _pendingGenerations.ContainsKey(sentenceIndex))
                        continue;

                    // Generate audio in background
                    var generationTask = GenerateAudioForSentence(sentenceIndex);
                    _pendingGenerations[sentenceIndex] = generationTask;

                    try
                    {
                        await generationTask;
                        _pendingGenerations.TryRemove(sentenceIndex, out _);
                    }
                    catch (Exception ex)
                    {
                        _pendingGenerations.TryRemove(sentenceIndex, out _);
                        CacheError?.Invoke(this, $"Background generation failed for sentence {sentenceIndex}: {ex.Message}");
                    }
                }
                else
                {
                    // No work available, wait a bit
                    await Task.Delay(100, cancellationToken);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                CacheError?.Invoke(this, $"Background cache worker error: {ex.Message}");
                await Task.Delay(1000, cancellationToken); // Wait before retrying
            }
        }
    }

    private async Task<string> GenerateAudioForSentence(int sentenceIndex)
    {
        if (_currentResource == null || sentenceIndex >= _sentences.Count)
            throw new ArgumentException("Invalid sentence index or no resource loaded");

        await _generationSemaphore.WaitAsync();
        try
        {
            var sentence = _sentences[sentenceIndex];
            var cacheKey = $"reading_{_currentResource.Id}_{sentenceIndex}";
            var audioFilePath = Path.Combine(FileSystem.AppDataDirectory, $"{cacheKey}.mp3");

            // Check file cache first
            if (File.Exists(audioFilePath))
            {
                _audioCache[sentenceIndex] = audioFilePath;
                SentenceCached?.Invoke(this, sentenceIndex);
                return audioFilePath;
            }

            // Generate audio with context
            string? previousText = sentenceIndex > 0 ? _sentences[sentenceIndex - 1] : null;
            string? nextText = sentenceIndex < _sentences.Count - 1 ? _sentences[sentenceIndex + 1] : null;

            var audioStream = await _speechService.TextToSpeechAsync(
                sentence,
                previousText: previousText,
                nextText: nextText);

            // Save to file
            using var fileStream = File.Create(audioFilePath);
            await audioStream.CopyToAsync(fileStream);

            _audioCache[sentenceIndex] = audioFilePath;
            SentenceCached?.Invoke(this, sentenceIndex);

            // Clean up old cache entries
            CleanupOldCache(sentenceIndex);

            return audioFilePath;
        }
        finally
        {
            _generationSemaphore.Release();
        }
    }

    private void CleanupOldCache(int currentIndex)
    {
        // Keep current + 2 previous + 5 ahead, remove older files
        var keepIndices = new HashSet<int>();

        // Add indices to keep
        for (int i = Math.Max(0, currentIndex - 2); i <= Math.Min(_sentences.Count - 1, currentIndex + _config.CacheAheadCount); i++)
        {
            keepIndices.Add(i);
        }

        // Remove cache entries and files not in keep list
        var toRemove = _audioCache.Keys.Where(k => !keepIndices.Contains(k)).ToList();
        foreach (var index in toRemove)
        {
            if (_audioCache.TryRemove(index, out var filePath))
            {
                try
                {
                    if (File.Exists(filePath))
                        File.Delete(filePath);
                }
                catch (Exception ex)
                {
                    // Silently handle file deletion errors
                    _logger.LogWarning(ex, "Failed to delete cache file");
                }
            }
        }
    }

    private void ClearQueue()
    {
        while (_preloadQueue.TryDequeue(out _)) { }
    }

    public void Stop()
    {
        if (_backgroundCts != null && !_backgroundCts.Token.IsCancellationRequested)
        {
            _backgroundCts.Cancel();
        }
        ClearQueue();
        _pendingGenerations.Clear();
    }

    public async Task ClearAllCacheAsync()
    {
        // Stop background processing
        if (_backgroundCts != null && !_backgroundCts.Token.IsCancellationRequested)
        {
            await _backgroundCts.CancelAsync();
        }

        // Wait for any pending operations to complete
        if (_backgroundTask != null && !_backgroundTask.IsCompleted)
        {
            try
            {
                await _backgroundTask.WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch (TimeoutException)
            {
                // Continue with cleanup even if background task doesn't stop gracefully
            }
        }

        // Clear in-memory cache and delete files
        var filesToDelete = _audioCache.Values.ToList();
        _audioCache.Clear();
        _pendingGenerations.Clear();
        ClearQueue();

        // Delete cached audio files
        foreach (var filePath in filesToDelete)
        {
            try
            {
                if (File.Exists(filePath))
                {
                    File.Delete(filePath);
                    _logger.LogDebug("🏴‍☠️ AudioCacheManager: Deleted cache file {FilePath}", filePath);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "🏴‍☠️ AudioCacheManager: Failed to delete cache file {FilePath}", filePath);
            }
        }

        // Restart background task if it was running
        if (_config.EnableBackgroundCaching)
        {
            // Create new cancellation token source
            _backgroundCts?.Dispose();
            _backgroundCts = new CancellationTokenSource();
            _backgroundTask = Task.Run(() => BackgroundCacheWorker(_backgroundCts.Token));
        }

        _logger.LogInformation("🏴‍☠️ AudioCacheManager: Cache cleared successfully, deleted {Count} files", filesToDelete.Count);
    }

    public void Dispose()
    {
        if (_disposed) return;

        if (_backgroundCts != null && !_backgroundCts.Token.IsCancellationRequested)
        {
            _backgroundCts.Cancel();
        }
        _backgroundTask?.Wait(TimeSpan.FromSeconds(5)); // Give it time to stop gracefully

        _backgroundCts?.Dispose();
        _generationSemaphore?.Dispose();

        _disposed = true;
    }
}
