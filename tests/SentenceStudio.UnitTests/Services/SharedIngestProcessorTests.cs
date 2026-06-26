using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SentenceStudio.Services;
using SentenceStudio.Shared.Models;
using SentenceStudio.Sharing;

namespace SentenceStudio.UnitTests.Services;

/// <summary>
/// Unit tests for <see cref="SharedIngestProcessor"/>.
/// All dependencies are faked/mocked — no database or AI calls.
/// </summary>
public sealed class SharedIngestProcessorTests
{
    // ---------------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------------

    private const string TestUserId = "user-test-123";
    private const string TargetLanguage = "Korean";
    private const string NativeLanguage = "English";
    private const string SharedInboxId = "resource-shared-inbox-abc";

    /// <summary>Simple in-memory ISharedIngestQueue backed by a list.</summary>
    private sealed class InMemorySharedIngestQueue : ISharedIngestQueue
    {
        private readonly List<SharedIngestItem> _items = new();

        public void Enqueue(SharedIngestItem item) => _items.Add(item);

        public IReadOnlyList<SharedIngestItem> List() => _items.AsReadOnly();

        public bool Remove(string id)
        {
            var item = _items.FirstOrDefault(i => i.Id == id);
            if (item is null) return false;
            _items.Remove(item);
            return true;
        }
    }

    private static ContentImportPreview MakePreview(int rowCount = 1) =>
        new()
        {
            Rows = Enumerable.Range(1, rowCount)
                .Select(i => new ImportRow
                {
                    RowNumber = i,
                    TargetLanguageTerm = $"word{i}",
                    NativeLanguageTerm = $"translation{i}",
                    Status = RowStatus.Ok,
                    IsSelected = true
                })
                .ToList()
                .AsReadOnly()
        };

    private static ContentImportResult MakeCommitResult(string resourceId, int created = 1) =>
        new() { ResourceId = resourceId, CreatedCount = created, SkippedCount = 0 };

    private static SharedIngestItem TextItem(string payload = "Hello world") =>
        new()
        {
            Kind = SharedIngestKind.Text,
            Payload = payload,
            CapturedAtUtc = DateTime.UtcNow
        };

    private static SharedIngestItem UrlItem(string url = "https://example.com/article") =>
        new()
        {
            Kind = SharedIngestKind.Url,
            Payload = url,
            CapturedAtUtc = DateTime.UtcNow
        };

    /// <summary>Builds a standard IActiveUserProfileProvider mock for a signed-in user.</summary>
    private static Mock<IActiveUserProfileProvider> BuildProfileProvider(string userId = TestUserId)
    {
        var mock = new Mock<IActiveUserProfileProvider>();
        UserProfile? profile = string.IsNullOrEmpty(userId)
            ? null
            : new UserProfile { Id = userId, TargetLanguage = TargetLanguage, NativeLanguage = NativeLanguage };
        mock.Setup(p => p.GetActiveAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(profile);
        return mock;
    }

    /// <summary>
    /// Builds a processor using the given queue, mocked import service,
    /// inbox finder, profile provider, and optional web fetcher / video pipeline / gate.
    /// Each call creates a fresh <see cref="SharedIngestDrainGate"/> by default,
    /// ensuring tests are fully isolated from one another.
    /// </summary>
    private static SharedIngestProcessor BuildProcessor(
        InMemorySharedIngestQueue queue,
        Mock<IContentImportService> importServiceMock,
        Mock<ISharedInboxResourceFinder> inboxFinderMock,
        Mock<IActiveUserProfileProvider> profileProviderMock,
        Mock<IWebArticleFetcher>? webFetcherMock = null,
        Mock<IVideoImportPipeline>? videoImportMock = null,
        SharedIngestDrainGate? gate = null)
    {
        webFetcherMock ??= new Mock<IWebArticleFetcher>();
        videoImportMock ??= new Mock<IVideoImportPipeline>();
        gate ??= new SharedIngestDrainGate();
        return new(
            queue,
            importServiceMock.Object,
            inboxFinderMock.Object,
            webFetcherMock.Object,
            videoImportMock.Object,
            profileProviderMock.Object,
            new SharedIngestNotifier(),
            gate,
            NullLogger<SharedIngestProcessor>.Instance);
    }

    // ---------------------------------------------------------------------------
    // Tests
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task HappyPath_TwoTextItems_BothProcessed_SameResourceId()
    {
        // Arrange: queue with two text items, no pre-existing Shared Inbox
        var queue = new InMemorySharedIngestQueue();
        var itemA = TextItem("안녕하세요");
        var itemB = TextItem("감사합니다");
        queue.Enqueue(itemA);
        queue.Enqueue(itemB);

        var importService = new Mock<IContentImportService>();
        importService
            .Setup(s => s.ParseSharedTextAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(MakePreview(rowCount: 2));
        // First commit creates the resource (Mode=New) and returns SharedInboxId
        importService
            .SetupSequence(s => s.CommitImportAsync(It.IsAny<ContentImportCommit>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(MakeCommitResult(SharedInboxId, created: 2))  // itemA
            .ReturnsAsync(MakeCommitResult(SharedInboxId, created: 2)); // itemB

        var inboxFinder = new Mock<ISharedInboxResourceFinder>();
        inboxFinder
            .Setup(f => f.FindSharedInboxAsync(TestUserId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((LearningResource?)null); // not found up front

        var processor = BuildProcessor(queue, importService, inboxFinder, BuildProfileProvider());

        // Act
        var result = await processor.DrainAsync();

        // Assert — overall counts
        result.Deferred.Should().BeFalse();
        result.ProcessedCount.Should().Be(2);
        result.CreatedVocabCount.Should().Be(4); // 2+2
        result.SkippedVocabCount.Should().Be(0);
        result.FailedCount.Should().Be(0);
        result.ResourceId.Should().Be(SharedInboxId);

        // Both items removed from queue
        queue.List().Should().BeEmpty();

        // ParseSharedTextAsync called twice (new share-specific path)
        importService.Verify(
            s => s.ParseSharedTextAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Exactly(2));

        // ParseContentAsync was NOT called for text items
        importService.Verify(
            s => s.ParseContentAsync(It.IsAny<ContentImportRequest>(), It.IsAny<CancellationToken>()),
            Times.Never);

        // First commit used Mode=New, second used Mode=Existing with the captured id
        importService.Verify(
            s => s.CommitImportAsync(
                It.Is<ContentImportCommit>(c => c.Target.Mode == ImportTargetMode.New),
                It.IsAny<CancellationToken>()),
            Times.Once);
        importService.Verify(
            s => s.CommitImportAsync(
                It.Is<ContentImportCommit>(c =>
                    c.Target.Mode == ImportTargetMode.Existing &&
                    c.Target.ExistingResourceId == SharedInboxId),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task NoActiveUser_Returns_Deferred_NothingProcessed()
    {
        // Arrange
        var queue = new InMemorySharedIngestQueue();
        queue.Enqueue(TextItem("some text"));

        var importService = new Mock<IContentImportService>();
        var inboxFinder = new Mock<ISharedInboxResourceFinder>();
        var profileProvider = BuildProfileProvider(userId: ""); // empty user id

        var processor = BuildProcessor(queue, importService, inboxFinder, profileProvider);

        // Act
        var result = await processor.DrainAsync();

        // Assert
        result.Deferred.Should().BeTrue();
        result.ProcessedCount.Should().Be(0);

        // Nothing was parsed or committed
        importService.Verify(
            s => s.ParseContentAsync(It.IsAny<ContentImportRequest>(), It.IsAny<CancellationToken>()),
            Times.Never);
        importService.Verify(
            s => s.ParseSharedTextAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
        importService.Verify(
            s => s.CommitImportAsync(It.IsAny<ContentImportCommit>(), It.IsAny<CancellationToken>()),
            Times.Never);

        // Item remains in queue
        queue.List().Should().HaveCount(1);
    }

    [Fact]
    public async Task ArticleUrlItem_FetchSucceeds_ImportedAsResource_RemovedFromQueue()
    {
        // Arrange: article URL (not YouTube) → fetch → ParseContentAsync(Transcript) → CommitImportAsync(New resource)
        var queue = new InMemorySharedIngestQueue();
        var urlItem = UrlItem("https://example.com/article");
        queue.Enqueue(urlItem);

        var fetchedText = "한국어 vocabulary: 안녕하세요 means hello in Korean language context.";
        const string articleTitle = "Example Article";
        const string newResourceId = "resource-new-abc";

        var webFetcher = new Mock<IWebArticleFetcher>();
        webFetcher
            .Setup(f => f.FetchReadableTextAsync(urlItem.Payload, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new WebArticleText(urlItem.Payload, articleTitle, fetchedText, true, null));

        var importService = new Mock<IContentImportService>();
        importService
            .Setup(s => s.ParseContentAsync(It.IsAny<ContentImportRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(MakePreview(rowCount: 3));
        importService
            .Setup(s => s.CommitImportAsync(It.IsAny<ContentImportCommit>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(MakeCommitResult(newResourceId, created: 3));

        var inboxFinder = new Mock<ISharedInboxResourceFinder>();
        inboxFinder
            .Setup(f => f.FindSharedInboxAsync(TestUserId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((LearningResource?)null);

        var processor = BuildProcessor(queue, importService, inboxFinder, BuildProfileProvider(), webFetcher);

        // Act
        var result = await processor.DrainAsync();

        // Assert
        result.ProcessedCount.Should().Be(1);
        result.CreatedVocabCount.Should().Be(3);
        result.FailedCount.Should().Be(0);
        result.EmptyCount.Should().Be(0);

        // URL item was removed from queue
        queue.List().Should().BeEmpty();

        // ParseContentAsync called with Transcript content type
        importService.Verify(
            s => s.ParseContentAsync(
                It.Is<ContentImportRequest>(r =>
                    r.RawText == fetchedText &&
                    r.ContentType == ContentType.Transcript &&
                    r.HarvestTranscript),
                It.IsAny<CancellationToken>()),
            Times.Once);

        // CommitImportAsync called with Mode=New (new resource, not Shared Inbox)
        importService.Verify(
            s => s.CommitImportAsync(
                It.Is<ContentImportCommit>(c =>
                    c.Target.Mode == ImportTargetMode.New &&
                    c.Target.NewResourceTitle == articleTitle),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task UrlItem_FetchFails_LeftInQueue_FailedCountIncremented()
    {
        // Arrange
        var queue = new InMemorySharedIngestQueue();
        var urlItem = UrlItem();
        queue.Enqueue(urlItem);

        var webFetcher = new Mock<IWebArticleFetcher>();
        webFetcher
            .Setup(f => f.FetchReadableTextAsync(urlItem.Payload, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new WebArticleText(urlItem.Payload, null, "", false, "HTTP 503"));

        var importService = new Mock<IContentImportService>();
        var inboxFinder = new Mock<ISharedInboxResourceFinder>();
        inboxFinder
            .Setup(f => f.FindSharedInboxAsync(TestUserId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((LearningResource?)null);

        var processor = BuildProcessor(queue, importService, inboxFinder, BuildProfileProvider(), webFetcher);

        // Act
        var result = await processor.DrainAsync();

        // Assert
        result.FailedCount.Should().Be(1);
        result.ProcessedCount.Should().Be(0);
        result.EmptyCount.Should().Be(0);

        // URL item remains in queue for retry
        queue.List().Should().ContainSingle(i => i.Id == urlItem.Id);

        // No parse or commit was attempted
        importService.Verify(
            s => s.ParseContentAsync(It.IsAny<ContentImportRequest>(), It.IsAny<CancellationToken>()),
            Times.Never);
        importService.Verify(
            s => s.CommitImportAsync(It.IsAny<ContentImportCommit>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task UrlItem_FetchSucceeds_EmptyText_RemovedFromQueue_EmptyCountIncremented()
    {
        // Arrange: fetch returns Succeeded=true but empty text (thin page with no fallback)
        var queue = new InMemorySharedIngestQueue();
        var urlItem = UrlItem();
        queue.Enqueue(urlItem);

        var webFetcher = new Mock<IWebArticleFetcher>();
        webFetcher
            .Setup(f => f.FetchReadableTextAsync(urlItem.Payload, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new WebArticleText(urlItem.Payload, null, "", true, null));

        var importService = new Mock<IContentImportService>();
        var inboxFinder = new Mock<ISharedInboxResourceFinder>();
        inboxFinder
            .Setup(f => f.FindSharedInboxAsync(TestUserId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((LearningResource?)null);

        var processor = BuildProcessor(queue, importService, inboxFinder, BuildProfileProvider(), webFetcher);

        // Act
        var result = await processor.DrainAsync();

        // Assert
        result.EmptyCount.Should().Be(1);
        result.FailedCount.Should().Be(0);
        result.ProcessedCount.Should().Be(0);

        // Empty-text URL item is removed so it does not retry forever
        queue.List().Should().BeEmpty();

        // No parse or commit was attempted
        importService.Verify(
            s => s.ParseContentAsync(It.IsAny<ContentImportRequest>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task PartialFailure_ItemA_Succeeds_ItemB_Throws_ProcessingContinues()
    {
        // Arrange
        var queue = new InMemorySharedIngestQueue();
        var itemA = TextItem("good item");
        var itemB = TextItem("bad item");
        queue.Enqueue(itemA);
        queue.Enqueue(itemB);

        var importService = new Mock<IContentImportService>();
        importService
            .Setup(s => s.ParseSharedTextAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(MakePreview());
        // itemA commits successfully; itemB's commit throws
        importService
            .SetupSequence(s => s.CommitImportAsync(It.IsAny<ContentImportCommit>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(MakeCommitResult(SharedInboxId, created: 1))
            .ThrowsAsync(new InvalidOperationException("AI service unavailable"));

        var inboxFinder = new Mock<ISharedInboxResourceFinder>();
        inboxFinder
            .Setup(f => f.FindSharedInboxAsync(TestUserId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((LearningResource?)null);

        var processor = BuildProcessor(queue, importService, inboxFinder, BuildProfileProvider());

        // Act
        var result = await processor.DrainAsync();

        // Assert
        result.ProcessedCount.Should().Be(1);  // itemA
        result.FailedCount.Should().Be(1);     // itemB
        result.CreatedVocabCount.Should().Be(1);

        // itemA removed; itemB remains queued for retry
        queue.List().Should().ContainSingle(i => i.Id == itemB.Id);
        queue.List().Should().NotContain(i => i.Id == itemA.Id);

        // Both parse calls were made (itemB was parsed before commit threw)
        importService.Verify(
            s => s.ParseSharedTextAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Exactly(2));
    }

    [Fact]
    public async Task ExistingSharedInbox_AllItemsUseExistingMode()
    {
        // Arrange: "Shared Inbox" already exists for the user
        var queue = new InMemorySharedIngestQueue();
        queue.Enqueue(TextItem("item one"));
        queue.Enqueue(TextItem("item two"));

        var existingResource = new LearningResource
        {
            Id = SharedInboxId,
            Title = SharedIngestProcessor.SharedInboxResourceTitle
        };

        var importService = new Mock<IContentImportService>();
        importService
            .Setup(s => s.ParseSharedTextAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(MakePreview());
        importService
            .Setup(s => s.CommitImportAsync(It.IsAny<ContentImportCommit>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(MakeCommitResult(SharedInboxId));

        var inboxFinder = new Mock<ISharedInboxResourceFinder>();
        inboxFinder
            .Setup(f => f.FindSharedInboxAsync(TestUserId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(existingResource);

        var processor = BuildProcessor(queue, importService, inboxFinder, BuildProfileProvider());

        // Act
        var result = await processor.DrainAsync();

        // Assert — Mode=New was never used; both committed with Mode=Existing
        importService.Verify(
            s => s.CommitImportAsync(
                It.Is<ContentImportCommit>(c => c.Target.Mode == ImportTargetMode.New),
                It.IsAny<CancellationToken>()),
            Times.Never);
        importService.Verify(
            s => s.CommitImportAsync(
                It.Is<ContentImportCommit>(c =>
                    c.Target.Mode == ImportTargetMode.Existing &&
                    c.Target.ExistingResourceId == SharedInboxId),
                It.IsAny<CancellationToken>()),
            Times.Exactly(2));

        result.ProcessedCount.Should().Be(2);
        result.FailedCount.Should().Be(0);
        queue.List().Should().BeEmpty();
    }

    [Fact]
    public async Task EmptyPreview_ItemRemovedFromQueue_EmptyCountIncremented_NoCommit()
    {
        // Arrange: parse returns no rows
        var queue = new InMemorySharedIngestQueue();
        var item = TextItem("unrecognisable content");
        queue.Enqueue(item);

        var importService = new Mock<IContentImportService>();
        importService
            .Setup(s => s.ParseSharedTextAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(MakePreview(rowCount: 0)); // zero rows

        var inboxFinder = new Mock<ISharedInboxResourceFinder>();
        inboxFinder
            .Setup(f => f.FindSharedInboxAsync(TestUserId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((LearningResource?)null);

        var processor = BuildProcessor(queue, importService, inboxFinder, BuildProfileProvider());

        // Act
        var result = await processor.DrainAsync();

        // Assert
        result.EmptyCount.Should().Be(1);
        result.ProcessedCount.Should().Be(1);
        result.CreatedVocabCount.Should().Be(0);

        // Item removed despite empty parse (won't retry forever)
        queue.List().Should().BeEmpty();

        // CommitImportAsync was never called
        importService.Verify(
            s => s.CommitImportAsync(It.IsAny<ContentImportCommit>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task SingleFlight_ConcurrentDrainOnSeparateInstances_ReturnedNoOp()
    {
        // Models the iOS activation pattern: each foreground creates a new DI scope,
        // producing a NEW SharedIngestProcessor instance.  The injected singleton gate
        // collapses concurrent drains across instances, not just within one instance.

        // Shared queue and shared gate — same singleton as production DI would provide.
        var sharedGate = new SharedIngestDrainGate();
        var queue = new InMemorySharedIngestQueue();
        queue.Enqueue(TextItem("slow item"));

        var parseStarted = new SemaphoreSlim(0, 1);
        var parseContinue = new SemaphoreSlim(0, 1);

        // Instance A — slow parse that signals when it starts.
        var importServiceA = new Mock<IContentImportService>();
        importServiceA
            .Setup(s => s.ParseSharedTextAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(async (string _, string _, string _, CancellationToken _) =>
            {
                parseStarted.Release();          // signal: A has entered parse
                await parseContinue.WaitAsync(); // block until we release from the test
                return MakePreview();
            });
        importServiceA
            .Setup(s => s.CommitImportAsync(It.IsAny<ContentImportCommit>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(MakeCommitResult(SharedInboxId));

        var inboxFinderA = new Mock<ISharedInboxResourceFinder>();
        inboxFinderA
            .Setup(f => f.FindSharedInboxAsync(TestUserId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((LearningResource?)null);

        // Both A and B share the same gate — same as production singleton DI.
        var processorA = BuildProcessor(queue, importServiceA, inboxFinderA, BuildProfileProvider(), gate: sharedGate);

        // Instance B — separate object, separate mocks; should never reach parse/commit.
        var importServiceB = new Mock<IContentImportService>();
        var inboxFinderB = new Mock<ISharedInboxResourceFinder>();
        var processorB = BuildProcessor(queue, importServiceB, inboxFinderB, BuildProfileProvider(), gate: sharedGate);

        // Act: start A's drain (will block inside parse).
        var firstDrain = Task.Run(() => processorA.DrainAsync());

        // Wait until A is inside parse, then fire B's drain — should return no-op immediately.
        await parseStarted.WaitAsync();
        var secondDrainResult = await processorB.DrainAsync();

        // Release A and await completion.
        parseContinue.Release();
        var firstDrainResult = await firstDrain;

        // Assert: B was a no-op — gate held it off.
        secondDrainResult.ProcessedCount.Should().Be(0);
        secondDrainResult.CreatedVocabCount.Should().Be(0);
        secondDrainResult.Deferred.Should().BeFalse();

        importServiceB.Verify(
            s => s.ParseSharedTextAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
        importServiceB.Verify(
            s => s.CommitImportAsync(It.IsAny<ContentImportCommit>(), It.IsAny<CancellationToken>()),
            Times.Never);

        // A completed normally (gate was released in finally).
        firstDrainResult.ProcessedCount.Should().Be(1);

        // Gate must be idle after A completes (Exit() called in finally).
        sharedGate.TryEnter().Should().BeTrue("gate must be released after drain completes");
        sharedGate.Exit(); // clean up so we don't leak
    }

    [Fact]
    public async Task TextItem_UsesParseSharedTextAsync_ArticleUrlItem_ImportsAsResource()
    {
        // Arrange: one text item, one article URL item
        var queue = new InMemorySharedIngestQueue();
        var textItem = TextItem("왜 사람들은 나쁜 댓글을 달까요?");
        var urlItem = UrlItem("https://example.com/article");
        queue.Enqueue(textItem);
        queue.Enqueue(urlItem);

        var fetchedText = "한국어 article body content for vocabulary extraction";
        const string articleTitle = "Example";
        var webFetcher = new Mock<IWebArticleFetcher>();
        webFetcher
            .Setup(f => f.FetchReadableTextAsync(urlItem.Payload, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new WebArticleText(urlItem.Payload, articleTitle, fetchedText, true, null));

        var importService = new Mock<IContentImportService>();
        importService
            .Setup(s => s.ParseSharedTextAsync(textItem.Payload, TargetLanguage, NativeLanguage, It.IsAny<CancellationToken>()))
            .ReturnsAsync(MakePreview(rowCount: 3));
        importService
            .Setup(s => s.ParseContentAsync(It.IsAny<ContentImportRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(MakePreview(rowCount: 5));
        importService
            .Setup(s => s.CommitImportAsync(It.IsAny<ContentImportCommit>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(MakeCommitResult(SharedInboxId));

        var inboxFinder = new Mock<ISharedInboxResourceFinder>();
        inboxFinder
            .Setup(f => f.FindSharedInboxAsync(TestUserId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((LearningResource?)null);

        var processor = BuildProcessor(queue, importService, inboxFinder, BuildProfileProvider(), webFetcher);

        // Act
        var result = await processor.DrainAsync();

        // Assert
        result.ProcessedCount.Should().Be(2);
        result.FailedCount.Should().Be(0);
        queue.List().Should().BeEmpty();

        // Text item routed through ParseSharedTextAsync
        importService.Verify(
            s => s.ParseSharedTextAsync(textItem.Payload, TargetLanguage, NativeLanguage, It.IsAny<CancellationToken>()),
            Times.Once);

        // Article URL routed through ParseContentAsync with Transcript type
        importService.Verify(
            s => s.ParseContentAsync(
                It.Is<ContentImportRequest>(r =>
                    r.RawText == fetchedText &&
                    r.ContentType == ContentType.Transcript),
                It.IsAny<CancellationToken>()),
            Times.Once);

        // Article URL committed with Mode=New (not Shared Inbox)
        importService.Verify(
            s => s.CommitImportAsync(
                It.Is<ContentImportCommit>(c =>
                    c.Target.Mode == ImportTargetMode.New &&
                    c.Target.NewResourceTitle == articleTitle),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task YouTubeUrlItem_VideoImportKickedOff_ItemRemoved_NotifierVideoImportStarted()
    {
        // Arrange: YouTube URL → video import detached, item removed immediately
        const string youtubeUrl = "https://www.youtube.com/watch?v=dQw4w9WgXcQ";
        var queue = new InMemorySharedIngestQueue();
        var urlItem = UrlItem(youtubeUrl);
        queue.Enqueue(urlItem);

        var importStarted = new TaskCompletionSource<bool>();
        var videoImport = new Mock<IVideoImportPipeline>();
        videoImport
            .Setup(v => v.ImportFromUrlAsync(youtubeUrl, TestUserId, TargetLanguage))
            .Returns(async () =>
            {
                importStarted.TrySetResult(true);
                await Task.Delay(50); // simulate async work
                return new SentenceStudio.Shared.Models.VideoImport { Id = "vi-1" };
            });

        var importService = new Mock<IContentImportService>();
        var inboxFinder = new Mock<ISharedInboxResourceFinder>();
        inboxFinder
            .Setup(f => f.FindSharedInboxAsync(TestUserId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((LearningResource?)null);

        var processor = BuildProcessor(queue, importService, inboxFinder, BuildProfileProvider(),
            videoImportMock: videoImport);

        // Act
        var result = await processor.DrainAsync();

        // Wait briefly so the detached Task.Run has a chance to call ImportFromUrlAsync
        await Task.WhenAny(importStarted.Task, Task.Delay(500));

        // Assert: drain returns immediately (processedCount=1, no AI parse/commit)
        result.ProcessedCount.Should().Be(1);
        result.FailedCount.Should().Be(0);
        result.CreatedVocabCount.Should().Be(0);

        // Item removed from queue immediately (not waiting for import to finish)
        queue.List().Should().BeEmpty();

        // Video import was invoked (detached)
        videoImport.Verify(
            v => v.ImportFromUrlAsync(youtubeUrl, TestUserId, TargetLanguage),
            Times.Once);

        // ParseContentAsync and CommitImportAsync were NOT called (video path bypasses them)
        importService.Verify(
            s => s.ParseContentAsync(It.IsAny<ContentImportRequest>(), It.IsAny<CancellationToken>()),
            Times.Never);
        importService.Verify(
            s => s.CommitImportAsync(It.IsAny<ContentImportCommit>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }
}
