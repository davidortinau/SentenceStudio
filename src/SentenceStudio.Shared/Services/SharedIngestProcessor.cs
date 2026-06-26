using Microsoft.Extensions.Logging;
using SentenceStudio.Sharing;
using SentenceStudio.Shared.Models;

namespace SentenceStudio.Services;

// ---------------------------------------------------------------------------
// Tiny interface so the "find Shared Inbox" resource look-up is fakeable in
// unit tests without pulling in the full LearningResourceRepository DI graph.
// The concrete implementation is in LearningResourceSharedInboxFinder below.
// ---------------------------------------------------------------------------

/// <summary>
/// Locates (or indicates absence of) the "Shared Inbox" LearningResource for
/// a given user.  Injected into <see cref="SharedIngestProcessor"/> so tests
/// can supply a simple fake without wiring up the real EF/DI stack.
/// </summary>
public interface ISharedInboxResourceFinder
{
    /// <summary>
    /// Returns the "Shared Inbox" <see cref="SentenceStudio.Shared.Models.LearningResource"/>
    /// owned by <paramref name="userId"/>, or <c>null</c> if it does not yet exist.
    /// </summary>
    Task<SentenceStudio.Shared.Models.LearningResource?> FindSharedInboxAsync(
        string userId, CancellationToken ct = default);
}

// ---------------------------------------------------------------------------
// Tiny interface so the active-user profile look-up is fakeable in unit tests
// without pulling in the full UserProfileRepository DI graph.
// The concrete implementation is UserProfileActiveProvider below.
// ---------------------------------------------------------------------------

/// <summary>
/// Fetches the currently active user profile.
/// Injected into <see cref="SharedIngestProcessor"/> so tests can supply a
/// simple fake without wiring up the real EF/DI stack.
/// </summary>
public interface IActiveUserProfileProvider
{
    /// <summary>
    /// Returns the active <see cref="SentenceStudio.Shared.Models.UserProfile"/>,
    /// or <c>null</c> when no user is signed in.
    /// </summary>
    Task<SentenceStudio.Shared.Models.UserProfile?> GetActiveAsync(CancellationToken ct = default);
}

// ---------------------------------------------------------------------------
// Result returned by DrainAsync
// ---------------------------------------------------------------------------

// ---------------------------------------------------------------------------
// Drain gate — injectable singleton so tests can isolate instances
// ---------------------------------------------------------------------------

/// <summary>
/// Process-wide single-flight guard for <see cref="SharedIngestProcessor.DrainAsync"/>.
/// Register as a Singleton in DI so all scoped processor instances share one gate.
/// Tests supply their own instance per test, eliminating static cross-contamination.
/// </summary>
public sealed class SharedIngestDrainGate
{
    private int _busy;

    /// <summary>Returns true and acquires the gate if it was idle; false if already held.</summary>
    public bool TryEnter() => System.Threading.Interlocked.CompareExchange(ref _busy, 1, 0) == 0;

    /// <summary>Releases the gate.</summary>
    public void Exit() => System.Threading.Interlocked.Exchange(ref _busy, 0);
}

/// <summary>
/// Summary counts from a single <see cref="SharedIngestProcessor.DrainAsync"/> run.
/// </summary>
public sealed class SharedIngestDrainResult
{
    /// <summary>Number of items that were fully processed (text + fetched URL items).</summary>
    public int ProcessedCount { get; init; }

    /// <summary>Total vocabulary words created across all processed items.</summary>
    public int CreatedVocabCount { get; init; }

    /// <summary>Total vocabulary words skipped (already exist) across all processed items.</summary>
    public int SkippedVocabCount { get; init; }

    /// <summary>
    /// Items that produced zero usable content (empty AI parse, or URL fetch succeeded
    /// but returned no readable text); removed from queue so they do not retry forever.
    /// </summary>
    public int EmptyCount { get; init; }

    /// <summary>Items that failed with an exception; left in queue for retry.</summary>
    public int FailedCount { get; init; }

    /// <summary>
    /// True only when the drain was skipped because no active user is set.
    /// All items remain queued for the next foreground cycle.
    /// </summary>
    public bool Deferred { get; init; }

    /// <summary>
    /// The LearningResource id that was used (or created) as "Shared Inbox"
    /// during this drain.  Null when no items were processed.
    /// </summary>
    public string? ResourceId { get; init; }
}

// ---------------------------------------------------------------------------
// Interface
// ---------------------------------------------------------------------------

/// <summary>
/// Drains the <see cref="ISharedIngestQueue"/>, converts shared text items
/// into vocabulary via <see cref="IContentImportService"/>, and appends them
/// to the user's "Shared Inbox" <c>LearningResource</c>.
/// </summary>
public interface ISharedIngestProcessor
{
    /// <summary>
    /// Processes all pending items in the shared ingest queue for the currently
    /// active user.  Safe to call on every foreground event — concurrent calls
    /// are collapsed to a single in-flight drain via a single-flight guard.
    /// </summary>
    Task<SharedIngestDrainResult> DrainAsync(CancellationToken ct = default);
}

// ---------------------------------------------------------------------------
// Implementation
// ---------------------------------------------------------------------------

/// <summary>
/// Platform-agnostic orchestrator that drains the <see cref="ISharedIngestQueue"/>
/// and saves vocabulary to the active user's "Shared Inbox" resource.
/// </summary>
/// <remarks>
/// Designed to be registered as a Scoped or Transient service.  All dependencies
/// are injected so the class is fully unit-testable with fakes.
/// </remarks>
public sealed class SharedIngestProcessor : ISharedIngestProcessor
{
    /// <summary>The exact title used to find or create the Shared Inbox resource.</summary>
    public const string SharedInboxResourceTitle = "Shared Inbox";

    private readonly ISharedIngestQueue _queue;
    private readonly IContentImportService _importService;
    private readonly ISharedInboxResourceFinder _inboxFinder;
    private readonly IWebArticleFetcher _webFetcher;
    private readonly IVideoImportPipeline _videoImport;
    private readonly IActiveUserProfileProvider _profileProvider;
    private readonly SharedIngestNotifier _notifier;
    private readonly SharedIngestDrainGate _gate;
    private readonly ILogger<SharedIngestProcessor> _logger;

    public SharedIngestProcessor(
        ISharedIngestQueue queue,
        IContentImportService importService,
        ISharedInboxResourceFinder inboxFinder,
        IWebArticleFetcher webFetcher,
        IVideoImportPipeline videoImport,
        IActiveUserProfileProvider profileProvider,
        SharedIngestNotifier notifier,
        SharedIngestDrainGate gate,
        ILogger<SharedIngestProcessor> logger)
    {
        _queue = queue;
        _importService = importService;
        _inboxFinder = inboxFinder;
        _webFetcher = webFetcher;
        _videoImport = videoImport;
        _profileProvider = profileProvider;
        _notifier = notifier;
        _gate = gate;
        _logger = logger;
    }

    /// <inheritdoc/>
    public async Task<SharedIngestDrainResult> DrainAsync(CancellationToken ct = default)
    {
        // --- 1. Single-flight guard: if a drain is already running, return immediately ---
        if (!_gate.TryEnter())
        {
            _logger.LogDebug("SharedIngestProcessor: drain already in progress; skipping.");
            return new SharedIngestDrainResult();
        }

        try
        {
            return await DrainCoreAsync(ct);
        }
        finally
        {
            _gate.Exit();
        }
    }

    private async Task<SharedIngestDrainResult> DrainCoreAsync(CancellationToken ct)
    {
        // --- 2. Auth/tenant gate ---
        var userProfile = await _profileProvider.GetActiveAsync(ct);
        if (userProfile is null || string.IsNullOrEmpty(userProfile.Id))
        {
            var queuedCount = _queue.List().Count;
            _logger.LogInformation(
                "SharedIngestProcessor: no active user; deferring {Count} shared item(s).", queuedCount);
            return new SharedIngestDrainResult { Deferred = true };
        }

        var userId = userProfile.Id;

        // --- 3. List items; snapshot so Remove() calls during iteration are safe ---
        var items = _queue.List().ToList();
        if (items.Count == 0)
            return new SharedIngestDrainResult();

        // --- Language from user profile ---
        var targetLanguage = string.IsNullOrWhiteSpace(userProfile.TargetLanguage)
            ? "Korean" : userProfile.TargetLanguage;
        var nativeLanguage = string.IsNullOrWhiteSpace(userProfile.NativeLanguage)
            ? "English" : userProfile.NativeLanguage;

        // --- 4. Resolve "Shared Inbox" resource (may be null — created on first commit) ---
        var existingInbox = await _inboxFinder.FindSharedInboxAsync(userId, ct);
        string? sharedInboxId = existingInbox?.Id;

        // Accumulators
        int processedCount = 0, createdVocab = 0, skippedVocab = 0,
            emptyCount = 0, failedCount = 0;

        // Track the last successfully-processed item's completion info for the notifier.
        SharedIngestNotificationKind lastKind = SharedIngestNotificationKind.Vocabulary;
        int lastCount = 0;
        string? lastTitle = null;
        string? lastRoute = "/vocabulary";
        bool anyProcessed = false;

        // --- 5. Process each item ---
        foreach (var item in items)
        {
            ct.ThrowIfCancellationRequested();

            if (item.Kind == SentenceStudio.Sharing.SharedIngestKind.Url)
            {
                // Detect URL type before processing so we can show the right spinner
                var notifKind = IsYouTubeUrl(item.Payload)
                    ? SharedIngestNotificationKind.VideoImportStarted
                    : SharedIngestNotificationKind.ResourceImported;

                _notifier.SetProcessing(notifKind);

                var outcome = await ProcessUrlItemAsync(
                    item, userId, targetLanguage, nativeLanguage, ct);

                switch (outcome.Status)
                {
                    case UrlItemStatus.Processed:
                        processedCount++;
                        createdVocab += outcome.CreatedCount;
                        lastKind = outcome.NotificationKind;
                        lastCount = outcome.CreatedCount;
                        lastTitle = outcome.CompletionTitle;
                        lastRoute = outcome.NotificationRoute;
                        anyProcessed = true;
                        break;
                    case UrlItemStatus.Empty:
                        emptyCount++;
                        break;
                    case UrlItemStatus.Failed:
                        failedCount++;
                        break;
                }
                continue;
            }

            // Text item path
            _notifier.SetProcessing(SharedIngestNotificationKind.Vocabulary);

            try
            {
                var preview = await _importService.ParseSharedTextAsync(
                    item.Payload, targetLanguage, nativeLanguage, ct);

                if (preview.Rows.Count == 0)
                {
                    _logger.LogInformation(
                        "SharedIngestProcessor: item {Id} yielded zero rows after parse — removing.", item.Id);
                    _queue.Remove(item.Id);
                    emptyCount++;
                    processedCount++;
                    continue;
                }

                var target = sharedInboxId != null
                    ? new ImportTarget
                    {
                        Mode = ImportTargetMode.Existing,
                        ExistingResourceId = sharedInboxId,
                        TargetLanguage = targetLanguage,
                        NativeLanguage = nativeLanguage
                    }
                    : new ImportTarget
                    {
                        Mode = ImportTargetMode.New,
                        NewResourceTitle = SharedInboxResourceTitle,
                        NewResourceDescription = "Items shared to SentenceStudio",
                        TargetLanguage = targetLanguage,
                        NativeLanguage = nativeLanguage
                    };

                var commit = new ContentImportCommit
                {
                    Preview = preview,
                    Target = target,
                    DedupMode = DedupMode.Skip,
                    HarvestWords = true,
                    HarvestPhrases = true,
                    HarvestSentences = true
                };

                var result = await _importService.CommitImportAsync(commit, ct);

                if (sharedInboxId == null && !string.IsNullOrEmpty(result.ResourceId))
                    sharedInboxId = result.ResourceId;

                createdVocab += result.CreatedCount;
                skippedVocab += result.SkippedCount;
                processedCount++;

                lastKind = SharedIngestNotificationKind.Vocabulary;
                lastCount = result.CreatedCount;
                lastTitle = null;
                lastRoute = "/vocabulary";
                anyProcessed = true;

                _queue.Remove(item.Id);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(
                    ex,
                    "SharedIngestProcessor: failed to process shared item {Id}; leaving in queue for retry.",
                    item.Id);
                failedCount++;
            }
        }

        var drainResult = new SharedIngestDrainResult
        {
            ProcessedCount = processedCount,
            CreatedVocabCount = createdVocab,
            SkippedVocabCount = skippedVocab,
            EmptyCount = emptyCount,
            FailedCount = failedCount,
            Deferred = false,
            ResourceId = sharedInboxId
        };

        // Only notify UI if something was actually processed
        if (anyProcessed)
            _notifier.SetCompleted(lastKind, lastCount, lastTitle, lastRoute);

        return drainResult;
    }

    /// <summary>
    /// Returns true when <paramref name="url"/> is a valid YouTube video URL.
    /// Uses the same VideoId.Parse approach as <see cref="YouTubeImportService"/>.
    /// </summary>
    private static bool IsYouTubeUrl(string url)
    {
        try
        {
            YoutubeExplode.Videos.VideoId.Parse(url);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Handles a single URL ingest item.
    /// YouTube URLs kick off the video import pipeline detached and return immediately.
    /// Article URLs fetch readable text and import as a new LearningResource (Transcript type).
    /// </summary>
    private async Task<UrlItemOutcome> ProcessUrlItemAsync(
        SharedIngestItem item,
        string userId,
        string targetLanguage,
        string nativeLanguage,
        CancellationToken ct)
    {
        var url = item.Payload;

        // ── YouTube path ──────────────────────────────────────────────────────────
        if (IsYouTubeUrl(url))
        {
            // Fire and forget — VideoImportPipelineService manages its own scope.
            _ = Task.Run(async () =>
            {
                try
                {
                    await _videoImport.ImportFromUrlAsync(url, userId, targetLanguage);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "SharedIngestProcessor: shared video import failed for {Url}", url);
                }
            });

            _queue.Remove(item.Id);

            return UrlItemOutcome.YouTube();
        }

        // ── Article path ──────────────────────────────────────────────────────────
        WebArticleText article;
        try
        {
            article = await _webFetcher.FetchReadableTextAsync(url, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(
                ex,
                "SharedIngestProcessor: exception while fetching URL item {Id} ({Url}); leaving in queue.",
                item.Id, url);
            return UrlItemOutcome.Failed();
        }

        if (!article.Succeeded)
        {
            _logger.LogWarning(
                "SharedIngestProcessor: URL item {Id} fetch failed — {Warning}; leaving in queue for retry.",
                item.Id, article.Warning);
            return UrlItemOutcome.Failed();
        }

        if (string.IsNullOrWhiteSpace(article.Text))
        {
            _logger.LogInformation(
                "SharedIngestProcessor: URL item {Id} ({Url}) yielded no readable text — removing.",
                item.Id, url);
            _queue.Remove(item.Id);
            return UrlItemOutcome.Empty();
        }

        try
        {
            var request = new ContentImportRequest
            {
                RawText = article.Text,
                ContentType = ContentType.Transcript,
                TargetLanguage = targetLanguage,
                NativeLanguage = nativeLanguage,
                HarvestTranscript = true,
                HarvestWords = true,
                HarvestPhrases = true
            };

            var preview = await _importService.ParseContentAsync(request, ct);

            var resourceTitle = !string.IsNullOrWhiteSpace(article.Title)
                ? article.Title
                : new Uri(url).Host;

            var target = new ImportTarget
            {
                Mode = ImportTargetMode.New,
                NewResourceTitle = resourceTitle,
                NewResourceDescription = $"Imported from shared link: {url}",
                TargetLanguage = targetLanguage,
                NativeLanguage = nativeLanguage
            };

            var commit = new ContentImportCommit
            {
                Preview = preview,
                Target = target,
                DedupMode = DedupMode.Skip,
                TranscriptText = article.Text,
                HarvestTranscript = true,
                HarvestWords = true,
                HarvestPhrases = true
            };

            var result = await _importService.CommitImportAsync(commit, ct);

            _queue.Remove(item.Id);
            return UrlItemOutcome.Article(result.CreatedCount, resourceTitle);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(
                ex,
                "SharedIngestProcessor: failed to import article URL item {Id}; leaving in queue for retry.",
                item.Id);
            return UrlItemOutcome.Failed();
        }
    }

    // Discriminated-union-style result for ProcessUrlItemAsync
    private readonly record struct UrlItemOutcome(
        UrlItemStatus Status,
        int CreatedCount,
        string? CompletionTitle,
        SharedIngestNotificationKind NotificationKind,
        string NotificationRoute)
    {
        public static UrlItemOutcome YouTube() =>
            new(UrlItemStatus.Processed, 0, null,
                SharedIngestNotificationKind.VideoImportStarted, "/media-import");

        public static UrlItemOutcome Article(int created, string? title) =>
            new(UrlItemStatus.Processed, created, title,
                SharedIngestNotificationKind.ResourceImported, "/vocabulary");

        public static UrlItemOutcome Failed() =>
            new(UrlItemStatus.Failed, 0, null,
                SharedIngestNotificationKind.Vocabulary, "/vocabulary");

        public static UrlItemOutcome Empty() =>
            new(UrlItemStatus.Empty, 0, null,
                SharedIngestNotificationKind.Vocabulary, "/vocabulary");
    }

    private enum UrlItemStatus { Processed, Failed, Empty }
}

// ---------------------------------------------------------------------------
// Production implementation of ISharedInboxResourceFinder
// ---------------------------------------------------------------------------

/// <summary>
/// Production implementation that queries <see cref="SentenceStudio.Data.LearningResourceRepository"/>
/// for the "Shared Inbox" resource belonging to the active user.
/// Registered in DI alongside the iOS App Group wiring in a later slice.
/// </summary>
public sealed class LearningResourceSharedInboxFinder : ISharedInboxResourceFinder
{
    private readonly SentenceStudio.Data.LearningResourceRepository _repo;

    public LearningResourceSharedInboxFinder(SentenceStudio.Data.LearningResourceRepository repo)
        => _repo = repo;

    /// <inheritdoc/>
    public async Task<SentenceStudio.Shared.Models.LearningResource?> FindSharedInboxAsync(
        string userId, CancellationToken ct = default)
    {
        var all = await _repo.GetAllResourcesLightweightAsync(userProfileId: userId);
        return all.FirstOrDefault(r =>
            string.Equals(r.Title, SharedIngestProcessor.SharedInboxResourceTitle,
                StringComparison.Ordinal));
    }
}

// ---------------------------------------------------------------------------
// Production implementation of IActiveUserProfileProvider
// ---------------------------------------------------------------------------

/// <summary>
/// Production implementation that delegates to <see cref="SentenceStudio.Data.UserProfileRepository"/>
/// for the currently active user profile.  Registered in DI in CoreServiceExtensions.
/// </summary>
public sealed class UserProfileActiveProvider : IActiveUserProfileProvider
{
    private readonly SentenceStudio.Data.UserProfileRepository _repo;

    public UserProfileActiveProvider(SentenceStudio.Data.UserProfileRepository repo)
        => _repo = repo;

    /// <inheritdoc/>
    public async Task<SentenceStudio.Shared.Models.UserProfile?> GetActiveAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return await _repo.GetAsync();
    }
}
