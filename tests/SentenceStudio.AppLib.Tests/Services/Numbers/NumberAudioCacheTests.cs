using Microsoft.Extensions.Logging;
using Moq;
using SentenceStudio.Abstractions;
using SentenceStudio.Services.Numbers;
using Xunit;

namespace SentenceStudio.AppLib.Tests.Services.Numbers;

/// <summary>
/// Fake TTS service for testing NumberAudioCache.
/// </summary>
public class FakeTtsService : INumberTtsService
{
    private readonly Func<string, Stream>? _ttsProvider;
    private int _callCount = 0;

    public int CallCount => _callCount;

    public FakeTtsService(Func<string, Stream>? ttsProvider = null)
    {
        _ttsProvider = ttsProvider;
    }

    public Task<Stream> TextToSpeechAsync(string text, string voiceId, float speed, CancellationToken ct = default)
    {
        Interlocked.Increment(ref _callCount);

        if (_ttsProvider != null)
        {
            return Task.FromResult(_ttsProvider(text));
        }

        // Default: return a simple test stream
        return Task.FromResult<Stream>(new MemoryStream(new byte[] { 1, 2, 3 }));
    }
}

public class NumberAudioCacheTests : IDisposable
{
    private readonly FakeTtsService _fakeTtsService;
    private readonly Mock<IFileSystemService> _mockFileSystem;
    private readonly Mock<ILogger<NumberAudioCache>> _mockLogger;
    private readonly NumberAudioCache _cache;
    private readonly string _testCacheDir;

    public NumberAudioCacheTests()
    {
        _fakeTtsService = new FakeTtsService();
        _mockFileSystem = new Mock<IFileSystemService>();
        _mockLogger = new Mock<ILogger<NumberAudioCache>>();

        // Use a real temp directory for testing file operations
        _testCacheDir = Path.Combine(Path.GetTempPath(), "ss-test-cache-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testCacheDir);
        _mockFileSystem.Setup(f => f.AppDataDirectory).Returns(_testCacheDir);

        _cache = new NumberAudioCache(
            _fakeTtsService,
            _mockFileSystem.Object,
            _mockLogger.Object);
    }

    [Fact]
    public async Task GetOrCreateAsync_ReturnsCachedPath_WhenFileExists()
    {
        // Arrange
        var languageCode = "ko";
        var text = "한";

        // First call creates the cache
        var path1 = await _cache.GetOrCreateAsync(languageCode, text);
        Assert.NotNull(path1);
        Assert.True(File.Exists(path1));

        // Second call should return the same path without calling TTS again
        var callCountBefore = _fakeTtsService.CallCount;
        var path2 = await _cache.GetOrCreateAsync(languageCode, text);
        Assert.Equal(path1, path2);
        Assert.True(File.Exists(path2));

        // Verify TTS was not called on second request (call count unchanged)
        Assert.Equal(callCountBefore, _fakeTtsService.CallCount);
    }

    [Fact]
    public async Task GetOrCreateAsync_Idempotent_WhenCalledConcurrently()
    {
        // Arrange
        var languageCode = "ko";
        var text = "두";

        var fakeWithDelay = new FakeTtsService(t =>
        {
            Thread.Sleep(100); // Simulate TTS latency
            return new MemoryStream(new byte[] { 1, 2, 3 });
        });

        var cache = new NumberAudioCache(fakeWithDelay, _mockFileSystem.Object, _mockLogger.Object);

        // Act - Call concurrently 5 times
        var tasks = Enumerable.Range(0, 5)
            .Select(_ => cache.GetOrCreateAsync(languageCode, text))
            .ToArray();

        var paths = await Task.WhenAll(tasks);

        // Assert - All paths should be the same
        Assert.All(paths, p => Assert.Equal(paths[0], p));
        Assert.True(File.Exists(paths[0]!));

        // Verify TTS was only called once despite concurrent requests
        Assert.Equal(1, fakeWithDelay.CallCount);
    }

    [Fact]
    public async Task GetOrCreateAsync_DifferentTexts_ProduceDifferentPaths()
    {
        // Arrange
        var languageCode = "ko";
        var text1 = "한";
        var text2 = "두";

        // Act
        var path1 = await _cache.GetOrCreateAsync(languageCode, text1);
        var path2 = await _cache.GetOrCreateAsync(languageCode, text2);

        // Assert - Paths should be different (hash collision impossible for different inputs)
        Assert.NotEqual(path1, path2);
        Assert.True(File.Exists(path1!));
        Assert.True(File.Exists(path2!));
    }

    [Fact]
    public async Task PrewarmAsync_RespectsConcurrencyLimit()
    {
        // Arrange
        var languageCode = "ko";
        var texts = Enumerable.Range(1, 10).Select(i => $"Text {i}").ToList();
        var concurrentCalls = 0;
        var maxConcurrent = 0;
        var lockObj = new object();

        var fakeWithTracking = new FakeTtsService(t =>
        {
            lock (lockObj)
            {
                concurrentCalls++;
                maxConcurrent = Math.Max(maxConcurrent, concurrentCalls);
            }

            Thread.Sleep(50); // Simulate TTS latency

            lock (lockObj)
            {
                concurrentCalls--;
            }

            return new MemoryStream(new byte[] { 1, 2, 3 });
        });

        var cache = new NumberAudioCache(fakeWithTracking, _mockFileSystem.Object, _mockLogger.Object);

        // Act
        await cache.PrewarmAsync(languageCode, texts);

        // Assert - Should never exceed concurrency limit (3)
        Assert.True(maxConcurrent <= 3, $"Max concurrent was {maxConcurrent}, expected <= 3");
    }

    [Fact]
    public async Task PrewarmAsync_ReportsProgress()
    {
        // Arrange
        var languageCode = "ko";
        var texts = new[] { "한", "두", "세" };
        var progressReports = new List<int>();
        var progress = new Progress<int>(p => progressReports.Add(p));

        // Act
        await _cache.PrewarmAsync(languageCode, texts, progress);

        // Assert
        Assert.Contains(1, progressReports);
        Assert.Contains(2, progressReports);
        Assert.Contains(3, progressReports);
    }

    [Fact]
    public async Task GetOrCreateAsync_ReturnNull_WhenTtsFails()
    {
        // Arrange
        var languageCode = "ko";
        var text = "한";

        var fakeWithFailure = new FakeTtsService(t =>
        {
            throw new Exception("TTS API error");
        });

        var cache = new NumberAudioCache(fakeWithFailure, _mockFileSystem.Object, _mockLogger.Object);

        // Act
        var result = await cache.GetOrCreateAsync(languageCode, text);

        // Assert - Should return null and not throw
        Assert.Null(result);
    }

    [Fact]
    public async Task GetCacheSizeBytesAsync_ReturnsZero_WhenCacheEmpty()
    {
        // Arrange
        var languageCode = "ko";

        // Act
        var size = await _cache.GetCacheSizeBytesAsync(languageCode);

        // Assert
        Assert.Equal(0, size);
    }

    [Fact]
    public async Task GetCacheSizeBytesAsync_ReturnsTotalSize_WhenCachePopulated()
    {
        // Arrange
        var languageCode = "ko";
        var texts = new[] { "한", "두" };
        var audioData = new byte[1000]; // 1KB per file

        var fakeWith1KBFiles = new FakeTtsService(t => new MemoryStream(audioData));
        var cache = new NumberAudioCache(fakeWith1KBFiles, _mockFileSystem.Object, _mockLogger.Object);

        // Act - Populate cache
        await cache.GetOrCreateAsync(languageCode, texts[0]);
        await cache.GetOrCreateAsync(languageCode, texts[1]);

        var size = await cache.GetCacheSizeBytesAsync(languageCode);

        // Assert - Should be ~2KB
        Assert.True(size >= 2000, $"Expected >= 2000 bytes, got {size}");
    }

    [Fact]
    public async Task ClearAsync_RemovesCacheDirectory()
    {
        // Arrange
        var languageCode = "ko";
        var text = "한";

        // Create cache
        var path = await _cache.GetOrCreateAsync(languageCode, text);
        Assert.NotNull(path);
        Assert.True(File.Exists(path));

        // Act - Clear cache
        await _cache.ClearAsync(languageCode);

        // Assert - File should no longer exist
        Assert.False(File.Exists(path));
    }

    public void Dispose()
    {
        // Cleanup test cache directory
        try
        {
            if (Directory.Exists(_testCacheDir))
            {
                Directory.Delete(_testCacheDir, recursive: true);
            }
        }
        catch
        {
            // Best effort cleanup
        }
    }
}

public class NoOpNumberAudioCacheTests
{
    [Fact]
    public async Task GetOrCreateAsync_AlwaysReturnsNull()
    {
        // Arrange
        var logger = Mock.Of<ILogger<NoOpNumberAudioCache>>();
        var cache = new NoOpNumberAudioCache(logger);

        // Act
        var result = await cache.GetOrCreateAsync("ko", "한");

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public async Task PrewarmAsync_DoesNotThrow()
    {
        // Arrange
        var logger = Mock.Of<ILogger<NoOpNumberAudioCache>>();
        var cache = new NoOpNumberAudioCache(logger);
        var texts = new[] { "한", "두", "세" };

        // Act & Assert - Should complete without throwing
        await cache.PrewarmAsync("ko", texts);
    }

    [Fact]
    public async Task GetCacheSizeBytesAsync_AlwaysReturnsZero()
    {
        // Arrange
        var logger = Mock.Of<ILogger<NoOpNumberAudioCache>>();
        var cache = new NoOpNumberAudioCache(logger);

        // Act
        var size = await cache.GetCacheSizeBytesAsync("ko");

        // Assert
        Assert.Equal(0, size);
    }
}
