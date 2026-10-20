using System.Collections.Immutable;
using System.Data;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SentenceStudio.Abstractions;
using SentenceStudio.Data;
using SentenceStudio.Services;
using SentenceStudio.Services.Plans;
using SentenceStudio.Shared.Models;
using SentenceStudio.Shared.Services;

namespace SentenceStudio.Native.Adapters;

/// <summary>Shares the isolated-turn writer's SQLite serialization, scorer and receipt index.</summary>
public sealed class NativeQuizSentenceAttemptCommitter(
    IServiceScopeFactory scopes,
    IUserScopeProvider userScope,
    IPreferencesService preferences) : INativeQuizSentenceCommitter
{
    internal const string ReceiptActivityType = "VocabularyReview.NativeSentenceReceipt.v1";
    private const string QuizActivityType = "VocabularyReview";
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    public async Task<NativeQuizSentenceCommitReceipt?> FindAsync(
        string userId, string launchContextKey, string turnId,
        string submissionId, CancellationToken cancellationToken)
    {
        RequireRequest(userId, launchContextKey, turnId, submissionId, cancellationToken);
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        RequireSqlite(db);
        await using var transaction = await db.Database.BeginTransactionAsync(
            IsolationLevel.Serializable, cancellationToken);
        var found = await ReadReceiptAsync(
            db, userId, launchContextKey, turnId, submissionId, cancellationToken);
        NativeQuizOwnerGuard.RequireActiveOwner(
            userId, userScope, preferences, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return found;
    }

    public async Task<NativeQuizSentenceCommitReceipt> CommitAsync(
        string userId, string launchContextKey, string turnId,
        string submissionId, IReadOnlyList<NativeQuizSentenceGrade> results,
        CancellationToken cancellationToken)
    {
        RequireRequest(userId, launchContextKey, turnId, submissionId, cancellationToken);
        ArgumentNullException.ThrowIfNull(results);
        var frozen = JsonSerializer.Deserialize<NativeQuizSentenceGrade[]>(
            JsonSerializer.Serialize(results, JsonOptions), JsonOptions)
            ?? throw new InvalidOperationException("Sentence results could not be captured.");

        await using var scope = scopes.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var db = services.GetRequiredService<ApplicationDbContext>();
        RequireSqlite(db);
        await using var transaction = await db.Database.BeginTransactionAsync(
            IsolationLevel.Serializable, cancellationToken);
        NativeQuizOwnerGuard.RequireActiveOwner(
            userId, userScope, preferences, cancellationToken);
        var existing = await ReadReceiptAsync(
            db, userId, launchContextKey, turnId, submissionId, cancellationToken);
        if (existing is not null)
        {
            if (!SameResults(existing.Results, frozen))
                throw new InvalidOperationException(
                    "A sentence submission cannot be replayed with changed results.");
            await transaction.CommitAsync(cancellationToken);
            return existing;
        }

        var source = await db.ActivitySessions.AsNoTracking()
            .SingleOrDefaultAsync(row => row.UserId == userId
                && row.ActivityType == QuizActivityType
                && row.LaunchContextKey == launchContextKey
                && row.Status == ActivitySessionStatus.InProgress,
                cancellationToken)
            ?? throw new InvalidOperationException(
                "Sentence scoring needs an in-progress owner-bound quiz session.");
        var stored = NativeQuizAttemptCommitterAdapter.ReadStoredState(source.StateJson);
        var word = await ValidatePendingAsync(
            db, services.GetRequiredService<IPlanDateContext>(),
            userId, launchContextKey, turnId, submissionId, stored, frozen,
            cancellationToken);

        VocabularyProgress? progress = null;
        var contextIds = new List<string>();
        foreach (var result in frozen.Where(result => result.UsageCorrect
            && !result.GradeUnavailable))
        {
            progress ??= await db.VocabularyProgresses.SingleOrDefaultAsync(
                row => row.UserId == userId && row.VocabularyWordId == word.Id,
                cancellationToken);
            if (progress is null)
            {
                progress = NativeQuizAttemptCommitterAdapter.CreateNewProgress(userId, word.Id);
                db.VocabularyProgresses.Add(progress);
            }
            var attempt = new VocabularyAttempt
            {
                VocabularyWordId = word.Id,
                UserId = userId,
                Activity = "VocabularyQuiz",
                InputMode = "Text",
                WasCorrect = true,
                DifficultyWeight = 2.5f,
                ContextType = "Sentence",
                UserInput = result.Sentence,
                ExpectedAnswer = word.TargetLanguageTerm,
                ResponseTimeMs = 0
            };
            var now = DateTime.Now;
            VocabularyProgressService.ApplyAttemptScoring(progress, attempt, now);
            if (progress.IsFamiliar)
            {
                // Match the source's post-attempt familiar verification transition.
                progress.VerificationState = VerificationStatus.Confirmed;
                progress.MasteryScore = 1f;
                progress.CurrentStreak = 7f;
                progress.ProductionInStreak =
                    VocabularyMasteryCalculator.MIN_PRODUCTION_FOR_KNOWN;
                progress.MasteredAt = now;
                progress.ReviewInterval = 60;
                progress.EaseFactor = 2.5f;
                progress.NextReviewDate = now.AddDays(60);
                progress.LastPracticedAt = now;
            }
            progress.UpdatedAt = now;
            await NativeQuizAttemptCommitterAdapter.RecordDirectConstituentExposuresAsync(
                db, userId, word, attempt, cancellationToken);
            var context = VocabularyProgressService.CreateLearningContext(
                progress.Id, attempt, now);
            context.CreatedAt = now;
            context.UpdatedAt = now;
            db.VocabularyLearningContexts.Add(context);
            contextIds.Add(context.Id);
        }

        var nowUtc = DateTime.UtcNow;
        db.ActivitySessions.Add(new ActivitySession
        {
            UserId = userId,
            ActivityType = ReceiptActivityType,
            LaunchContextKey = ReceiptKey(launchContextKey, turnId, submissionId),
            StateJson = JsonSerializer.Serialize(new SentenceReceiptJournal(
                userId, launchContextKey, turnId, submissionId,
                source.Id, source.StateJson, word.Id, frozen, progress?.Id,
                contextIds), JsonOptions),
            Status = ActivitySessionStatus.InProgress,
            StartedAt = nowUtc,
            UpdatedAt = nowUtc
        });
        NativeQuizOwnerGuard.RequireActiveOwner(
            userId, userScope, preferences, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        NativeQuizOwnerGuard.RequireActiveOwner(
            userId, userScope, preferences, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return await ReadReceiptAsync(db, userId, launchContextKey, turnId,
                submissionId, cancellationToken)
            ?? throw new InvalidOperationException(
                "Committed sentence results have no verifiable receipt.");
    }

    private static async Task<VocabularyWord> ValidatePendingAsync(
        ApplicationDbContext db, IPlanDateContext localDate, string owner,
        string launchContextKey, string turnId, string submissionId,
        NativeQuizStoredState stored, IReadOnlyList<NativeQuizSentenceGrade> results,
        CancellationToken cancellationToken)
    {
        var snapshot = stored.Snapshot;
        var entries = VocabQuizSentenceUsagePolicy.SplitEntries(
            stored.SentenceSourceInput ?? string.Empty);
        if (stored.UserId != owner || stored.LaunchContextKey != launchContextKey
            || stored.TurnId != turnId
            || stored.SentenceSubmissionId != submissionId
            || stored.SentenceCreditApplied || stored.PendingAttempt is not null
            || stored.SummaryVisible || results.Count == 0
            || snapshot.BatchPool is null || snapshot.RoundWordOrder is null
            || snapshot.CurrentTurnInRound < 0
            || snapshot.CurrentTurnInRound >= snapshot.RoundWordOrder.Count
            || stored.SentenceResults is null
            || !SameResults(stored.SentenceResults, results)
            || !entries.SequenceEqual(results.Select(result => result.Sentence),
                StringComparer.Ordinal)
            || VocabQuizSessionSnapshot.BuildLaunchContextKey(
                snapshot.PlanItemId, snapshot.FocusVocabularyIds,
                snapshot.ResourceIds, snapshot.DueOnly, snapshot.SkillId)
                != launchContextKey
            || stored.PlanDate != localDate.UserLocalDate)
            throw new InvalidOperationException(
                "Sentence results do not match an approved durable quiz submission.");

        var wordId = snapshot.RoundWordOrder[snapshot.CurrentTurnInRound];
        if (!snapshot.BatchPool.Any(item => item.WordId == wordId)
            || snapshot.ResourceIds.Any(string.IsNullOrWhiteSpace)
            || snapshot.ResourceIds.Distinct(StringComparer.Ordinal).Count()
                != snapshot.ResourceIds.Count)
            throw new InvalidOperationException(
                "Sentence word or resources are outside the approved quiz batch.");

        if (!string.IsNullOrWhiteSpace(snapshot.PlanItemId))
        {
            var date = stored.PlanDate.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
            var plan = await db.DailyPlans.AsNoTracking().SingleOrDefaultAsync(
                row => row.UserProfileId == owner && row.Date == date,
                cancellationToken);
            var card = await db.DailyPlanCompletions.AsNoTracking().SingleOrDefaultAsync(
                row => row.UserProfileId == owner && row.Date == date
                    && row.PlanItemId == snapshot.PlanItemId && !row.IsArchived,
                cancellationToken);
            var focus = VocabQuizLaunchValidator.GetPlanFocusVocabularyIds(
                plan?.FocusVocabularyFacts, plan?.NarrativeFacts);
            if (plan is null || card is null || snapshot.ResourceIds.Count > 1
                || card.Id != stored.PlanItemRowId
                || card.ActivityType != QuizActivityType
                || card.IsCompleted != stored.IsRepractice
                || card.IsCompleted != (card.CompletedAt is not null)
                || card.ResourceId != snapshot.ResourceIds.SingleOrDefault()
                || card.SkillId != snapshot.SkillId
                || !focus.ToHashSet(StringComparer.Ordinal)
                    .SetEquals(snapshot.FocusVocabularyIds))
                throw new InvalidOperationException(
                    "Sentence scoring needs its current, owned plan context.");
        }
        else
        {
            var date = stored.PlanDate.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
            var adHoc = await db.DailyPlanCompletions.AsNoTracking()
                .SingleOrDefaultAsync(row => row.Id == stored.PlanItemRowId
                    && row.UserProfileId == owner && row.Date == date
                    && row.PlanItemId == stored.AdHocPlanItemId
                    && row.PlanItemId.StartsWith("adhoc-")
                    && row.ActivityType == QuizActivityType
                    && !row.IsArchived, cancellationToken);
            if (adHoc is null
                || adHoc.ResourceId != (snapshot.DueOnly || snapshot.ResourceIds.Count != 1
                    ? null : snapshot.ResourceIds[0])
                || adHoc.SkillId != snapshot.SkillId)
                throw new InvalidOperationException(
                    "Sentence scoring needs its owned direct activity row.");
        }

        if (snapshot.SkillId is not null
            && !await db.SkillProfiles.AsNoTracking().AnyAsync(
                row => row.Id == snapshot.SkillId && row.UserProfileId == owner,
                cancellationToken))
            throw new InvalidOperationException("Sentence skill is not owned.");
        var ownedResources = db.LearningResources.AsNoTracking()
            .Where(row => row.UserProfileId == owner);
        if (await ownedResources.CountAsync(
                row => snapshot.ResourceIds.Contains(row.Id), cancellationToken)
            != snapshot.ResourceIds.Count)
            throw new InvalidOperationException("Sentence resource is not owned.");
        var reachable = await db.ResourceVocabularyMappings.AsNoTracking()
            .Join(ownedResources, mapping => mapping.ResourceId, resource => resource.Id,
                (mapping, resource) => new { mapping.VocabularyWordId, resource.Id })
            .Where(row => snapshot.ResourceIds.Count == 0
                || string.IsNullOrWhiteSpace(snapshot.PlanItemId)
                    && (snapshot.DueOnly || snapshot.FocusVocabularyIds.Count > 0)
                || snapshot.ResourceIds.Contains(row.Id))
            .Select(row => row.VocabularyWordId)
            .Distinct()
            .ToArrayAsync(cancellationToken);
        if (VocabQuizLaunchValidator.CountRejectedSnapshotReferences(
                snapshot, snapshot.PlanItemId, snapshot.FocusVocabularyIds,
                snapshot.ResourceIds, snapshot.DueOnly, snapshot.SkillId, reachable) != 0)
            throw new InvalidOperationException(
                "Sentence batch contains unowned vocabulary.");
        var word = await db.VocabularyWords.AsNoTracking().SingleOrDefaultAsync(
            row => row.Id == wordId, cancellationToken);
        if (word is null || !FocusVocabularySelection.IsUsableVocabularyWord(word))
            throw new InvalidOperationException(
                "Sentence target word is unavailable.");
        var targetLanguage = await db.UserProfiles.AsNoTracking()
            .Where(row => row.Id == owner)
            .Select(row => row.TargetLanguage)
            .SingleOrDefaultAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(targetLanguage)
            || results.Any(result => result.UsageCorrect
                && (result.GradeUnavailable
                    || !VocabQuizSentenceUsagePolicy.HasVerifiedTargetUse(
                        result.Sentence, word, targetLanguage,
                        result.TargetUsedForm))))
            throw new InvalidOperationException(
                "Sentence credit requires target-language use present in the submitted sentence.");
        return word;
    }

    private async Task<NativeQuizSentenceCommitReceipt?> ReadReceiptAsync(
        ApplicationDbContext db, string userId, string contextKey, string turnId,
        string submissionId, CancellationToken cancellationToken)
    {
        var row = await db.ActivitySessions.AsNoTracking().SingleOrDefaultAsync(
            receipt => receipt.UserId == userId
                && receipt.ActivityType == ReceiptActivityType
                && receipt.LaunchContextKey == ReceiptKey(contextKey, turnId, submissionId),
            cancellationToken);
        if (row is null)
            return null;
        if (row.Status != ActivitySessionStatus.InProgress)
            throw new InvalidOperationException(
                "Sentence receipt lost its permanent uniqueness reservation.");
        var journal = JsonSerializer.Deserialize<SentenceReceiptJournal>(
            row.StateJson, JsonOptions)
            ?? throw new InvalidOperationException("Sentence receipt is empty.");
        if (journal.Version != 1 || journal.UserId != userId
            || journal.ContextKey != contextKey || journal.TurnId != turnId
            || journal.SubmissionId != submissionId
            || journal.SourceSessionId <= 0
            || journal.Results is null || journal.Results.Length == 0
            || journal.ContextIds is null
            || journal.ContextIds.Count != journal.Results.Count(result =>
                result.UsageCorrect && !result.GradeUnavailable)
            || (journal.ContextIds.Count > 0) != (journal.ProgressId is not null))
            throw new InvalidOperationException("Sentence receipt identity is invalid.");
        var source = await db.ActivitySessions.AsNoTracking().AnyAsync(
            session => session.Id == journal.SourceSessionId
                && session.UserId == userId
                && session.ActivityType == QuizActivityType
                && session.LaunchContextKey == contextKey,
            cancellationToken);
        var captured = NativeQuizAttemptCommitterAdapter.ReadStoredState(
            journal.SourceStateJson);
        if (!source || captured.UserId != userId
            || captured.SentenceSubmissionId != submissionId
            || !SameResults(captured.SentenceResults, journal.Results)
            || captured.Snapshot.RoundWordOrder[captured.Snapshot.CurrentTurnInRound]
                != journal.WordId)
            throw new InvalidOperationException(
                "Sentence receipt has no matching owned quiz submission.");
        var savedContexts = await db.VocabularyLearningContexts.AsNoTracking()
            .Where(context => journal.ContextIds.Contains(context.Id)
                && context.VocabularyProgressId == journal.ProgressId
                && context.Activity == "VocabularyQuiz"
                && context.InputMode == "Text"
                && context.ContextType == "Sentence" && context.WasCorrect)
            .Select(context => context.UserInput)
            .ToListAsync(cancellationToken);
        if (savedContexts.Count != journal.ContextIds.Count
            || !savedContexts.ToHashSet(StringComparer.Ordinal)
                .SetEquals(journal.Results.Where(result =>
                    result.UsageCorrect && !result.GradeUnavailable)
                    .Select(result => result.Sentence)))
            throw new InvalidOperationException(
                "Sentence receipt lost its scored learning contexts.");
        VocabularyProgress? progress = null;
        if (journal.ProgressId is not null)
        {
            progress = await db.VocabularyProgresses.AsNoTracking().SingleOrDefaultAsync(
                item => item.Id == journal.ProgressId && item.UserId == userId
                    && item.VocabularyWordId == journal.WordId,
                cancellationToken);
            if (progress is null)
                throw new InvalidOperationException(
                    "Sentence receipt lost its owned progress row.");
        }
        return new NativeQuizSentenceCommitReceipt(
            submissionId, journal.ContextIds.Count,
            journal.Results.ToImmutableArray(), progress);
    }

    private void RequireRequest(
        string userId, string contextKey, string turnId,
        string submissionId, CancellationToken cancellationToken)
    {
        NativeQuizOwnerGuard.RequireActiveOwner(
            userId, userScope, preferences, cancellationToken);
        if (string.IsNullOrWhiteSpace(contextKey) || contextKey.Length > 2048
            || !Guid.TryParseExact(turnId, "N", out _)
            || !Guid.TryParseExact(submissionId, "N", out _))
            throw new InvalidOperationException(
                "Sentence receipt needs a valid launch, turn and submission identity.");
    }

    private static string ReceiptKey(
        string contextKey, string turnId, string submissionId) =>
        JsonSerializer.Serialize(new[] { contextKey, turnId, submissionId }, JsonOptions);

    private static bool SameResults(
        IReadOnlyList<NativeQuizSentenceGrade> left,
        IReadOnlyList<NativeQuizSentenceGrade> right) =>
        JsonSerializer.Serialize(left, JsonOptions) ==
        JsonSerializer.Serialize(right, JsonOptions);

    private static void RequireSqlite(ApplicationDbContext db)
    {
        if (!db.Database.IsSqlite())
            throw new NotSupportedException(
                "Sentence receipts require the existing SQLite writer serialization.");
    }

    private sealed record SentenceReceiptJournal(
        string UserId, string ContextKey, string TurnId,
        string SubmissionId, int SourceSessionId, string SourceStateJson,
        string WordId, NativeQuizSentenceGrade[] Results, string? ProgressId,
        IReadOnlyList<string> ContextIds)
    {
        public int Version { get; init; } = 1;
    }
}
