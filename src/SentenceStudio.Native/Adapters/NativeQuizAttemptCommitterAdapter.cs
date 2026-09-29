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

/// <summary>
/// Scores one approved, already-snapshotted quiz turn in a single SQLite
/// transaction. A separate, permanently InProgress ActivitySession is the
/// durable receipt: the existing partial unique index serializes
/// (owner, receipt activity, encoded launch context + turn) inserts.
/// The quiz's own pending ActivitySession is read, never overwritten here.
/// </summary>
public sealed class NativeQuizAttemptCommitterAdapter(
    IServiceScopeFactory scopes,
    IUserScopeProvider userScope,
    IPreferencesService preferences) : INativeQuizAttemptCommitter, INativeQuizCompletion
{
    internal const string ReceiptActivityType = "VocabularyReview.NativeAttemptReceipt.v1";
    private const string QuizSessionActivityType = "VocabularyReview";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<NativeQuizCommitReceipt?> FindAsync(
        string userId, string launchContextKey, string turnId,
        CancellationToken cancellationToken)
    {
        RequireRequest(userId, launchContextKey, turnId, cancellationToken);
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        RequireSqlite(db);

        // Keep all receipt, source-session, plan, progress, and context reads
        // coherent with concurrent writers. No other DbContext is resolved.
        await using var transaction = await db.Database.BeginTransactionAsync(
            IsolationLevel.Serializable, cancellationToken);
        var existing = await ReadReceiptAsync(
            db, userId, launchContextKey, turnId, cancellationToken);
        NativeQuizOwnerGuard.RequireActiveOwner(
            userId, userScope, preferences, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        NativeQuizOwnerGuard.RequireActiveOwner(
            userId, userScope, preferences, cancellationToken);
        return existing?.Receipt;
    }

    public async Task<NativeQuizCommitReceipt> CommitAsync(
        string userId, string launchContextKey, string turnId,
        VocabularyAttempt attempt, CancellationToken cancellationToken)
    {
        RequireRequest(userId, launchContextKey, turnId, cancellationToken);
        ArgumentNullException.ThrowIfNull(attempt);

        // VocabularyAttempt is mutable. Freeze every field before the first
        // await, and compare the entire payload against the persisted turn.
        var frozenAttempt = JsonSerializer.Deserialize<VocabularyAttempt>(
            JsonSerializer.Serialize(attempt, JsonOptions), JsonOptions)
            ?? throw new InvalidOperationException("Quiz attempt could not be captured.");
        if (frozenAttempt.UserId != userId)
            throw new UnauthorizedAccessException("Quiz attempt belongs to another owner.");

        await using var scope = scopes.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var db = services.GetRequiredService<ApplicationDbContext>();
        var localDate = services.GetRequiredService<IPlanDateContext>();
        RequireSqlite(db);

        // SQLite begins an immediate, non-deferred writer transaction. The
        // next caller re-reads the receipt only after this writer commits or
        // rolls back; a check in a different scope could never guarantee that.
        await using var transaction = await db.Database.BeginTransactionAsync(
            IsolationLevel.Serializable, cancellationToken);
        NativeQuizOwnerGuard.RequireActiveOwner(
            userId, userScope, preferences, cancellationToken);
        var existing = await ReadReceiptAsync(
            db, userId, launchContextKey, turnId, cancellationToken);
        if (existing is not null)
        {
            if (!SameAttempt(existing.State.Attempt, frozenAttempt))
                throw new InvalidOperationException(
                    "A committed quiz turn cannot be replayed with a different attempt.");
            NativeQuizOwnerGuard.RequireActiveOwner(
                userId, userScope, preferences, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            NativeQuizOwnerGuard.RequireActiveOwner(
                userId, userScope, preferences, cancellationToken);
            return existing.Receipt;
        }

        var source = await db.ActivitySessions.AsNoTracking()
            .SingleOrDefaultAsync(row => row.UserId == userId
                && row.ActivityType == QuizSessionActivityType
                && row.LaunchContextKey == launchContextKey
                && row.Status == ActivitySessionStatus.InProgress,
                cancellationToken)
            ?? throw new InvalidOperationException(
                "Quiz scoring requires a persisted, in-progress pending turn.");
        var stored = ReadStoredState(source.StateJson);
        if (stored.PlanDate != localDate.UserLocalDate)
            throw new InvalidOperationException(
                "Quiz scoring requires the approved current user-local plan day.");
        var approved = await RequireApprovedPendingAsync(
            db, userId, launchContextKey, turnId, stored, frozenAttempt,
            cancellationToken);

        var progress = await db.VocabularyProgresses
            .SingleOrDefaultAsync(row => row.UserId == userId
                && row.VocabularyWordId == frozenAttempt.VocabularyWordId,
                cancellationToken);
        if (progress is null)
        {
            var now = DateTime.Now;
            progress = new VocabularyProgress
            {
                UserId = userId,
                VocabularyWordId = frozenAttempt.VocabularyWordId,
                FirstSeenAt = now,
                LastPracticedAt = now,
                CreatedAt = now,
                UpdatedAt = now,
#pragma warning disable CS0618
                CurrentPhase = LearningPhase.Recognition,
#pragma warning restore CS0618
                ReviewInterval = 1,
                EaseFactor = 2.5f
            };
            db.VocabularyProgresses.Add(progress);
        }

        VocabularyProgressService.ApplyAttemptScoring(
            progress, frozenAttempt, DateTime.Now);
        // VocabularyProgressRepository.SaveAsync also refreshes UpdatedAt.
        progress.UpdatedAt = DateTime.Now;
        NativeQuizOwnerGuard.RequireActiveOwner(
            userId, userScope, preferences, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);

        var context = VocabularyProgressService.CreateLearningContext(
            progress.Id, frozenAttempt, DateTime.Now);
        // Match the source repository's new-context timestamp treatment.
        context.UpdatedAt = DateTime.Now;
        context.CreatedAt = DateTime.Now;
        db.VocabularyLearningContexts.Add(context);
        var nowUtc = DateTime.UtcNow;
        var journal = new StoredAttemptReceipt
        {
            UserId = userId,
            LaunchContextKey = launchContextKey,
            TurnId = turnId,
            PlanDate = stored.PlanDate,
            PlanId = approved.Plan.Id,
            PlanItemRowId = approved.Card.Id,
            SourceSessionId = source.Id,
            SourceStateJson = source.StateJson,
            Attempt = frozenAttempt,
            ScoredProgress = progress,
            LearningContextId = context.Id
        };
        db.ActivitySessions.Add(new ActivitySession
        {
            UserId = userId,
            ActivityType = ReceiptActivityType,
            LaunchContextKey = ReceiptKey(launchContextKey, turnId),
            StateJson = JsonSerializer.Serialize(journal, JsonOptions),
            Status = ActivitySessionStatus.InProgress,
            StartedAt = nowUtc,
            UpdatedAt = nowUtc
        });

        NativeQuizOwnerGuard.RequireActiveOwner(
            userId, userScope, preferences, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        // The source pending snapshot must still be the one validated under
        // the writer reservation; only the presenter may advance it later.
        var sourceJson = await db.ActivitySessions.AsNoTracking()
            .Where(row => row.Id == source.Id && row.UserId == userId
                && row.ActivityType == QuizSessionActivityType
                && row.LaunchContextKey == launchContextKey
                && row.Status == ActivitySessionStatus.InProgress)
            .Select(row => row.StateJson)
            .SingleOrDefaultAsync(cancellationToken);
        if (!string.Equals(sourceJson, source.StateJson, StringComparison.Ordinal)
            || localDate.UserLocalDate != stored.PlanDate)
            throw new InvalidOperationException(
                "The pending quiz turn or its local plan date changed before commit.");
        NativeQuizOwnerGuard.RequireActiveOwner(
            userId, userScope, preferences, cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        // A commit result is not success-shaped until it can be read back
        // with its progress and learning context from the same DB scope.
        var verified = (await ReadReceiptAsync(
            db, userId, launchContextKey, turnId, cancellationToken))?.Receipt
            ?? throw new InvalidOperationException(
                "The committed quiz attempt receipt could not be verified.");
        NativeQuizOwnerGuard.RequireActiveOwner(
            userId, userScope, preferences, cancellationToken);
        return verified;
    }

    private async Task<VerifiedReceipt?> ReadReceiptAsync(
        ApplicationDbContext db, string userId, string launchContextKey,
        string turnId, CancellationToken cancellationToken)
    {
        var row = await db.ActivitySessions.AsNoTracking()
            .SingleOrDefaultAsync(receipt => receipt.UserId == userId
                && receipt.ActivityType == ReceiptActivityType
                && receipt.LaunchContextKey == ReceiptKey(launchContextKey, turnId),
                cancellationToken);
        if (row is null)
            return null;
        // Changing the status would release the partial unique index. Never
        // treat such a row as an absent receipt and score another attempt.
        if (row.Status != ActivitySessionStatus.InProgress)
            throw new InvalidOperationException(
                "A quiz attempt receipt lost its permanent uniqueness reservation.");

        StoredAttemptReceipt journal;
        try
        {
            journal = JsonSerializer.Deserialize<StoredAttemptReceipt>(
                row.StateJson, JsonOptions)
                ?? throw new JsonException("Quiz attempt receipt is empty.");
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                "The persisted quiz attempt receipt is invalid.", ex);
        }

        if (journal.Version != 1 || journal.UserId != userId
            || journal.LaunchContextKey != launchContextKey || journal.TurnId != turnId
            || journal.SourceSessionId <= 0 || string.IsNullOrWhiteSpace(journal.PlanId)
            || string.IsNullOrWhiteSpace(journal.PlanItemRowId)
            || string.IsNullOrWhiteSpace(journal.LearningContextId)
            || journal.Attempt is null || journal.ScoredProgress is null)
            throw new InvalidOperationException("Quiz attempt receipt identity is invalid.");

        var source = await db.ActivitySessions.AsNoTracking().SingleOrDefaultAsync(
            session => session.Id == journal.SourceSessionId && session.UserId == userId
                && session.ActivityType == QuizSessionActivityType
                && session.LaunchContextKey == launchContextKey,
            cancellationToken)
            ?? throw new InvalidOperationException(
                "Quiz attempt receipt has no owned source session.");
        var captured = ReadStoredState(journal.SourceStateJson);
        if (captured.PlanDate != journal.PlanDate)
            throw new InvalidOperationException("Quiz attempt receipt plan date changed.");
        await RequireHistoricalPendingAsync(
            db, userId, launchContextKey, turnId, captured, journal,
            cancellationToken);

        var current = ReadStoredState(source.StateJson);
        if (current.UserId != userId || current.PlanDate != journal.PlanDate
            || current.LaunchContextKey != launchContextKey
            || current.Snapshot.PlanItemId != captured.Snapshot.PlanItemId
            || current.PlanItemRowId != captured.PlanItemRowId
            || VocabQuizSessionSnapshot.BuildLaunchContextKey(
                current.Snapshot.PlanItemId, current.Snapshot.FocusVocabularyIds,
                current.Snapshot.ResourceIds, current.Snapshot.DueOnly,
                current.Snapshot.SkillId) != launchContextKey
            || current.TurnId == turnId
                && (current.PendingAttempt is null && !current.SummaryVisible
                    || current.PendingAttempt is not null
                        && !SameAttempt(current.PendingAttempt, journal.Attempt)))
            throw new InvalidOperationException(
                "The quiz source session no longer agrees with its turn receipt.");

        var scored = journal.ScoredProgress;
        if (string.IsNullOrWhiteSpace(scored.Id) || scored.UserId != userId
            || scored.VocabularyWordId != journal.Attempt.VocabularyWordId
            || scored.TotalAttempts <= 0)
            throw new InvalidOperationException(
                "The quiz receipt contains no owned scored progress.");
        var persisted = await db.VocabularyProgresses.AsNoTracking()
            .SingleOrDefaultAsync(progress => progress.Id == scored.Id
                && progress.UserId == userId
                && progress.VocabularyWordId == scored.VocabularyWordId,
                cancellationToken);
        if (persisted is null || persisted.TotalAttempts < scored.TotalAttempts
            || persisted.CorrectAttempts < scored.CorrectAttempts)
            throw new InvalidOperationException(
                "The quiz receipt's scored progress is not persisted.");

        var context = await db.VocabularyLearningContexts.AsNoTracking()
            .SingleOrDefaultAsync(value => value.Id == journal.LearningContextId
                && value.VocabularyProgressId == persisted.Id, cancellationToken);
        if (context is null || !ContextMatches(context, journal.Attempt))
            throw new InvalidOperationException(
                "The quiz receipt's learning context is not persisted.");

        return new VerifiedReceipt(
            new NativeQuizCommitReceipt(turnId, scored), journal);
    }

    private static async Task<ApprovedPending> RequireApprovedPendingAsync(
        ApplicationDbContext db, string userId, string launchContextKey,
        string turnId, NativeQuizStoredState stored, VocabularyAttempt attempt,
        CancellationToken cancellationToken)
    {
        var snapshot = ValidatePendingTurn(
            userId, launchContextKey, turnId, stored, attempt);
        var dateKey = stored.PlanDate.ToDateTime(
            TimeOnly.MinValue, DateTimeKind.Utc);
        var plan = await db.DailyPlans.AsNoTracking()
            .SingleOrDefaultAsync(value => value.UserProfileId == userId
                && value.Date == dateKey, cancellationToken)
            ?? throw new InvalidOperationException(
                "Quiz scoring requires an owned plan for its local day.");
        // Archiving permits multiple historical rows for the same logical
        // item. Only the one active row can authorize a new attempt.
        var card = await db.DailyPlanCompletions.AsNoTracking()
            .SingleOrDefaultAsync(value => value.UserProfileId == userId
                && value.Date == dateKey
                && value.PlanItemId == snapshot.PlanItemId
                && !value.IsArchived, cancellationToken);
        if (card is null || card.ActivityType != QuizSessionActivityType
            || stored.PlanItemRowId != card.Id
            || card.IsCompleted || card.CompletedAt is not null
            || card.ResourceId != snapshot.ResourceIds.SingleOrDefault()
            || card.SkillId != snapshot.SkillId)
            throw new InvalidOperationException(
                "Quiz scoring requires its assigned, active plan card.");

        var focus = VocabQuizLaunchValidator.GetPlanFocusVocabularyIds(
            plan.FocusVocabularyFacts, plan.NarrativeFacts);
        if (snapshot.FocusVocabularyIds.Count != focus.Count
            || !snapshot.FocusVocabularyIds.ToHashSet(StringComparer.Ordinal)
                .SetEquals(focus))
            throw new InvalidOperationException(
                "Quiz snapshot does not match the plan's assigned vocabulary.");

        await RequireOwnedSnapshotAsync(
            db, userId, snapshot, attempt, card, focus, cancellationToken);
        return new ApprovedPending(plan, card);
    }

    private static async Task RequireHistoricalPendingAsync(
        ApplicationDbContext db, string userId, string launchContextKey,
        string turnId, NativeQuizStoredState captured, StoredAttemptReceipt journal,
        CancellationToken cancellationToken)
    {
        var snapshot = ValidatePendingTurn(
            userId, launchContextKey, turnId, captured, journal.Attempt);
        var dateKey = captured.PlanDate.ToDateTime(
            TimeOnly.MinValue, DateTimeKind.Utc);
        // A regenerated plan may change focus, archive the old card, and
        // replace its parent. The receipt's original row ID is the stable
        // evidence; the logical PlanItemId alone is not unique in history.
        var card = await db.DailyPlanCompletions.AsNoTracking()
            .SingleOrDefaultAsync(value => value.Id == journal.PlanItemRowId
                && value.UserProfileId == userId
                && value.Date == dateKey
                && value.PlanItemId == snapshot.PlanItemId, cancellationToken);
        if (card is null || card.ActivityType != QuizSessionActivityType
            || captured.PlanItemRowId != journal.PlanItemRowId
            || card.ResourceId != snapshot.ResourceIds.SingleOrDefault()
            || card.SkillId != snapshot.SkillId)
            throw new InvalidOperationException(
                "Quiz attempt receipt lost its original owned plan card.");

        // PlanId and SourceStateJson were captured at scoring time. The plan
        // parent is mutable and can be deleted by ResetToday; replay instead
        // corroborates that captured turn with its historical card, owned
        // vocabulary graph, persisted progress, and learning context.
        await RequireOwnedSnapshotAsync(
            db, userId, snapshot, journal.Attempt, card,
            snapshot.FocusVocabularyIds, cancellationToken);
    }

    private static VocabQuizSessionSnapshot ValidatePendingTurn(
        string userId, string launchContextKey, string turnId,
        NativeQuizStoredState stored, VocabularyAttempt attempt)
    {
        var snapshot = stored.Snapshot;
        if (stored.UserId != userId || stored.LaunchContextKey != launchContextKey
            || stored.TurnId != turnId || stored.SummaryVisible
            || stored.RequiresCorrection || stored.PendingAttempt is null
            || !SameAttempt(stored.PendingAttempt, attempt)
            || string.IsNullOrWhiteSpace(snapshot.PlanItemId)
            || snapshot.PlanItemId?.StartsWith("adhoc-", StringComparison.Ordinal) == true
            || snapshot.DueOnly
            || snapshot.FocusVocabularyIds is null || snapshot.ResourceIds is null
            || snapshot.BatchPool is null || snapshot.RoundWordOrder is null
            || snapshot.SessionItemsWordIds is null
            || snapshot.BatchPool.Count is < 1 or > 20
            || snapshot.BatchPool.Any(item => item is null
                || string.IsNullOrWhiteSpace(item.WordId))
            || snapshot.BatchPool.Select(item => item.WordId)
                .Distinct(StringComparer.Ordinal).Count() != snapshot.BatchPool.Count
            || snapshot.RoundWordOrder.Count is < 1 or > 10
            || snapshot.RoundWordOrder.Distinct(StringComparer.Ordinal).Count()
                != snapshot.RoundWordOrder.Count
            || snapshot.SessionItemsWordIds.Distinct(StringComparer.Ordinal).Count()
                != snapshot.SessionItemsWordIds.Count
            || snapshot.ResourceIds.Count > 1
            || snapshot.CurrentTurnInRound < 0
            || snapshot.CurrentTurnInRound >= snapshot.RoundWordOrder.Count
            || snapshot.UserMode is not ("Text" or "MultipleChoice")
            || snapshot.TotalTurns < 1 || snapshot.CorrectCount < 0
            || snapshot.CorrectCount > snapshot.TotalTurns
            || VocabQuizSessionSnapshot.BuildLaunchContextKey(
                snapshot.PlanItemId, snapshot.FocusVocabularyIds,
                snapshot.ResourceIds, snapshot.DueOnly,
                snapshot.SkillId) != launchContextKey
            || snapshot.RoundWordOrder[snapshot.CurrentTurnInRound]
                != attempt.VocabularyWordId
            || stored.DisplayDirection is not ("Mixed" or "NativeToTarget" or "TargetToNative")
            || stored.DisplayDirection == "NativeToTarget"
                && !snapshot.PromptUsesNativeLanguage
            || stored.DisplayDirection == "TargetToNative"
                && snapshot.PromptUsesNativeLanguage
            || attempt.UserId != userId || attempt.Activity != "VocabularyQuiz"
            || attempt.InputMode != snapshot.UserMode
            || attempt.ContextType != "Isolated"
            || attempt.LearningResourceId is not null
            || attempt.UserConfidence is not null
            || attempt.PenaltyOverride is not null
            || attempt.ResponseTimeMs < 0
            || string.IsNullOrWhiteSpace(attempt.UserInput)
            || string.IsNullOrWhiteSpace(attempt.ExpectedAnswer)
            || attempt.DifficultyWeight !=
                (attempt.InputMode == "Text" ? 1.5f : 1.0f))
            throw new InvalidOperationException(
                "Quiz attempt does not match a durable, approved pending turn.");

        return snapshot;
    }

    private static async Task RequireOwnedSnapshotAsync(
        ApplicationDbContext db, string userId,
        VocabQuizSessionSnapshot snapshot, VocabularyAttempt attempt,
        DailyPlanCompletion card, IReadOnlyList<string> focus,
        CancellationToken cancellationToken)
    {
        var resourceId = snapshot.ResourceIds.SingleOrDefault();
        if (resourceId is not null
            && !await db.LearningResources.AsNoTracking()
                .AnyAsync(resource => resource.Id == resourceId
                    && resource.UserProfileId == userId, cancellationToken))
            throw new InvalidOperationException(
                "Quiz plan resource is not owned by the active user.");
        if (snapshot.SkillId is not null
            && !await db.SkillProfiles.AsNoTracking()
                .AnyAsync(skill => skill.Id == snapshot.SkillId
                    && skill.UserProfileId == userId, cancellationToken))
            throw new InvalidOperationException(
                "Quiz plan skill is not owned by the active user.");

        var batchIds = snapshot.BatchPool.Select(item => item.WordId).ToArray();
        var reachable = await db.ResourceVocabularyMappings.AsNoTracking()
            .Join(db.LearningResources.AsNoTracking().Where(resource =>
                    resource.UserProfileId == userId
                    && (resourceId == null || resource.Id == resourceId)),
                mapping => mapping.ResourceId, resource => resource.Id,
                (mapping, resource) => mapping.VocabularyWordId)
            .Where(wordId => batchIds.Contains(wordId))
            .Distinct()
            .ToListAsync(cancellationToken);
        if (reachable.Count != batchIds.Length
            || focus.Count > 0 && batchIds.Any(id => !focus.Contains(
                id, StringComparer.Ordinal))
            || VocabQuizLaunchValidator.CountRejectedSnapshotReferences(
                snapshot, card.PlanItemId, focus,
                snapshot.ResourceIds, false, card.SkillId, reachable) != 0)
            throw new InvalidOperationException(
                "Quiz pending turn references vocabulary outside its approved owner graph.");

        var word = await db.VocabularyWords.AsNoTracking().SingleOrDefaultAsync(
            item => item.Id == attempt.VocabularyWordId, cancellationToken)
            ?? throw new InvalidOperationException("Quiz pending word is missing.");
        if (word.LexicalUnitType is LexicalUnitType.Phrase or LexicalUnitType.Sentence)
            throw new NotSupportedException(
                "Phrase and sentence attempts require the source constituent cascade.");
        var expected = snapshot.PromptUsesNativeLanguage
            ? word.TargetLanguageTerm : word.NativeLanguageTerm;
        if (string.IsNullOrWhiteSpace(expected)
            || !string.Equals(expected, attempt.ExpectedAnswer, StringComparison.Ordinal))
            throw new InvalidOperationException(
                "Quiz pending answer does not match the persisted word and direction.");
    }

    private void RequireRequest(
        string userId, string launchContextKey, string turnId,
        CancellationToken cancellationToken)
    {
        NativeQuizOwnerGuard.RequireActiveOwner(
            userId, userScope, preferences, cancellationToken);
        if (string.IsNullOrWhiteSpace(launchContextKey)
            || launchContextKey.Length > 2048
            || !Guid.TryParseExact(turnId, "N", out _))
            throw new InvalidOperationException(
                "Quiz receipt requires a valid launch context and stable turn ID.");
    }

    private static void RequireSqlite(ApplicationDbContext db)
    {
        if (!db.Database.IsSqlite())
            throw new NotSupportedException(
                "Native quiz attempt receipts require SQLite writer serialization.");
    }

    private static NativeQuizStoredState ReadStoredState(string json)
    {
        try
        {
            return NativeQuizStoredState.Deserialize(json);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                "The persisted quiz snapshot cannot be trusted.", ex);
        }
    }

    // JSON array escaping makes the pair injective even if a context contains
    // delimiters. The existing filtered unique index supplies the constraint.
    private static string ReceiptKey(string launchContextKey, string turnId) =>
        JsonSerializer.Serialize(new[] { launchContextKey, turnId }, JsonOptions);

    private static bool SameAttempt(VocabularyAttempt left, VocabularyAttempt right) =>
        string.Equals(JsonSerializer.Serialize(left, JsonOptions),
            JsonSerializer.Serialize(right, JsonOptions), StringComparison.Ordinal);

    private static bool ContextMatches(
        VocabularyLearningContext context, VocabularyAttempt attempt) =>
        context.Activity == attempt.Activity
        && context.InputMode == attempt.InputMode
        && context.WasCorrect == attempt.WasCorrect
        && context.DifficultyScore == attempt.DifficultyWeight
        && context.ResponseTimeMs == attempt.ResponseTimeMs
        && context.UserConfidence == attempt.UserConfidence
        && context.ContextType == attempt.ContextType
        && context.LearningResourceId == attempt.LearningResourceId
        && context.UserInput == attempt.UserInput
        && context.ExpectedAnswer == attempt.ExpectedAnswer
        && context.CorrectAnswersInContext == (attempt.WasCorrect ? 1 : 0);

    public async Task<bool> CompleteQuizAsync(
        NativeQuizLaunch launch, string launchContextKey, string planItemRowId,
        int minutesSpent,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(launch);
        NativeQuizOwnerGuard.RequireActiveOwner(
            launch.UserId, userScope, preferences, cancellationToken);
        if (launch.DueOnly || string.IsNullOrWhiteSpace(launch.PlanItemId)
            || string.IsNullOrWhiteSpace(planItemRowId)
            || string.IsNullOrWhiteSpace(launchContextKey)
            || launchContextKey != VocabQuizSessionSnapshot.BuildLaunchContextKey(
                launch.PlanItemId,
                launch.FocusVocabularyIds.IsDefault ? [] : launch.FocusVocabularyIds,
                launch.ResourceIds.IsDefault ? [] : launch.ResourceIds,
                launch.DueOnly, launch.SkillId))
            throw new InvalidOperationException("Quiz completion requires the assigned launch context.");

        await using var scope = scopes.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var db = services.GetRequiredService<ApplicationDbContext>();
        RequireSqlite(db);
        if (services.GetRequiredService<IPlanDateContext>().UserLocalDate != launch.PlanDate)
            return false;

        await using var transaction = await db.Database.BeginTransactionAsync(
            IsolationLevel.Serializable, cancellationToken);
        NativeQuizOwnerGuard.RequireActiveOwner(
            launch.UserId, userScope, preferences, cancellationToken);
        var dateKey = launch.PlanDate.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
        var plan = await db.DailyPlans.AsNoTracking().SingleOrDefaultAsync(
            row => row.UserProfileId == launch.UserId && row.Date == dateKey,
            cancellationToken);
        var card = await db.DailyPlanCompletions.SingleOrDefaultAsync(
            row => row.Id == planItemRowId
                && row.UserProfileId == launch.UserId && row.Date == dateKey
                && row.PlanItemId == launch.PlanItemId && !row.IsArchived,
            cancellationToken);
        var session = await db.ActivitySessions.SingleOrDefaultAsync(
            row => row.UserId == launch.UserId
                && row.ActivityType == QuizSessionActivityType
                && row.LaunchContextKey == launchContextKey
                && row.Status == ActivitySessionStatus.InProgress,
            cancellationToken);
        if (plan is null || card is null || session is null
            || card.ActivityType != QuizSessionActivityType
            || card.ResourceId != (launch.ResourceIds.IsDefaultOrEmpty
                ? null : launch.ResourceIds.SingleOrDefault())
            || card.SkillId != launch.SkillId
            || !VocabQuizLaunchValidator.GetPlanFocusVocabularyIds(
                    plan.FocusVocabularyFacts, plan.NarrativeFacts)
                .SequenceEqual(
                    launch.FocusVocabularyIds.IsDefault
                        ? [] : launch.FocusVocabularyIds, StringComparer.Ordinal))
            return false;

        var stored = ReadStoredState(session.StateJson);
        if (stored.UserId != launch.UserId || stored.PlanDate != launch.PlanDate
            || stored.LaunchContextKey != launchContextKey
            || stored.Snapshot.PlanItemId != launch.PlanItemId
            || stored.PlanItemRowId != planItemRowId
            || !stored.SummaryVisible || stored.PendingAttempt is not null
            || stored.Snapshot.TotalTurns <= 0
            || stored.Snapshot.RoundWordOrder is null
            || stored.Snapshot.RoundWordOrder.Count == 0
            || stored.Snapshot.CurrentTurnInRound != stored.Snapshot.RoundWordOrder.Count)
            return false;

        // A saved summary must follow a real committed turn, not an edited
        // snapshot that merely claims the session is over.
        var finalTurn = await ReadReceiptAsync(
            db, launch.UserId, launchContextKey, stored.TurnId, cancellationToken);
        if (finalTurn?.State.SourceSessionId != session.Id
            || finalTurn.State.PlanItemRowId != card.Id)
            return false;

        var now = DateTime.UtcNow;
        card.MinutesSpent = Math.Max(card.MinutesSpent,
            Math.Clamp(minutesSpent, 0, PlanService.MaxMinutesSpent));
        card.IsCompleted = true;
        card.CompletedAt ??= now;
        card.UpdatedAt = now;
        session.Status = ActivitySessionStatus.Completed;
        session.CompletedAt = now;
        session.UpdatedAt = now;

        NativeQuizOwnerGuard.RequireActiveOwner(
            launch.UserId, userScope, preferences, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        NativeQuizOwnerGuard.RequireActiveOwner(
            launch.UserId, userScope, preferences, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    public async Task<bool> CompleteAllMasteredAsync(
        NativeQuizLaunch launch, string planItemRowId, int minutesSpent,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(launch);
        NativeQuizOwnerGuard.RequireActiveOwner(
            launch.UserId, userScope, preferences, cancellationToken);
        if (launch.DueOnly || !launch.FocusVocabularyIds.IsDefaultOrEmpty
            || string.IsNullOrWhiteSpace(planItemRowId)
            || string.IsNullOrWhiteSpace(launch.PlanItemId)
            || (!launch.ResourceIds.IsDefault && launch.ResourceIds.Length > 1))
            return false;

        await using var scope = scopes.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var db = services.GetRequiredService<ApplicationDbContext>();
        RequireSqlite(db);
        if (services.GetRequiredService<IPlanDateContext>().UserLocalDate != launch.PlanDate)
            return false;

        await using var transaction = await db.Database.BeginTransactionAsync(
            IsolationLevel.Serializable, cancellationToken);
        NativeQuizOwnerGuard.RequireActiveOwner(
            launch.UserId, userScope, preferences, cancellationToken);
        var dateKey = launch.PlanDate.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
        var plan = await db.DailyPlans.AsNoTracking().SingleOrDefaultAsync(
            row => row.UserProfileId == launch.UserId && row.Date == dateKey,
            cancellationToken);
        var card = await db.DailyPlanCompletions.SingleOrDefaultAsync(
            row => row.Id == planItemRowId
                && row.UserProfileId == launch.UserId && row.Date == dateKey
                && row.PlanItemId == launch.PlanItemId && !row.IsArchived,
            cancellationToken);
        var resourceId = launch.ResourceIds.IsDefaultOrEmpty
            ? null : launch.ResourceIds[0];
        var contextKey = VocabQuizSessionSnapshot.BuildLaunchContextKey(
            launch.PlanItemId, Array.Empty<string>(),
            launch.ResourceIds.IsDefault
                ? Array.Empty<string>() : launch.ResourceIds, false, launch.SkillId);
        if (plan is null || card is null
            || card.ActivityType != QuizSessionActivityType
            || card.ResourceId != resourceId || card.SkillId != launch.SkillId
            || VocabQuizLaunchValidator.GetPlanFocusVocabularyIds(
                plan.FocusVocabularyFacts, plan.NarrativeFacts).Count != 0
            || await db.ActivitySessions.AsNoTracking().AnyAsync(
                session => session.UserId == launch.UserId
                    && session.ActivityType == QuizSessionActivityType
                    && session.LaunchContextKey == contextKey
                    && session.Status == ActivitySessionStatus.InProgress,
                cancellationToken))
            return false;

        var ownedWords = await db.VocabularyWords.AsNoTracking()
            .Where(word => db.ResourceVocabularyMappings.Any(mapping =>
                mapping.VocabularyWordId == word.Id
                && db.LearningResources.Any(resource =>
                    resource.Id == mapping.ResourceId
                    && resource.UserProfileId == launch.UserId
                    && (resourceId == null || resource.Id == resourceId))))
            .ToListAsync(cancellationToken);
        var usable = ownedWords.Where(FocusVocabularySelection.IsUsableVocabularyWord)
            .Select(word => word.Id).ToList();
        if (usable.Count == 0)
            return false;
        var progress = await db.VocabularyProgresses.AsNoTracking()
            .Where(row => row.UserId == launch.UserId
                && usable.Contains(row.VocabularyWordId))
            .ToListAsync(cancellationToken);
        if (progress.Count != usable.Count || progress.Any(row => !row.IsKnown))
            return false;

        var now = DateTime.UtcNow;
        card.MinutesSpent = Math.Max(card.MinutesSpent,
            Math.Clamp(minutesSpent, 0, PlanService.MaxMinutesSpent));
        card.IsCompleted = true;
        card.CompletedAt ??= now;
        card.UpdatedAt = now;
        NativeQuizOwnerGuard.RequireActiveOwner(
            launch.UserId, userScope, preferences, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        NativeQuizOwnerGuard.RequireActiveOwner(
            launch.UserId, userScope, preferences, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    private sealed record ApprovedPending(
        DailyPlan Plan, DailyPlanCompletion Card);

    private sealed record VerifiedReceipt(
        NativeQuizCommitReceipt Receipt, StoredAttemptReceipt State);

    private sealed record StoredAttemptReceipt
    {
        public StoredAttemptReceipt() { }

        public int Version { get; init; } = 1;
        public required string UserId { get; init; }
        public required string LaunchContextKey { get; init; }
        public required string TurnId { get; init; }
        public required DateOnly PlanDate { get; init; }
        public required string PlanId { get; init; }
        public required string PlanItemRowId { get; init; }
        public required int SourceSessionId { get; init; }
        public required string SourceStateJson { get; init; }
        public required VocabularyAttempt Attempt { get; init; }
        public required VocabularyProgress ScoredProgress { get; init; }
        public required string LearningContextId { get; init; }
    }
}

internal interface INativeQuizCompletion
{
    Task<bool> CompleteQuizAsync(
        NativeQuizLaunch launch, string launchContextKey, string planItemRowId,
        int minutesSpent,
        CancellationToken cancellationToken);

    Task<bool> CompleteAllMasteredAsync(
        NativeQuizLaunch launch, string planItemRowId, int minutesSpent,
        CancellationToken cancellationToken);
}
