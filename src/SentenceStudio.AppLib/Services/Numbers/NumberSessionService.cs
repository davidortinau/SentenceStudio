using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SentenceStudio.Data;
using SentenceStudio.Services.Spaced;
using SentenceStudio.Shared.Models.Numbers;

namespace SentenceStudio.Services.Numbers;

public class NumberSessionService
{
    private readonly INumberItemGenerator _generator;
    private readonly INumberAnswerGrader _grader;
    private readonly ApplicationDbContext _db;
    private readonly ILogger<NumberSessionService> _logger;
    
    // In-memory session storage for Phase 1 (sessions not persisted)
    private readonly Dictionary<Guid, NumberSession> _activeSessions = new();

    public NumberSessionService(
        INumberItemGenerator generator,
        INumberAnswerGrader grader,
        ApplicationDbContext db,
        ILogger<NumberSessionService> logger)
    {
        _generator = generator;
        _grader = grader;
        _db = db;
        _logger = logger;
    }

    public async Task<NumberSession> StartSessionAsync(
        int userProfileId,
        string languageCode,
        NumberSessionRequest request,
        CancellationToken ct = default)
    {
        var sessionId = Guid.NewGuid();
        var items = new List<NumberItem>();

        // Query mastery progress to find items
        var progressQuery = _db.NumberMasteryProgresses
            .Where(p => p.UserProfileId == userProfileId && p.LanguageCode == languageCode);

        if (!string.IsNullOrEmpty(request.ContextCode))
        {
            progressQuery = progressQuery.Where(p => p.ContextCode == request.ContextCode);
        }

        if (request.DueOnly)
        {
            progressQuery = progressQuery.Where(p => p.DueDate <= DateTime.UtcNow);
        }

        var dueProgress = await progressQuery.ToListAsync(ct);

        // Generate items from due progress records
        foreach (var progress in dueProgress.Take(request.ItemCount))
        {
            var context = await _db.NumberContexts
                .FirstOrDefaultAsync(c => c.Code == progress.ContextCode, ct);
                
            if (context == null) continue;

            var counter = progress.CounterId != null
                ? await _db.NumberCounters.FindAsync(new object[] { progress.CounterId }, ct)
                : null;

            var itemRequest = new NumberItemRequest(
                ContextCode: context.Code,
                SubModeCode: request.SubModeCode,
                Bucket: progress.Bucket,
                CounterId: counter?.Id,
                Difficulty: request.Difficulty ?? 1,
                RandomSeed: Random.Shared.Next()
            );

            var item = _generator.GenerateItem(itemRequest);
            items.Add(item);
        }

        // If not enough due items, pad with random unseen items
        if (items.Count < request.ItemCount)
        {
            var contexts = await _db.NumberContexts
                .Where(c => c.IsActive)
                .ToListAsync(ct);

            var counters = await _db.NumberCounters
                .Where(c => c.LanguageCode == languageCode)
                .ToListAsync(ct);

            var needed = request.ItemCount - items.Count;
            var buckets = new[] { "1-10", "11-99" };

            for (int i = 0; i < needed && contexts.Count > 0; i++)
            {
                var context = contexts[Random.Shared.Next(contexts.Count)];
                var system = context.DefaultSystem;
                var bucket = buckets[Random.Shared.Next(buckets.Length)];
                var counter = counters.Count > 0 
                    ? counters[Random.Shared.Next(counters.Count)]
                    : null;

                var itemRequest = new NumberItemRequest(
                    ContextCode: context.Code,
                    SubModeCode: request.SubModeCode,
                    Bucket: bucket,
                    CounterId: counter?.Id,
                    Difficulty: request.Difficulty ?? 1,
                    RandomSeed: Random.Shared.Next()
                );

                var item = _generator.GenerateItem(itemRequest);
                items.Add(item);
            }
        }

        var session = new NumberSession(sessionId, items.AsReadOnly(), request, DateTime.UtcNow);
        _activeSessions[sessionId] = session;

        _logger.LogInformation(
            "Started number session {SessionId} for user {UserProfileId}: {ItemCount} items, DueOnly={DueOnly}",
            sessionId, userProfileId, items.Count, request.DueOnly);

        return session;
    }

    public async Task<NumberAttemptResult> SubmitAnswerAsync(
        int userProfileId,
        Guid sessionId,
        NumberItem item,
        string userAnswer,
        int latencyMs,
        CancellationToken ct = default)
    {
        // Grade the answer
        var gradeResult = _grader.Grade(item, userAnswer, latencyMs);

        // Persist attempt
        var attempt = new NumberAttempt
        {
            Id = Guid.NewGuid().ToString(),
            UserProfileId = userProfileId,
            LanguageCode = "ko", // TODO: get from session
            ContextCode = item.ContextCode,
            SubModeCode = item.SubModeCode,
            CounterId = item.CounterId,
            System = item.System,
            Bucket = item.Bucket,
            PromptValue = item.DisplayPrompt,
            ExpectedAnswer = item.CanonicalAnswer,
            UserAnswer = userAnswer,
            IsCorrect = gradeResult.IsCorrect,
            ErrorClass = gradeResult.ErrorClass,
            LatencyMs = latencyMs,
            AttemptedAt = DateTime.UtcNow
        };

        _db.NumberAttempts.Add(attempt);

        // Find or create mastery progress record
        var progress = await _db.NumberMasteryProgresses
            .FirstOrDefaultAsync(p =>
                p.UserProfileId == userProfileId &&
                p.LanguageCode == "ko" &&
                p.ContextCode == item.ContextCode &&
                p.CounterId == item.CounterId &&
                p.System == item.System &&
                p.Bucket == item.Bucket,
                ct);

        if (progress == null)
        {
            progress = new NumberMasteryProgress
            {
                Id = Guid.NewGuid().ToString(),
                UserProfileId = userProfileId,
                LanguageCode = "ko",
                ContextCode = item.ContextCode,
                CounterId = item.CounterId,
                System = item.System,
                Bucket = item.Bucket,
                CorrectCount = 0,
                TotalCount = 0,
                MedianLatencyMs = 0,
                EaseFactor = 2.5,
                Interval = 1,
                Repetitions = 0,
                DueDate = DateTime.UtcNow
            };
            _db.NumberMasteryProgresses.Add(progress);
        }

        // Update counts
        progress.TotalCount++;
        if (gradeResult.IsCorrect)
        {
            progress.CorrectCount++;
        }

        // Update median latency (running mean approximation)
        // NOTE: This is a simplified "mean" stored as "MedianLatencyMs" for Phase 1.
        // Phase 2+ should track a proper median or percentile distribution.
        if (progress.TotalCount == 1)
        {
            progress.MedianLatencyMs = latencyMs;
        }
        else
        {
            progress.MedianLatencyMs = (progress.MedianLatencyMs * (progress.TotalCount - 1) + latencyMs) / progress.TotalCount;
        }

        // Compute SM-2 quality from correctness and latency
        int sm2Quality = ComputeSm2Quality(gradeResult.IsCorrect, latencyMs);

        // Update SM-2 schedule
        var sm2Result = Sm2Scheduler.Update(
            progress.EaseFactor,
            progress.Interval,
            progress.Repetitions,
            sm2Quality);

        progress.EaseFactor = sm2Result.EaseFactor;
        progress.Interval = sm2Result.Interval;
        progress.Repetitions = sm2Result.Repetitions;
        progress.DueDate = sm2Result.DueDate;
        progress.LastReviewed = DateTime.UtcNow;

        await _db.SaveChangesAsync(ct);

        var snapshot = new MasteryProgressSnapshot(
            progress.CorrectCount,
            progress.TotalCount,
            progress.MedianLatencyMs,
            progress.DueDate
        );

        _logger.LogInformation(
            "Number attempt recorded: User={UserId}, Context={Context}, Bucket={Bucket}, " +
            "Correct={Correct}, Latency={Latency}ms, NextDue={DueDate:yyyy-MM-dd}",
            userProfileId, item.ContextCode, item.Bucket,
            gradeResult.IsCorrect, latencyMs, progress.DueDate);

        return new NumberAttemptResult(gradeResult, snapshot);
    }

    public Task<NumberSessionSummary> EndSessionAsync(Guid sessionId, CancellationToken ct = default)
    {
        if (!_activeSessions.TryGetValue(sessionId, out var session))
        {
            throw new InvalidOperationException($"Session {sessionId} not found");
        }

        // For Phase 1, we don't have attempt tracking in memory,
        // so we return a minimal summary. Phase 2 will track attempts
        // per session for richer summaries.
        var summary = new NumberSessionSummary(
            SessionId: sessionId,
            TotalItems: session.Items.Count,
            CorrectCount: 0,  // TODO: track in-session
            MedianLatencyMs: 0,  // TODO: track in-session
            ErrorClassBreakdown: new Dictionary<string, int>()
        );

        _activeSessions.Remove(sessionId);

        _logger.LogInformation("Ended number session {SessionId}", sessionId);

        return Task.FromResult(summary);
    }

    // Latency-to-SM2-quality mapping
    // Phase 1: simple thresholds based on automaticity research (Segalowitz)
    private static int ComputeSm2Quality(bool isCorrect, int latencyMs)
    {
        if (!isCorrect)
        {
            // Incorrect answers: 1 (complete failure) or 2 (minor error with partial recall)
            // For now, all incorrect = 1. Phase 2 can distinguish based on ErrorClass.
            return 1;
        }

        // Correct answers: quality based on latency
        // 5 = perfect (fast), 4 = good (acceptable), 3 = passing (slow but correct)
        return latencyMs switch
        {
            <= 8000 => 5,   // Fluent (automaticity threshold)
            <= 15000 => 4,  // Acceptable
            _ => 3          // Slow but correct
        };
    }
}

// Request/Response DTOs
public record NumberSessionRequest(
    string? ContextCode,
    string SubModeCode,
    int ItemCount = 10,
    bool DueOnly = false,
    int? Difficulty = null
);

public record NumberSession(
    Guid SessionId,
    IReadOnlyList<NumberItem> Items,
    NumberSessionRequest Request,
    DateTime StartedAtUtc
);

public record NumberAttemptResult(
    GradeResult Grade,
    MasteryProgressSnapshot Snapshot
);

public record MasteryProgressSnapshot(
    int CorrectCount,
    int TotalCount,
    int MedianLatencyMs,
    DateTime NextDueDate
);

public record NumberSessionSummary(
    Guid SessionId,
    int TotalItems,
    int CorrectCount,
    int MedianLatencyMs,
    Dictionary<string, int> ErrorClassBreakdown
);
