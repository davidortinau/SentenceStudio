// REGRESSION TESTS — YouTube import error handling
//
// Root cause (2026-08-06): YoutubeExplode 6.5.6 uses a deprecated YouTube client blocked by
// YouTube's PO-token requirement, causing VideoUnavailableException for ALL videos.
// Upgraded to 6.6.0 (ANDROID_VR client, upstream PR #936).
// These tests ensure structured error classification persists regardless of provider version
// and that raw provider messages/video IDs never leak to the user.
//
// DO NOT DELETE — recurring production bug requires regression coverage.

using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SentenceStudio.Abstractions;
using SentenceStudio.Data;
using SentenceStudio.Services;
using SentenceStudio.Shared.Models;

namespace SentenceStudio.UnitTests.Services;

public sealed class VideoImportErrorHandlingTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ServiceProvider _sp;
    private readonly ApplicationDbContext _db;

    public VideoImportErrorHandlingTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        var services = new ServiceCollection();
        services.AddDbContext<ApplicationDbContext>(opts =>
            opts.UseSqlite(_connection)
                .ConfigureWarnings(w =>
                    w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.RelationalEventId.PendingModelChangesWarning)));
        services.AddSingleton<IFileSystemService>(Mock.Of<IFileSystemService>());
        _sp = services.BuildServiceProvider();
        _db = _sp.GetRequiredService<ApplicationDbContext>();
        _db.Database.EnsureCreated();
    }

    public void Dispose()
    {
        _sp.Dispose();
        _connection.Dispose();
    }

    // ────────── Configurable fake ──────────

    private sealed class FakeYouTubeImportService : IYouTubeImportService
    {
        public Func<string, Task<YoutubeExplode.Videos.Video>>? GetVideoMetadataFunc { get; set; }
        public Func<string, Task<List<TranscriptTrack>>>? GetAvailableTranscriptsFunc { get; set; }
        public Func<TranscriptTrack, Task<string>>? DownloadTranscriptTextFunc { get; set; }
        public Func<string, double, double, Task<StreamHistory>>? ExtractAudioClipFunc { get; set; }

        public Task<YoutubeExplode.Videos.Video> GetVideoMetadataAsync(string videoUrl) =>
            GetVideoMetadataFunc?.Invoke(videoUrl) ?? throw new NotImplementedException();

        public Task<List<TranscriptTrack>> GetAvailableTranscriptsAsync(string videoUrl) =>
            GetAvailableTranscriptsFunc?.Invoke(videoUrl) ?? throw new NotImplementedException();

        public Task<string> DownloadTranscriptTextAsync(TranscriptTrack track) =>
            DownloadTranscriptTextFunc?.Invoke(track) ?? throw new NotImplementedException();

        public Task<StreamHistory> ExtractAudioClipAsync(string videoUrl, double startTime, double duration) =>
            ExtractAudioClipFunc?.Invoke(videoUrl, startTime, duration) ?? throw new NotImplementedException();
    }

    private VideoImportPipelineService BuildPipeline(IYouTubeImportService youtube, ILogger<VideoImportPipelineService>? logger = null) =>
        new(
            _sp,
            logger ?? NullLogger<VideoImportPipelineService>.Instance,
            youtube,
            null!,  // TranscriptFormattingService — never reached (tests fail at stage 1)
            null!,  // AiService — never reached
            Mock.Of<IFileSystemService>(),
            null);

    private async Task<VideoImport> SeedImport(
        string userProfileId = "test-user-123",
        VideoImportStatus status = VideoImportStatus.Pending,
        DateTime? createdAt = null)
    {
        var import = new VideoImport
        {
            Id = Guid.NewGuid().ToString(),
            UserProfileId = userProfileId,
            VideoUrl = "https://youtube.com/watch?v=TEST",
            Language = "Korean",
            Status = status,
            CreatedAt = createdAt ?? DateTime.UtcNow
        };
        _db.VideoImports.Add(import);
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();
        return import;
    }

    [Fact]
    public async Task ImportQueries_ReturnOnlyTheRequestedUsersRecords()
    {
        var owned = await SeedImport("owner");
        var other = await SeedImport("other");
        var pipeline = BuildPipeline(new FakeYouTubeImportService());

        (await pipeline.GetImportHistoryAsync("owner")).Select(i => i.Id).Should().Equal(owned.Id);
        (await pipeline.GetImportHistoryAsync("")).Should().BeEmpty();
        (await pipeline.GetImportByIdAsync(other.Id, "owner")).Should().BeNull();
        (await pipeline.GetImportByIdAsync(other.Id, "")).Should().BeNull();
        (await pipeline.GetImportByIdAsync(owned.Id, "owner")).Should().NotBeNull();
        (await pipeline.GetFailedImportForVideoAsync("TEST", "")).Should().BeNull();
    }

    [Fact]
    public async Task RetryImport_CannotResetAnotherUsersImport()
    {
        var other = await SeedImport("other", VideoImportStatus.Failed);
        var pipeline = BuildPipeline(new FakeYouTubeImportService());

        await pipeline.RetryImportAsync(other.Id, "");
        await Assert.ThrowsAsync<InvalidOperationException>(() => pipeline.RetryImportAsync(other.Id, "owner"));

        var unchanged = await _db.VideoImports.FindAsync(other.Id);
        unchanged!.Status.Should().Be(VideoImportStatus.Failed);
    }

    [Fact]
    public async Task FailedImportLookup_OnlyReturnsRequestedUsersImport()
    {
        var owned = await SeedImport("owner", VideoImportStatus.Failed);
        var other = await SeedImport("other", VideoImportStatus.Failed);
        _db.VideoImports.Single(vi => vi.Id == owned.Id).VideoId = "same-video";
        _db.VideoImports.Single(vi => vi.Id == other.Id).VideoId = "same-video";
        await _db.SaveChangesAsync();
        var pipeline = BuildPipeline(new FakeYouTubeImportService());

        (await pipeline.GetFailedImportForVideoAsync("same-video", "owner"))!.Id.Should().Be(owned.Id);
        (await pipeline.GetFailedImportForVideoAsync("same-video", "missing")).Should().BeNull();
    }

    [Fact]
    public async Task CleanupStaleImports_OnlyAffectsRequestedUser()
    {
        var old = DateTime.UtcNow.AddMinutes(-30);
        var owned = await SeedImport("owner", VideoImportStatus.FetchingTranscript, old);
        var other = await SeedImport("other", VideoImportStatus.FetchingTranscript, old);
        var pipeline = BuildPipeline(new FakeYouTubeImportService());

        (await pipeline.CleanupStaleImportsAsync("")).Should().Be(0);
        (await pipeline.CleanupStaleImportsAsync("owner")).Should().Be(1);

        (await _db.VideoImports.FindAsync(owned.Id))!.Status.Should().Be(VideoImportStatus.Failed);
        (await _db.VideoImports.FindAsync(other.Id))!.Status.Should().Be(VideoImportStatus.FetchingTranscript);
    }

    [Fact]
    public async Task RunPipelineAsync_RefusesMissingUserWithoutCreatingImport()
    {
        var fake = new FakeYouTubeImportService();
        var pipeline = BuildPipeline(fake);
        var import = new VideoImport { Id = Guid.NewGuid().ToString(), UserProfileId = "" };

        await Assert.ThrowsAsync<ArgumentException>(() => pipeline.RunPipelineAsync(import));
        (await _db.VideoImports.AnyAsync(vi => vi.Id == import.Id)).Should().BeFalse();
    }

    // ────────── VideoImportException unit tests ──────────

    [Fact]
    public void VideoImportException_Carries_ErrorCode()
    {
        var ex = new VideoImportException("test message", "VideoUnavailable", new Exception("inner"));

        ex.ErrorCode.Should().Be("VideoUnavailable");
        ex.Message.Should().Be("test message");
        ex.InnerException!.Message.Should().Be("inner");
    }

    [Fact]
    public void VideoImportException_Does_Not_Expose_VideoUrl()
    {
        var ex = new VideoImportException(
            "This video is not accessible. It may be private, age-restricted, region-locked, or removed.",
            "VideoUnavailable");

        ex.Message.Should().NotContain("youtube.com");
        ex.Message.Should().NotContain("youtu.be");
        ex.Message.Should().NotContain("http");
    }

    [Fact]
    public void VideoImportException_ErrorCodes_Are_Well_Known()
    {
        var codes = new[] { "VideoUnavailable", "RateLimited", "NetworkError", "Unknown" };
        foreach (var code in codes)
        {
            new VideoImportException("test", code).ErrorCode.Should().Be(code);
        }
    }

    // ────────── Pipeline integration: classified exceptions ──────────

    [Fact]
    public async Task Pipeline_VideoUnavailable_PersistsUserFriendlyMessage()
    {
        var fake = new FakeYouTubeImportService
        {
            GetVideoMetadataFunc = _ => throw new VideoImportException(
                "This video is not accessible. It may be private, age-restricted, region-locked, or removed.",
                "VideoUnavailable")
        };
        var import = await SeedImport();
        await BuildPipeline(fake).RunPipelineAsync(import);

        var updated = await _db.VideoImports.FindAsync(import.Id);
        updated!.Status.Should().Be(VideoImportStatus.Failed);
        updated.ErrorMessage.Should().Contain("not accessible");
        updated.ErrorMessage.Should().NotContain("youtube.com");
        updated.ErrorMessage.Should().NotContain("TEST");
    }

    [Fact]
    public async Task Pipeline_RateLimited_PersistsClassifiedMessage()
    {
        var fake = new FakeYouTubeImportService
        {
            GetVideoMetadataFunc = _ => throw new VideoImportException(
                "YouTube is temporarily rate-limiting requests. Please wait a few minutes and try again.",
                "RateLimited")
        };
        var import = await SeedImport();
        await BuildPipeline(fake).RunPipelineAsync(import);

        var updated = await _db.VideoImports.FindAsync(import.Id);
        updated!.Status.Should().Be(VideoImportStatus.Failed);
        updated.ErrorMessage.Should().Contain("rate-limiting");
    }

    [Fact]
    public async Task Pipeline_NetworkError_PersistsClassifiedMessage()
    {
        var fake = new FakeYouTubeImportService
        {
            GetVideoMetadataFunc = _ => throw new VideoImportException(
                "Could not reach YouTube (HTTP 503). Please try again later.",
                "NetworkError")
        };
        var import = await SeedImport();
        await BuildPipeline(fake).RunPipelineAsync(import);

        var updated = await _db.VideoImports.FindAsync(import.Id);
        updated!.Status.Should().Be(VideoImportStatus.Failed);
        updated.ErrorMessage.Should().Contain("Could not reach YouTube");
    }

    // ────────── Pipeline: unclassified exception sanitization ──────────

    [Fact]
    public async Task Pipeline_UnclassifiedException_DoesNotLeakExMessage()
    {
        var fake = new FakeYouTubeImportService
        {
            GetVideoMetadataFunc = _ => throw new InvalidOperationException(
                "secret internal detail with dQw4w9WgXcQ video id")
        };
        var import = await SeedImport();
        await BuildPipeline(fake).RunPipelineAsync(import);

        var updated = await _db.VideoImports.FindAsync(import.Id);
        updated!.Status.Should().Be(VideoImportStatus.Failed);
        updated.ErrorMessage.Should().Be("An unexpected error occurred during import.");
        updated.ErrorMessage.Should().NotContain("dQw4w9WgXcQ");
    }

    // ────────── Pipeline: cancellation passthrough ──────────

    [Fact]
    public async Task Pipeline_Cancellation_IsPreserved()
    {
        var fake = new FakeYouTubeImportService
        {
            GetVideoMetadataFunc = _ => throw new OperationCanceledException("cancelled")
        };
        var import = await SeedImport();

        var act = () => BuildPipeline(fake).RunPipelineAsync(import);
        await act.Should().ThrowAsync<OperationCanceledException>();

        var updated = await _db.VideoImports.FindAsync(import.Id);
        updated!.Status.Should().Be(VideoImportStatus.Failed);
        updated.ErrorMessage.Should().Contain("cancelled");
    }

    // ────────── Pipeline: log classification verification ──────────

    [Fact]
    public async Task Pipeline_LogsErrorCodeForClassifiedExceptions()
    {
        var loggerMock = new Mock<ILogger<VideoImportPipelineService>>();
        var fake = new FakeYouTubeImportService
        {
            GetVideoMetadataFunc = _ => throw new VideoImportException(
                "This video is not accessible.", "VideoUnavailable")
        };
        var import = await SeedImport();
        await BuildPipeline(fake, loggerMock.Object).RunPipelineAsync(import);

        loggerMock.Verify(
            x => x.Log(
                LogLevel.Warning,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, _) => v.ToString()!.Contains("VideoUnavailable")),
                It.IsAny<VideoImportException>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.AtLeastOnce());
    }
}
