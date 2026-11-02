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
        string userProfileId,
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
                ? await _db.NumberCounters
                    .FirstOrDefaultAsync(c => c.Counter == progress.CounterId, ct)
                : null;

            var itemRequest = new NumberItemRequest(
                ContextCode: context.Code,
                SubModeCode: request.SubModeCode,
                Bucket: progress.Bucket,
                CounterId: counter?.Counter, // Pass counter code (e.g., "잔"), not GUID
                Difficulty: request.Difficulty ?? 1,
                RandomSeed: Random.Shared.Next()
            );

            var item = _generator.GenerateItem(itemRequest);
            items.Add(item);
        }

        // If not enough due items, pad with random unseen items
        if (items.Count < request.ItemCount)
        {
            var contextsQuery = _db.NumberContexts.Where(c => c.IsActive);
            
            // Respect ContextCode filter from request
            if (!string.IsNullOrEmpty(request.ContextCode))
            {
                contextsQuery = contextsQuery.Where(c => c.Code == request.ContextCode);
            }
            
            var contexts = await contextsQuery.ToListAsync(ct);

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
                    CounterId: counter?.Counter, // Pass counter code (e.g., "잔"), not GUID
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
            "📐 NumberDrill session started: user={UserId} subMode={SubMode} context={Context} itemCount={ItemCount} dueOnly={DueOnly}",
            userProfileId, request.SubModeCode, request.ContextCode ?? "Any", items.Count, request.DueOnly);

        return session;
    }

    public async Task<NumberAttemptResult> SubmitAnswerAsync(
        string userProfileId,
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
        var prior = new NumberSm2State(progress.EaseFactor, progress.Interval, progress.Repetitions, progress.DueDate);

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

        var masteryDelta = gradeResult.IsCorrect ? 1 : -1;
        _logger.LogInformation(
            "📐 NumberDrill graded: user={UserId} subMode={SubMode} context={Context} bucket={Bucket} " +
            "correct={Correct} latencyMs={LatencyMs} masteryDelta={Delta} nextDue={DueDate:yyyy-MM-dd}",
            userProfileId, item.SubModeCode, item.ContextCode, item.Bucket,
            gradeResult.IsCorrect, latencyMs, masteryDelta, progress.DueDate);

        var receipt = new NumberAttemptReceipt(
            attempt.Id,
            progress.Id,
            prior,
            new NumberSm2State(progress.EaseFactor, progress.Interval, progress.Repetitions, progress.DueDate),
            progress.LastReviewed,
            progress.CorrectCount,
            progress.TotalCount);

        return new NumberAttemptResult(gradeResult, snapshot, receipt);
    }

    /// <summary>
    /// "I was correct": re-grades one stored attempt as correct for its owner.
    /// The attempt becomes IsCorrect = true; its ErrorClass is kept so grader
    /// misses stay diagnosable (a correct attempt with an ErrorClass is an
    /// override). The mastery row gains one correct answer, and its SM-2 state
    /// is restored to the pre-attempt values in <paramref name="receipt"/> and
    /// re-applied with the correct-answer quality for the attempt's latency, so
    /// the row ends as if the attempt had been graded correct.
    /// <para>
    /// Refused with no write when the owner is absent; the attempt or mastery
    /// row is missing or belongs to another owner; the receipt does not describe
    /// a wrong grade of that row; or the row has changed since that attempt.
    /// A second override of the same attempt is a no-op (AlreadyCorrect).
    /// </para>
    /// </summary>
    public async Task<NumberOverrideResult> OverrideAttemptAsync(
        string userProfileId,
        NumberAttemptReceipt receipt,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        if (string.IsNullOrWhiteSpace(userProfileId))
        {
            _logger.LogWarning("NumberDrill override refused: no active owner.");
            return NumberOverrideResult.Refused;
        }

        var attempt = await _db.NumberAttempts.FirstOrDefaultAsync(a =>
            a.Id == receipt.AttemptId && a.UserProfileId == userProfileId, ct);
        if (attempt == null)
        {
            _logger.LogWarning(
                "NumberDrill override refused: attempt {AttemptId} not found for user {UserId}.",
                receipt.AttemptId, userProfileId);
            return NumberOverrideResult.Refused;
        }
        // A context that stored the attempt still tracks it; read the stored row.
        await _db.Entry(attempt).ReloadAsync(ct);

        var progress = await _db.NumberMasteryProgresses.FirstOrDefaultAsync(p =>
            p.Id == receipt.MasteryProgressId &&
            p.UserProfileId == userProfileId &&
            p.LanguageCode == attempt.LanguageCode &&
            p.ContextCode == attempt.ContextCode &&
            p.CounterId == attempt.CounterId &&
            p.System == attempt.System &&
            p.Bucket == attempt.Bucket, ct);
        if (progress != null)
            await _db.Entry(progress).ReloadAsync(ct);

        if (attempt.IsCorrect)
        {
            _logger.LogInformation(
                "NumberDrill override ignored: attempt {AttemptId} is already correct.", attempt.Id);
            return new NumberOverrideResult(NumberOverrideOutcome.AlreadyCorrect,
                progress == null ? null : Snapshot(progress));
        }

        if (progress == null)
        {
            _logger.LogWarning(
                "NumberDrill override refused: mastery row {ProgressId} not found for attempt {AttemptId}.",
                receipt.MasteryProgressId, attempt.Id);
            return NumberOverrideResult.Refused;
        }

        // The receipt must describe the wrong grade this attempt applied ...
        var wrong = Sm2Scheduler.Update(receipt.Prior.EaseFactor, receipt.Prior.Interval,
            receipt.Prior.Repetitions, ComputeSm2Quality(false, attempt.LatencyMs));
        var receiptConsistent = wrong.EaseFactor == receipt.After.EaseFactor
            && wrong.Interval == receipt.After.Interval
            && wrong.Repetitions == receipt.After.Repetitions;
        // ... and the row must still be exactly as that attempt left it.
        var unchanged = progress.EaseFactor == receipt.After.EaseFactor
            && progress.Interval == receipt.After.Interval
            && progress.Repetitions == receipt.After.Repetitions
            && SameInstant(progress.DueDate, receipt.After.DueDate)
            && SameInstant(progress.LastReviewed, receipt.LastReviewed)
            && progress.CorrectCount == receipt.CorrectCount
            && progress.TotalCount == receipt.TotalCount;
        if (!receiptConsistent || !unchanged)
        {
            _logger.LogWarning(
                "NumberDrill override refused: mastery row {ProgressId} changed since attempt {AttemptId} " +
                "(receiptConsistent={ReceiptConsistent} unchanged={Unchanged}).",
                progress.Id, attempt.Id, receiptConsistent, unchanged);
            return NumberOverrideResult.Refused;
        }

        var correct = Sm2Scheduler.Update(receipt.Prior.EaseFactor, receipt.Prior.Interval,
            receipt.Prior.Repetitions, ComputeSm2Quality(true, attempt.LatencyMs));
        attempt.IsCorrect = true;
        progress.CorrectCount++;
        progress.EaseFactor = correct.EaseFactor;
        progress.Interval = correct.Interval;
        progress.Repetitions = correct.Repetitions;
        // Due from the attempt's review time, as the original correct grade would have been.
        progress.DueDate = (progress.LastReviewed ?? DateTime.UtcNow).AddDays(correct.Interval);

        await _db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "NumberDrill override applied: user={UserId} attempt={AttemptId} context={Context} " +
            "bucket={Bucket} errorClass={ErrorClass} nextDue={DueDate:yyyy-MM-dd}",
            userProfileId, attempt.Id, attempt.ContextCode, attempt.Bucket,
            attempt.ErrorClass ?? "(none)", progress.DueDate);

        return new NumberOverrideResult(NumberOverrideOutcome.Applied, Snapshot(progress));
    }

    private static MasteryProgressSnapshot Snapshot(NumberMasteryProgress progress) =>
        new(progress.CorrectCount, progress.TotalCount, progress.MedianLatencyMs, progress.DueDate);

    // PostgreSQL keeps microseconds; compare stored instants to the millisecond.
    private static bool SameInstant(DateTime? a, DateTime? b) =>
        a is null || b is null
            ? a is null && b is null
            : Math.Abs((a.Value - b.Value).TotalMilliseconds) < 1;

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
    MasteryProgressSnapshot Snapshot,
    NumberAttemptReceipt? Receipt = null
);

/// <summary>SM-2 scheduling values of a mastery row.</summary>
public record NumberSm2State(
    double EaseFactor,
    int Interval,
    int Repetitions,
    DateTime DueDate
);

/// <summary>
/// What one submit stored: the attempt, its mastery row, the row's SM-2 state
/// before (<see cref="Prior"/>) and after (<see cref="After"/>) the grade, and
/// the row's post-grade review time and counts. Pass it back to
/// <see cref="NumberSessionService.OverrideAttemptAsync"/>.
/// </summary>
public record NumberAttemptReceipt(
    string AttemptId,
    string MasteryProgressId,
    NumberSm2State Prior,
    NumberSm2State After,
    DateTime? LastReviewed,
    int CorrectCount,
    int TotalCount
);

public enum NumberOverrideOutcome
{
    Applied,
    AlreadyCorrect,
    Refused
}

public record NumberOverrideResult(
    NumberOverrideOutcome Outcome,
    MasteryProgressSnapshot? Snapshot)
{
    public static NumberOverrideResult Refused { get; } = new(NumberOverrideOutcome.Refused, null);
}

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
