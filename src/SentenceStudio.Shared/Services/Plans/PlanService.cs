using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;
using SentenceStudio.Contracts.Plans;
using SentenceStudio.Data;
using SentenceStudio.Services.PlanGeneration;
using SentenceStudio.Services.Progress;
using SentenceStudio.Shared.Models;

namespace SentenceStudio.Services.Plans;

/// <summary>
/// Default <see cref="IPlanService"/> implementation. Owns the canonical
/// CRUD path for <see cref="DailyPlan"/> + <see cref="DailyPlanCompletion"/>
/// rows so the Flutter HTTP client and the in-process MAUI Blazor (v2 flag)
/// client share one source of truth.
/// </summary>
/// <remarks>
/// v1 scope (Phase A of the daily-plan server-contract refactor):
/// <list type="bullet">
///   <item><description><b>Persistence + progress + reset</b> are fully
///   wired against <see cref="ApplicationDbContext"/>.</description></item>
///   <item><description><b>Generate</b> resolves
///   <see cref="IDeterministicPlanGenerator"/> from DI when available
///   (in-process MAUI Blazor path) and falls back to a stub plan when the
///   generator hasn't been wired (HTTP API path, pending the Phase B repo
///   refactor — see <c>plan.md §7</c>).</description></item>
///   <item><description><b>Streak</b> is computed by walking
///   <see cref="DailyPlanCompletion"/> backwards from today; consecutive
///   user-local days with at least one <c>IsCompleted=true</c> row count
///   toward the streak.</description></item>
///   <item><description><b>Plan-item ids</b> reuse
///   <see cref="PlanConverter.GeneratePlanItemId(DateTime, PlanActivityType, string?, string?)"/>
///   so HTTP-generated and CoreSync-synced rows collide on the same
///   <c>(UserProfileId, Date, PlanItemId)</c> unique index.</description></item>
/// </list>
/// </remarks>
/// <remarks>
/// <b>CoreSync interaction (v1 acceptance):</b> the per-item merge rules
/// (<c>MinutesSpent = max</c>, <c>IsCompleted = OR</c>, earliest non-null
/// <c>CompletedAt</c>) are applied inside this service on every HTTP write,
/// and crucially every write is monotonic — minutes only ever move forward,
/// <c>IsCompleted</c> can flip true but never false. CoreSync conflict
/// resolution at the row level is last-writer-wins (see
/// <c>SyncService.SynchronizeAsync</c>), which means if a MAUI device and
/// the HTTP API both write to the same <c>(UserProfileId, Date,
/// PlanItemId)</c> row within one sync window the larger of the two
/// counters wins by virtue of the monotonic update rule, even though
/// CoreSync itself doesn't compose them. The
/// <c>coresync-merge-rules</c> lane in <c>plan.md §12</c> tracks lifting
/// the same merge into the CoreSync provider config once that surface
/// supports per-table merge callbacks; until then the monotonic-update
/// invariant in this service is the contract that prevents data loss in
/// practice. New tests in <c>PlanServiceTests</c> assert it
/// (<c>UpdateProgress_OnlyMovesValueForward</c>,
/// <c>MarkComplete_IsIdempotent_AndPreservesEarliestCompletedAt</c>,
/// <c>Regenerate_PreservesProgressForMatchingItems</c>).
/// </remarks>
public sealed class PlanService : IPlanService
{
    /// <summary>
    /// Hard cap for <see cref="DailyPlanCompletion.MinutesSpent"/>. Matches
    /// the client-side clamp the legacy ProgressService used so the merge
    /// semantics (set-style, <c>max(existing, incoming)</c>) stay
    /// equivalent across paths.
    /// </summary>
    public const int MaxMinutesSpent = 240;

    /// <summary>
    /// How far back to look when computing the user's streak. Hard cap so
    /// even a pathological multi-year practice history doesn't drag the
    /// /today response.
    /// </summary>
    private const int StreakLookbackDays = 365;

    private readonly ApplicationDbContext _db;
    private readonly IUserScopeProvider _scope;
    private readonly IPlanDateContext _dateContext;
    private readonly IDeterministicPlanGenerator _deterministic;
    private readonly ILlmPlanGenerator? _llm;
    private readonly IPlanCopyProvider _copy;
    private readonly ILogger<PlanService> _logger;

    // JSON options + facts DTOs hoisted to PlanFactsSerializer (shared with
    // ProgressService). DO NOT add new private serialization options here —
    // any drift breaks the CoreSync round-trip silently.

    public PlanService(
        ApplicationDbContext db,
        IUserScopeProvider scope,
        IPlanDateContext dateContext,
        IDeterministicPlanGenerator deterministic,
        IPlanCopyProvider copy,
        ILogger<PlanService> logger,
        ILlmPlanGenerator? llm = null)
    {
        _db = db;
        _scope = scope;
        _dateContext = dateContext;
        _deterministic = deterministic;
        _llm = llm;
        _copy = copy;
        _logger = logger;
    }

    public async Task<TodaysPlanDto?> GetTodayAsync(CancellationToken ct = default)
    {
        var userId = _scope.UserProfileId;
        var todayLocal = _dateContext.UserLocalDate;
        var todayKey = ToDateKey(todayLocal);

        var plan = await _db.DailyPlans.AsNoTracking()
            .FirstOrDefaultAsync(p => p.UserProfileId == userId && p.Date == todayKey, ct);

        if (plan is null)
        {
            return null;
        }

        var completions = await _db.DailyPlanCompletions.AsNoTracking()
            .Where(c => c.UserProfileId == userId && c.Date == todayKey)
            .OrderBy(c => c.Priority)
            .ThenBy(c => c.PlanItemId)
            .ToListAsync(ct);

        var streak = await ComputeStreakAsync(userId, todayLocal, ct);

        return BuildDto(plan, completions, streak);
    }

    public async Task<TodaysPlanDto> GenerateTodayAsync(GenerateTodaysPlanRequest request, CancellationToken ct = default)
    {
        var userId = _scope.UserProfileId;
        var todayLocal = _dateContext.UserLocalDate;
        var todayKey = ToDateKey(todayLocal);
        var nowUtc = _dateContext.UtcNow;

        var strategy = ResolveStrategy(request.Strategy);

        PlanSkeleton? skeleton = null;
        try
        {
            var generator = strategy == "llm" && _llm is not null
                ? (IPlanGenerator)_llm
                : _deterministic;
            skeleton = await generator.GenerateAsync(userId, ct);

            // If LLM returned nothing, transparently fall back to deterministic.
            if (skeleton is null && generator is ILlmPlanGenerator)
            {
                // Identifier-free by policy: Coach call paths can reach the
                // generators, and a raw user id must never enter telemetry.
                _logger.LogWarning(
                    "LLM generator returned null. Falling back to deterministic.");
                skeleton = await _deterministic.GenerateAsync(userId, ct);
                strategy = "deterministic";
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Exception type name only — never the exception object. The LLM generator reaches a
            // model provider, and a provider failure routinely quotes the prompt or the model's
            // own output in Exception.Message, in an inner exception, or in Exception.Data;
            // LogWarning(ex, ...) writes all of it through Exception.ToString(). Coach call paths
            // reach this generator, so that text is learner content. A type name is a
            // compile-time constant and cannot carry any of it. This mirrors the sanitized
            // logging the coach surface uses (CoachExceptionSanitizer / CoachMemoryEndpoints),
            // which lives in the API project and is not referenceable from Shared.
            _logger.LogWarning(
                "Plan generator threw. Falling back to empty plan. Error={Error}",
                ex.GetType().Name);
        }

        if (skeleton is null)
        {
            skeleton = new PlanSkeleton
            {
                Activities = new List<PlannedActivity>(),
                TotalMinutes = 0,
                ResourceSelectionReason = string.Empty,
            };
            strategy = "deterministic";
        }

        // Resolve the date the generator stamped on its items. DPB uses
        // IPlanDateContext.UserLocalDate via the same shared service, so
        // these should match; carry the local-day instant to keep
        // GeneratePlanItemId byte-stable with the existing client scheme.
        var dateForIds = todayKey;

        // Preserve per-item progress across regenerations. Look up existing
        // completion rows for this user+date BEFORE we delete them.
        var existing = await _db.DailyPlanCompletions
            .Where(c => c.UserProfileId == userId && c.Date == todayKey)
            .ToListAsync(ct);
        var existingByItemId = existing.ToDictionary(c => c.PlanItemId, StringComparer.Ordinal);

        // Build the new completion rows from the skeleton's planned
        // activities. Stable ids come from PlanConverter to match CoreSync.
        var newCompletions = new List<DailyPlanCompletion>(skeleton.Activities.Count);
        foreach (var activity in skeleton.Activities)
        {
            if (!Enum.TryParse<PlanActivityType>(activity.ActivityType, ignoreCase: false, out var activityType))
            {
                _logger.LogWarning(
                    "Skipping unknown activity type '{ActivityType}' returned by generator.",
                    activity.ActivityType);
                continue;
            }

            var planItemId = PlanConverter.GeneratePlanItemId(
                dateForIds, activityType, activity.ResourceId, activity.SkillId);

            existingByItemId.TryGetValue(planItemId, out var prior);

            // Capture per-item progress from the merge: take max minutes,
            // OR isCompleted, and earliest non-null CompletedAt — same
            // semantics CoreSync applies, so the HTTP path is consistent.
            var minutesSpent = prior is null ? 0 : Math.Clamp(prior.MinutesSpent, 0, MaxMinutesSpent);
            var isCompleted = prior?.IsCompleted ?? false;
            var completedAt = prior?.CompletedAt;
            var createdAt = prior?.CreatedAt ?? nowUtc;

            newCompletions.Add(new DailyPlanCompletion
            {
                Id = prior?.Id ?? Guid.NewGuid().ToString("N"),
                UserProfileId = userId,
                Date = todayKey,
                PlanItemId = planItemId,
                ActivityType = activity.ActivityType,
                ResourceId = activity.ResourceId,
                SkillId = activity.SkillId,
                IsCompleted = isCompleted,
                CompletedAt = completedAt,
                MinutesSpent = minutesSpent,
                EstimatedMinutes = activity.EstimatedMinutes,
                Priority = activity.Priority,
                TitleKey = string.Empty,
                DescriptionKey = string.Empty,
#pragma warning disable CS0618 // legacy obsolete columns retained until drop-legacy migration
                Rationale = string.Empty,
                NarrativeJson = null,
#pragma warning restore CS0618
                CreatedAt = createdAt,
                UpdatedAt = nowUtc,
            });
        }

        // Upsert the parent DailyPlan row keyed by (UserProfileId, Date).
        var planRow = await _db.DailyPlans
            .FirstOrDefaultAsync(p => p.UserProfileId == userId && p.Date == todayKey, ct);

        var (rationaleFactsJson, narrativeFactsJson, focusVocabularyFactsJson) = SerializeFacts(skeleton);

        if (planRow is null)
        {
            planRow = new DailyPlan
            {
                Id = Guid.NewGuid().ToString("N"),
                UserProfileId = userId,
                Date = todayKey,
                GeneratedAtUtc = nowUtc,
                Strategy = strategy,
                RationaleFacts = rationaleFactsJson,
                NarrativeFacts = narrativeFactsJson,
                FocusVocabularyFacts = focusVocabularyFactsJson,
                CreatedAt = nowUtc,
                UpdatedAt = nowUtc,
            };
            _db.DailyPlans.Add(planRow);
        }
        else
        {
            planRow.GeneratedAtUtc = nowUtc;
            planRow.Strategy = strategy;
            planRow.RationaleFacts = rationaleFactsJson;
            planRow.NarrativeFacts = narrativeFactsJson;
            planRow.FocusVocabularyFacts = focusVocabularyFactsJson;
            planRow.UpdatedAt = nowUtc;
        }

        // Replace child rows: remove rows that no longer appear in the new
        // plan; upsert the rest. The unique (UserProfileId, Date, PlanItemId)
        // index guarantees we never produce duplicates.
        var newIdSet = new HashSet<string>(newCompletions.Select(c => c.PlanItemId), StringComparer.Ordinal);
        foreach (var stale in existing.Where(c => !newIdSet.Contains(c.PlanItemId)))
        {
            _db.DailyPlanCompletions.Remove(stale);
        }
        foreach (var fresh in newCompletions)
        {
            if (existingByItemId.TryGetValue(fresh.PlanItemId, out var prior))
            {
                // Update in place so we don't churn the primary key.
                prior.ActivityType = fresh.ActivityType;
                prior.ResourceId = fresh.ResourceId;
                prior.SkillId = fresh.SkillId;
                prior.EstimatedMinutes = fresh.EstimatedMinutes;
                prior.Priority = fresh.Priority;
                prior.UpdatedAt = nowUtc;
            }
            else
            {
                _db.DailyPlanCompletions.Add(fresh);
            }
        }

        await _db.SaveChangesAsync(ct);

        var streak = await ComputeStreakAsync(userId, todayLocal, ct);

        // Reload completions tracked so we return what's on disk.
        var finalCompletions = await _db.DailyPlanCompletions.AsNoTracking()
            .Where(c => c.UserProfileId == userId && c.Date == todayKey)
            .OrderBy(c => c.Priority)
            .ThenBy(c => c.PlanItemId)
            .ToListAsync(ct);

        return BuildDto(planRow, finalCompletions, streak);
    }

    public async Task<bool> UpdateProgressAsync(DateOnly planDate, string planItemId, int minutesSpent, CancellationToken ct = default)
    {
        var userId = _scope.UserProfileId;
        var dateKey = ToDateKey(planDate);
        var clamped = Math.Clamp(minutesSpent, 0, MaxMinutesSpent);
        var nowUtc = _dateContext.UtcNow;

        var row = await _db.DailyPlanCompletions
            .FirstOrDefaultAsync(c => c.UserProfileId == userId
                && c.Date == dateKey
                && c.PlanItemId == planItemId, ct);

        if (row is null)
        {
            return false;
        }

        // Set-style with floor of max(existing, incoming) so concurrent
        // updates from two clients never roll the value backwards. §6 of
        // plan.md.
        if (clamped > row.MinutesSpent)
        {
            row.MinutesSpent = clamped;
        }
        row.UpdatedAt = nowUtc;
        await _db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<PlanItemDto?> MarkCompleteAsync(DateOnly planDate, string planItemId, int minutesSpent, CancellationToken ct = default)
    {
        var userId = _scope.UserProfileId;
        var dateKey = ToDateKey(planDate);
        var clamped = Math.Clamp(minutesSpent, 0, MaxMinutesSpent);
        var nowUtc = _dateContext.UtcNow;

        var row = await _db.DailyPlanCompletions
            .FirstOrDefaultAsync(c => c.UserProfileId == userId
                && c.Date == dateKey
                && c.PlanItemId == planItemId, ct);

        if (row is null)
        {
            return null;
        }

        if (clamped > row.MinutesSpent)
        {
            row.MinutesSpent = clamped;
        }
        if (!row.IsCompleted)
        {
            row.IsCompleted = true;
            row.CompletedAt ??= nowUtc;
        }
        row.UpdatedAt = nowUtc;
        await _db.SaveChangesAsync(ct);

        var planFacts = await _db.DailyPlans.AsNoTracking()
            .Where(p => p.UserProfileId == userId && p.Date == dateKey)
            .Select(p => new { p.FocusVocabularyFacts, p.NarrativeFacts })
            .FirstOrDefaultAsync(ct);
        var focusVocabularyIds = DeserializeFocusVocabularyFacts(planFacts?.FocusVocabularyFacts);
        if (focusVocabularyIds.Count == 0)
        {
            var narrative = TryDeserializeNarrative(planFacts?.NarrativeFacts);
            if (narrative?.VocabInsight?.PreviewWords is { Count: > 0 } previewWords)
            {
                focusVocabularyIds = NormalizeFocusVocabularyIds(previewWords.Select(w => w.WordId));
            }
        }
        return MapItem(row, focusVocabularyIds);
    }

    public async Task ResetTodayAsync(CancellationToken ct = default)
    {
        var userId = _scope.UserProfileId;
        var todayKey = ToDateKey(_dateContext.UserLocalDate);

        var plan = await _db.DailyPlans
            .FirstOrDefaultAsync(p => p.UserProfileId == userId && p.Date == todayKey, ct);
        var completions = await _db.DailyPlanCompletions
            .Where(c => c.UserProfileId == userId && c.Date == todayKey)
            .ToListAsync(ct);

        if (plan is not null)
        {
            _db.DailyPlans.Remove(plan);
        }
        if (completions.Count > 0)
        {
            _db.DailyPlanCompletions.RemoveRange(completions);
        }

        if (plan is not null || completions.Count > 0)
        {
            await _db.SaveChangesAsync(ct);
        }
    }

    // ---------- coach plan revision ----------

    public async Task<PlanSnapshot> GetTodaySnapshotAsync(CancellationToken ct = default)
    {
        var userId = _scope.UserProfileId;
        var todayLocal = _dateContext.UserLocalDate;

        var completions = await LoadCompletionsAsync(userId, ToDateKey(todayLocal), tracked: false, ct);
        return PlanSnapshot.FromCompletions(todayLocal, completions);
    }

    public Task<PlanPreviewResult> PreviewPlanAsync(PlanConstraints? constraints, CancellationToken ct = default)
        => PreviewPlanAsync(constraints, focusVocabularyWordIds: null, ct);

    public async Task<PlanPreviewResult> PreviewPlanAsync(
        PlanConstraints? constraints,
        IReadOnlyList<string>? focusVocabularyWordIds,
        CancellationToken ct = default)
    {
        var userId = _scope.UserProfileId;
        var todayLocal = _dateContext.UserLocalDate;

        if (constraints is not null && !constraints.TryValidate(out var errors))
        {
            // Coach telemetry rule: never emit a user or profile id on this path.
            _logger.LogInformation(
                "Plan preview rejected: {ErrorCount} invalid constraint field(s).", errors.Count);
            return PlanPreviewResult.InvalidConstraints(errors);
        }

        // Preview never writes: PlanBuildRequest.Preview suppresses the only
        // write on the generation path (smart-resource seeding).
        var skeleton = await _deterministic
            .GenerateAsync(PlanBuildRequest.Preview(userId, constraints, focusVocabularyWordIds), ct)
            .ConfigureAwait(false);

        if (skeleton is null)
        {
            return PlanPreviewResult.NoFeasiblePlan();
        }

        var items = ProjectSkeleton(skeleton, ToDateKey(todayLocal), priorityOffset: 0);
        if (items.Count == 0)
        {
            return PlanPreviewResult.NoFeasiblePlan();
        }

        var snapshot = PlanSnapshot.FromItems(
            todayLocal,
            items.Select(i => new PlanSnapshotItem
            {
                PlanItemId = i.PlanItemId,
                ActivityType = i.ActivityType,
                ResourceId = i.ResourceId,
                SkillId = i.SkillId,
                Priority = i.Priority,
                EstimatedMinutes = i.EstimatedMinutes,
                MinutesSpent = 0,
                IsCompleted = false
            }));

        return PlanPreviewResult.Success(skeleton, snapshot);
    }

    public Task<PlanRevisionResult> ApplyCoachConstraintsAsync(
        CoachPlanRevisionRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return ExecuteRevisionUnitAsync(token => ApplyCoachConstraintsCoreAsync(request, token), ct);
    }

    /// <summary>
    /// The retriable unit behind <see cref="ApplyCoachConstraintsAsync"/>.
    /// </summary>
    /// <remarks>
    /// Every read this needs happens inside the unit, so an execution-strategy
    /// retry re-derives state from the database instead of replaying stale
    /// in-memory entities. The unit never begins, commits, or rolls back a
    /// transaction itself — it reports whether its work should be committed and
    /// <see cref="ExecuteRevisionUnitAsync"/> owns the transaction lifetime.
    /// </remarks>
    private async Task<(PlanRevisionResult Result, bool ShouldCommit)> ApplyCoachConstraintsCoreAsync(
        CoachPlanRevisionRequest request,
        CancellationToken ct)
    {
        var userId = _scope.UserProfileId;
        var todayLocal = _dateContext.UserLocalDate;
        var todayKey = ToDateKey(todayLocal);
        var nowUtc = _dateContext.UtcNow;

        var planRow = await _db.DailyPlans
            .FirstOrDefaultAsync(p => p.UserProfileId == userId && p.Date == todayKey, ct);
        var existing = await LoadCompletionsAsync(userId, todayKey, tracked: true, ct);
        var before = PlanSnapshot.FromCompletions(todayLocal, existing);

        if (planRow is null)
        {
            _logger.LogInformation(
                "Coach revision skipped: no plan exists for {PlanDate}.", todayLocal);
            return (PlanRevisionResult.NoWrite(PlanRevisionOutcome.PlanNotFound, before, request.OperationKey), false);
        }

        if (!before.MatchesVersion(request.ExpectedPlanVersion))
        {
            _logger.LogInformation(
                "Coach revision rejected (session '{SessionId}'): stale plan version.",
                request.SessionId);
            return (PlanRevisionResult.NoWrite(PlanRevisionOutcome.StalePlanVersion, before, request.OperationKey), false);
        }

        var preview = await PreviewPlanAsync(request.Constraints, request.FocusVocabularyWordIds, ct)
            .ConfigureAwait(false);
        if (!preview.IsSuccess)
        {
            var outcome = preview.Outcome == PlanPreviewOutcome.InvalidConstraints
                ? PlanRevisionOutcome.InvalidConstraints
                : PlanRevisionOutcome.NoFeasiblePlan;
            return (PlanRevisionResult.NoWrite(outcome, before, request.OperationKey, preview.ValidationErrors), false);
        }

        var preserved = existing.Where(c => c.IsCompleted || c.MinutesSpent > 0).ToList();
        var preservedIds = new HashSet<string>(preserved.Select(c => c.PlanItemId), StringComparer.Ordinal);
        var maxPreservedPriority = preserved.Count == 0 ? 0 : preserved.Max(c => c.Priority);

        // New items slot in after everything the learner has touched, so
        // preserved rows keep their exact stored priority and completed rows
        // stay byte-identical.
        var projected = ProjectSkeleton(preview.Skeleton!, todayKey, maxPreservedPriority)
            .Where(i => !preservedIds.Contains(i.PlanItemId))
            .ToList();

        var projectedById = projected.ToDictionary(i => i.PlanItemId, StringComparer.Ordinal);
        var replaceable = existing.Where(c => !preservedIds.Contains(c.PlanItemId)).ToList();

        var projectedItems = preserved
            .Select(ToSnapshotItem)
            .Concat(projected.Select(i => new PlanSnapshotItem
            {
                PlanItemId = i.PlanItemId,
                ActivityType = i.ActivityType,
                ResourceId = i.ResourceId,
                SkillId = i.SkillId,
                Priority = i.Priority,
                EstimatedMinutes = i.EstimatedMinutes,
                MinutesSpent = 0,
                IsCompleted = false
            }))
            .ToList();

        var after = PlanSnapshot.FromItems(todayLocal, projectedItems);

        if (string.Equals(after.Hash, before.Hash, StringComparison.Ordinal))
        {
            // Repeating an already-applied revision lands here. Nothing is
            // written, so apply is safely repeatable without a stored key.
            _logger.LogInformation(
                "Coach revision (session '{SessionId}') produced no change.",
                request.SessionId);
            return (PlanRevisionResult.NoWrite(PlanRevisionOutcome.NoChange, before, request.OperationKey), false);
        }

        var adjusted = 0;
        var added = 0;

        foreach (var row in replaceable)
        {
            if (projectedById.TryGetValue(row.PlanItemId, out var match))
            {
                // Matching untouched item: keep the row (and its stable id)
                // and re-point it at the revised plan's shape.
                row.ActivityType = match.ActivityType;
                row.ResourceId = match.ResourceId;
                row.SkillId = match.SkillId;
                row.EstimatedMinutes = match.EstimatedMinutes;
                row.Priority = match.Priority;
                row.UpdatedAt = nowUtc;
                adjusted++;
            }
            else
            {
                _db.DailyPlanCompletions.Remove(row);
            }
        }

        var existingIds = new HashSet<string>(existing.Select(c => c.PlanItemId), StringComparer.Ordinal);
        foreach (var item in projected.Where(i => !existingIds.Contains(i.PlanItemId)))
        {
            _db.DailyPlanCompletions.Add(NewCompletionRow(item, userId, todayKey, nowUtc));
            added++;
        }

        var (rationaleFactsJson, narrativeFactsJson, focusVocabularyFactsJson) = SerializeFacts(preview.Skeleton!);
        planRow.GeneratedAtUtc = nowUtc;
        planRow.RationaleFacts = rationaleFactsJson;
        planRow.NarrativeFacts = narrativeFactsJson;
        planRow.FocusVocabularyFacts = focusVocabularyFactsJson;
        planRow.UpdatedAt = nowUtc;

        await _db.SaveChangesAsync(ct);

        var persisted = await LoadCompletionsAsync(userId, todayKey, tracked: false, ct);
        var persistedSnapshot = PlanSnapshot.FromCompletions(todayLocal, persisted);

        var invariantErrors = ValidateRevisedPlan(before, persistedSnapshot);
        if (invariantErrors.Count > 0)
        {
            _logger.LogError(
                "Coach revision violated {ViolationCount} plan invariant(s); rolling back.",
                invariantErrors.Count);
            return (
                PlanRevisionResult.NoWrite(
                    PlanRevisionOutcome.ValidationFailed, before, request.OperationKey, invariantErrors),
                false);
        }

        return (
            BuildRevisionResult(
                PlanRevisionOutcome.Applied,
                request.OperationKey,
                before,
                persistedSnapshot,
                replacedItemCount: replaceable.Count,
                addedItemCount: added,
                removedItemCount: replaceable.Count - adjusted,
                adjustedItemCount: adjusted),
            true);
    }

    public Task<PlanRevisionResult> UndoCoachRevisionAsync(
        CoachPlanUndoRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.TargetSnapshot);
        return ExecuteRevisionUnitAsync(token => UndoCoachRevisionCoreAsync(request, token), ct);
    }

    /// <summary>The retriable unit behind <see cref="UndoCoachRevisionAsync"/>.</summary>
    private async Task<(PlanRevisionResult Result, bool ShouldCommit)> UndoCoachRevisionCoreAsync(
        CoachPlanUndoRequest request,
        CancellationToken ct)
    {
        var userId = _scope.UserProfileId;
        var todayLocal = _dateContext.UserLocalDate;
        var todayKey = ToDateKey(todayLocal);
        var nowUtc = _dateContext.UtcNow;

        var planRow = await _db.DailyPlans
            .FirstOrDefaultAsync(p => p.UserProfileId == userId && p.Date == todayKey, ct);
        var existing = await LoadCompletionsAsync(userId, todayKey, tracked: true, ct);
        var before = PlanSnapshot.FromCompletions(todayLocal, existing);

        if (planRow is null)
        {
            return (PlanRevisionResult.NoWrite(PlanRevisionOutcome.PlanNotFound, before, request.OperationKey), false);
        }

        if (!before.MatchesVersion(request.ExpectedPlanVersion))
        {
            _logger.LogInformation(
                "Coach undo rejected (revision '{RevisionId}'): stale plan version.",
                request.RevisionId);
            return (PlanRevisionResult.NoWrite(PlanRevisionOutcome.StalePlanVersion, before, request.OperationKey), false);
        }

        if (request.TargetSnapshot.PlanDate != todayLocal)
        {
            return (
                PlanRevisionResult.NoWrite(
                    PlanRevisionOutcome.ValidationFailed, before, request.OperationKey,
                    new[] { "Undo snapshot belongs to a different plan date." }),
                false);
        }

        var existingById = existing.ToDictionary(c => c.PlanItemId, StringComparer.Ordinal);
        var targetById = request.TargetSnapshot.Items.ToDictionary(i => i.PlanItemId, StringComparer.Ordinal);

        var adjusted = 0;
        var added = 0;
        var removed = 0;

        foreach (var row in existing)
        {
            // Completed and started work is never touched by an undo, even
            // when the target snapshot predates it.
            if (row.IsCompleted || row.MinutesSpent > 0)
            {
                continue;
            }

            if (targetById.TryGetValue(row.PlanItemId, out var target))
            {
                if (row.EstimatedMinutes != target.EstimatedMinutes || row.Priority != target.Priority)
                {
                    row.EstimatedMinutes = target.EstimatedMinutes;
                    row.Priority = target.Priority;
                    row.UpdatedAt = nowUtc;
                    adjusted++;
                }
            }
            else
            {
                _db.DailyPlanCompletions.Remove(row);
                removed++;
            }
        }

        foreach (var target in request.TargetSnapshot.Items)
        {
            if (existingById.ContainsKey(target.PlanItemId))
            {
                continue;
            }

            _db.DailyPlanCompletions.Add(new DailyPlanCompletion
            {
                Id = Guid.NewGuid().ToString("N"),
                UserProfileId = userId,
                Date = todayKey,
                PlanItemId = target.PlanItemId,
                ActivityType = target.ActivityType,
                ResourceId = target.ResourceId,
                SkillId = target.SkillId,
                IsCompleted = false,
                CompletedAt = null,
                // Restoring a snapshot never re-applies its logged minutes;
                // a restored item starts clean and the live rows above keep
                // whatever the learner has actually logged.
                MinutesSpent = 0,
                EstimatedMinutes = target.EstimatedMinutes,
                Priority = target.Priority,
                TitleKey = string.Empty,
                DescriptionKey = string.Empty,
#pragma warning disable CS0618 // legacy obsolete columns retained until drop-legacy migration
                Rationale = string.Empty,
                NarrativeJson = null,
#pragma warning restore CS0618
                CreatedAt = nowUtc,
                UpdatedAt = nowUtc,
            });
            added++;
        }

        if (added == 0 && removed == 0 && adjusted == 0)
        {
            return (PlanRevisionResult.NoWrite(PlanRevisionOutcome.NoChange, before, request.OperationKey), false);
        }

        planRow.UpdatedAt = nowUtc;
        await _db.SaveChangesAsync(ct);

        var persisted = await LoadCompletionsAsync(userId, todayKey, tracked: false, ct);
        var persistedSnapshot = PlanSnapshot.FromCompletions(todayLocal, persisted);

        var invariantErrors = ValidateRevisedPlan(before, persistedSnapshot);
        if (invariantErrors.Count > 0)
        {
            _logger.LogError(
                "Coach undo violated {ViolationCount} plan invariant(s); rolling back.",
                invariantErrors.Count);
            return (
                PlanRevisionResult.NoWrite(
                    PlanRevisionOutcome.ValidationFailed, before, request.OperationKey, invariantErrors),
                false);
        }

        return (
            BuildRevisionResult(
                PlanRevisionOutcome.Applied,
                request.OperationKey,
                before,
                persistedSnapshot,
                replacedItemCount: removed + adjusted,
                addedItemCount: added,
                removedItemCount: removed,
                adjustedItemCount: adjusted),
            true);
    }

    // ---------- coach revision helpers ----------

    /// <summary>
    /// Post-write invariants that must hold for any coach revision. A violation
    /// rolls the transaction back rather than shipping a corrupted plan.
    /// </summary>
    internal static List<string> ValidateRevisedPlan(PlanSnapshot before, PlanSnapshot after)
    {
        var errors = new List<string>();

        foreach (var priorItem in before.Items.Where(i => i.IsCompleted))
        {
            var match = after.Items.FirstOrDefault(i =>
                string.Equals(i.PlanItemId, priorItem.PlanItemId, StringComparison.Ordinal));

            if (match is null)
            {
                errors.Add($"Completed item '{priorItem.PlanItemId}' was removed by the revision.");
                continue;
            }

            if (!match.IsCompleted)
            {
                errors.Add($"Completed item '{priorItem.PlanItemId}' lost its completed state.");
            }

            if (match.MinutesSpent < priorItem.MinutesSpent)
            {
                errors.Add($"Completed item '{priorItem.PlanItemId}' lost logged minutes.");
            }
        }

        foreach (var priorItem in before.Items.Where(i => !i.IsCompleted && i.MinutesSpent > 0))
        {
            var match = after.Items.FirstOrDefault(i =>
                string.Equals(i.PlanItemId, priorItem.PlanItemId, StringComparison.Ordinal));

            if (match is null)
            {
                errors.Add($"Started item '{priorItem.PlanItemId}' was removed by the revision.");
            }
            else if (match.MinutesSpent < priorItem.MinutesSpent)
            {
                errors.Add($"Started item '{priorItem.PlanItemId}' lost logged minutes.");
            }
        }

        if (after.TotalMinutesSpent < before.TotalMinutesSpent)
        {
            errors.Add("Total logged minutes decreased.");
        }

        var duplicateIds = after.Items
            .GroupBy(i => i.PlanItemId, StringComparer.Ordinal)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToList();
        foreach (var duplicate in duplicateIds)
        {
            errors.Add($"Duplicate plan item id '{duplicate}' in revised plan.");
        }

        return errors;
    }

    private static PlanRevisionResult BuildRevisionResult(
        PlanRevisionOutcome outcome,
        string? operationKey,
        PlanSnapshot before,
        PlanSnapshot after,
        int replacedItemCount,
        int addedItemCount,
        int removedItemCount,
        int adjustedItemCount) =>
        new()
        {
            Outcome = outcome,
            OperationKey = operationKey,
            Before = before,
            After = after,
            PreservedCompletedCount = before.CompletedItemCount,
            PreservedInProgressCount = before.InProgressItemCount,
            PreservedMinutesSpent = after.TotalMinutesSpent,
            ReplacedItemCount = replacedItemCount,
            AddedItemCount = addedItemCount,
            RemovedItemCount = removedItemCount,
            AdjustedItemCount = adjustedItemCount
        };

    private static PlanSnapshotItem ToSnapshotItem(DailyPlanCompletion row) => new()
    {
        PlanItemId = row.PlanItemId,
        ActivityType = row.ActivityType,
        ResourceId = row.ResourceId,
        SkillId = row.SkillId,
        Priority = row.Priority,
        EstimatedMinutes = row.EstimatedMinutes,
        MinutesSpent = row.MinutesSpent,
        IsCompleted = row.IsCompleted
    };

    private Task<List<DailyPlanCompletion>> LoadCompletionsAsync(
        string userId, DateTime dateKey, bool tracked, CancellationToken ct)
    {
        var query = _db.DailyPlanCompletions.Where(c => c.UserProfileId == userId && c.Date == dateKey);
        if (!tracked)
        {
            query = query.AsNoTracking();
        }
        return query.OrderBy(c => c.Priority).ThenBy(c => c.PlanItemId).ToListAsync(ct);
    }

    /// <summary>
    /// Maps a skeleton's activities onto stable plan item ids, skipping activity
    /// types the enum doesn't recognize (the same guard
    /// <see cref="GenerateTodayAsync"/> applies).
    /// </summary>
    private List<ProjectedPlanItem> ProjectSkeleton(
        PlanSkeleton skeleton, DateTime dateForIds, int priorityOffset)
    {
        var items = new List<ProjectedPlanItem>(skeleton.Activities.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var activity in skeleton.Activities)
        {
            if (!Enum.TryParse<PlanActivityType>(activity.ActivityType, ignoreCase: false, out var activityType))
            {
                _logger.LogWarning(
                    "Skipping unknown activity type '{ActivityType}' returned by generator.",
                    activity.ActivityType);
                continue;
            }

            var planItemId = PlanConverter.GeneratePlanItemId(
                dateForIds, activityType, activity.ResourceId, activity.SkillId);

            if (!seen.Add(planItemId))
            {
                // The stable id scheme can collide when a generator emits the
                // same activity/resource/skill twice; the unique index would
                // reject the second row, so drop it here instead.
                continue;
            }

            items.Add(new ProjectedPlanItem(
                planItemId,
                activity.ActivityType,
                activity.ResourceId,
                activity.SkillId,
                activity.EstimatedMinutes,
                priorityOffset + activity.Priority));
        }

        return items;
    }

    private DailyPlanCompletion NewCompletionRow(
        ProjectedPlanItem item, string userId, DateTime dateKey, DateTime nowUtc) => new()
    {
        Id = Guid.NewGuid().ToString("N"),
        UserProfileId = userId,
        Date = dateKey,
        PlanItemId = item.PlanItemId,
        ActivityType = item.ActivityType,
        ResourceId = item.ResourceId,
        SkillId = item.SkillId,
        IsCompleted = false,
        CompletedAt = null,
        MinutesSpent = 0,
        EstimatedMinutes = item.EstimatedMinutes,
        Priority = item.Priority,
        TitleKey = string.Empty,
        DescriptionKey = string.Empty,
#pragma warning disable CS0618 // legacy obsolete columns retained until drop-legacy migration
        Rationale = string.Empty,
        NarrativeJson = null,
#pragma warning restore CS0618
        CreatedAt = nowUtc,
        UpdatedAt = nowUtc,
    };

    /// <summary>
    /// Runs one plan-revision unit under the provider's execution strategy.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why this exists:</b> the API runs on Npgsql with
    /// <c>EnableRetryOnFailure</c>, so EF refuses a hand-rolled
    /// <c>BeginTransaction</c> with "NpgsqlRetryingExecutionStrategy does not
    /// support user-initiated transactions". Any user-initiated transaction has
    /// to be created and committed *inside*
    /// <c>Database.CreateExecutionStrategy().ExecuteAsync(...)</c> so the
    /// strategy can replay the whole unit — transaction included — after a
    /// transient failure.
    /// </para>
    /// <para>
    /// The unit re-reads everything it needs on each attempt, and the change
    /// tracker is cleared before each attempt, so a retry never replays stale
    /// entities from the failed one.
    /// </para>
    /// <para>
    /// <b>Ambient transactions:</b> when the caller already owns a transaction
    /// we join it — no retry (retrying inside somebody else's transaction would
    /// duplicate their work), and no commit or rollback of their transaction.
    /// A rejected unit is undone with a savepoint when the provider supports
    /// one, so the caller's other work survives.
    /// </para>
    /// </remarks>
    private async Task<PlanRevisionResult> ExecuteRevisionUnitAsync(
        Func<CancellationToken, Task<(PlanRevisionResult Result, bool ShouldCommit)>> unit,
        CancellationToken ct)
    {
        if (_db.Database.CurrentTransaction is { } ambient)
        {
            return await ExecuteInAmbientTransactionAsync(unit, ambient, ct).ConfigureAwait(false);
        }

        var strategy = _db.Database.CreateExecutionStrategy();

        return await strategy.ExecuteAsync(async () =>
        {
            // Each attempt starts from a clean slate: anything a previous
            // attempt staged is discarded before we re-read.
            _db.ChangeTracker.Clear();

            IDbContextTransaction? tx;
            try
            {
                tx = await _db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
            }
            catch (InvalidOperationException ex)
            {
                // Providers without transaction support (e.g. the in-memory
                // provider used by some component tests). The merge still runs;
                // it just isn't atomic there.
                _logger.LogDebug(
                    "Provider does not support transactions; coach revision will not be atomic. Error={Error}",
                    ex.GetType().Name);
                tx = null;
            }

            if (tx is null)
            {
                var (nonAtomicResult, shouldCommit) = await unit(ct).ConfigureAwait(false);
                if (!shouldCommit)
                {
                    _db.ChangeTracker.Clear();
                }
                return nonAtomicResult;
            }

            await using (tx.ConfigureAwait(false))
            {
                try
                {
                    var (result, shouldCommit) = await unit(ct).ConfigureAwait(false);

                    if (shouldCommit)
                    {
                        await tx.CommitAsync(ct).ConfigureAwait(false);
                    }
                    else
                    {
                        await tx.RollbackAsync(ct).ConfigureAwait(false);
                        // Drop staged changes too, otherwise the next
                        // SaveChangesAsync on this context replays them.
                        _db.ChangeTracker.Clear();
                    }

                    return result;
                }
                catch
                {
                    await SafeRollbackAsync(tx, ct).ConfigureAwait(false);
                    _db.ChangeTracker.Clear();
                    throw;
                }
            }
        }).ConfigureAwait(false);
    }

    private async Task<PlanRevisionResult> ExecuteInAmbientTransactionAsync(
        Func<CancellationToken, Task<(PlanRevisionResult Result, bool ShouldCommit)>> unit,
        IDbContextTransaction ambient,
        CancellationToken ct)
    {
        var savepoint = await TryCreateSavepointAsync(ambient, ct).ConfigureAwait(false);

        try
        {
            var (result, shouldCommit) = await unit(ct).ConfigureAwait(false);

            if (shouldCommit)
            {
                await TryReleaseSavepointAsync(ambient, savepoint, ct).ConfigureAwait(false);
            }
            else
            {
                await TryRollbackToSavepointAsync(ambient, savepoint, ct).ConfigureAwait(false);
                _db.ChangeTracker.Clear();
            }

            return result;
        }
        catch
        {
            await TryRollbackToSavepointAsync(ambient, savepoint, ct).ConfigureAwait(false);
            _db.ChangeTracker.Clear();
            throw;
        }
    }

    private async Task<string?> TryCreateSavepointAsync(IDbContextTransaction ambient, CancellationToken ct)
    {
        if (!ambient.SupportsSavepoints)
        {
            _logger.LogDebug("Ambient transaction does not support savepoints; a rejected revision cannot be undone here.");
            return null;
        }

        var name = "coach_revision_" + Guid.NewGuid().ToString("N");
        try
        {
            await ambient.CreateSavepointAsync(name, ct).ConfigureAwait(false);
            return name;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(
                "Could not create a savepoint for the coach revision. Error={Error}", ex.GetType().Name);
            return null;
        }
    }

    private async Task TryRollbackToSavepointAsync(IDbContextTransaction ambient, string? savepoint, CancellationToken ct)
    {
        if (savepoint is null)
        {
            return;
        }

        try
        {
            await ambient.RollbackToSavepointAsync(savepoint, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                "Could not roll back to the coach revision savepoint. Error={Error}", ex.GetType().Name);
        }
    }

    private async Task TryReleaseSavepointAsync(IDbContextTransaction ambient, string? savepoint, CancellationToken ct)
    {
        if (savepoint is null)
        {
            return;
        }

        try
        {
            await ambient.ReleaseSavepointAsync(savepoint, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Releasing is an optimization; the savepoint dies with the
            // transaction either way.
            _logger.LogDebug(
                "Could not release the coach revision savepoint. Error={Error}", ex.GetType().Name);
        }
    }

    private async Task SafeRollbackAsync(IDbContextTransaction tx, CancellationToken ct)
    {
        try
        {
            await tx.RollbackAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // The transaction may already be aborted or the connection gone.
            // Swallow so the original failure is the one that propagates.
            _logger.LogDebug(
                "Rolling back the coach revision transaction failed. Error={Error}", ex.GetType().Name);
        }
    }

    private readonly record struct ProjectedPlanItem(
        string PlanItemId,
        string ActivityType,
        string? ResourceId,
        string? SkillId,
        int EstimatedMinutes,
        int Priority);

    // ---------- helpers ----------

    /// <summary>
    /// Canonical date key: user-local midnight expressed as a UTC instant.
    /// Matches the existing on-disk format used by both DailyPlan.Date and
    /// DailyPlanCompletion.Date so CoreSync rows collide on the same value.
    /// </summary>
    private static DateTime ToDateKey(DateOnly localDate) =>
        localDate.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);

    private static string ResolveStrategy(string? requested)
    {
        if (string.IsNullOrWhiteSpace(requested))
        {
            return "deterministic";
        }
        return requested.Trim().ToLowerInvariant() switch
        {
            "llm" => "llm",
            "deterministic" => "deterministic",
            _ => "deterministic", // "auto" + anything else collapses to deterministic for v1
        };
    }

    private async Task<StreakDto> ComputeStreakAsync(string userId, DateOnly todayLocal, CancellationToken ct)
    {
        var sinceKey = ToDateKey(todayLocal.AddDays(-StreakLookbackDays));

        // Gather all completed rows in window. Group by Date (we only care
        // whether a day has at least one completion).
        var completedDays = await _db.DailyPlanCompletions.AsNoTracking()
            .Where(c => c.UserProfileId == userId
                && c.IsCompleted
                && c.Date >= sinceKey)
            .Select(c => c.Date)
            .Distinct()
            .ToListAsync(ct);

        if (completedDays.Count == 0)
        {
            return new StreakDto { CurrentStreak = 0, LongestStreak = 0, LastPracticeDate = null };
        }

        var localDays = completedDays
            .Select(d => DateOnly.FromDateTime(d))
            .OrderByDescending(d => d)
            .ToList();

        var lastPractice = localDays[0];

        // Current streak: must include today OR yesterday (grace day) to be
        // "live". If the most recent completed day is older than yesterday
        // then current = 0.
        int currentStreak = 0;
        var expected = todayLocal;
        foreach (var day in localDays)
        {
            if (day == expected)
            {
                currentStreak++;
                expected = expected.AddDays(-1);
            }
            else if (day == expected.AddDays(1) && currentStreak == 0 && day == todayLocal.AddDays(-1))
            {
                // Grace: first observed day is yesterday, treat as live streak start.
                currentStreak = 1;
                expected = day.AddDays(-1);
            }
            else
            {
                break;
            }
        }

        // Longest streak: linear scan over sorted ascending days.
        int longest = 0;
        int run = 0;
        DateOnly? prev = null;
        foreach (var day in localDays.OrderBy(d => d))
        {
            if (prev is null || day == prev.Value.AddDays(1))
            {
                run++;
            }
            else
            {
                run = 1;
            }
            if (run > longest)
            {
                longest = run;
            }
            prev = day;
        }
        if (currentStreak > longest)
        {
            longest = currentStreak;
        }

        return new StreakDto
        {
            CurrentStreak = currentStreak,
            LongestStreak = longest,
            LastPracticeDate = lastPractice,
        };
    }

    private TodaysPlanDto BuildDto(DailyPlan plan, List<DailyPlanCompletion> completions, StreakDto streak)
    {
        var focusVocabularyIds = DeserializeFocusVocabularyFacts(plan.FocusVocabularyFacts);
        var narrative = TryDeserializeNarrative(plan.NarrativeFacts);
        var rationale = TryDeserializeRationale(plan.RationaleFacts);

        // Fallback: if DailyPlan.FocusVocabularyFacts is empty (legacy rows written
        // before the AddFocusVocabularyFacts migration, or LLM responses that did
        // not propagate FocusVocabularyIds), hydrate from the narrative's
        // PreviewWords. The narrative powers the dashboard insight and the
        // "Preview plan vocabulary" flashcard, so it's the right source to keep
        // activities aligned with what the user previewed.
        if (focusVocabularyIds.Count == 0 && narrative?.VocabInsight?.PreviewWords is { Count: > 0 } previewWords)
        {
            focusVocabularyIds = NormalizeFocusVocabularyIds(previewWords.Select(w => w.WordId));
        }

        var items = completions
            .OrderBy(c => c.Priority)
            .ThenBy(c => c.PlanItemId, StringComparer.Ordinal)
            .Select(c => MapItem(c, focusVocabularyIds))
            .ToList();

        var estimatedTotal = items.Sum(i => i.EstimatedMinutes);
        var completedCount = items.Count(i => i.IsCompleted);
        var totalCount = items.Count;
        var percent = totalCount == 0 ? 0d : (completedCount * 100d) / totalCount;

        return new TodaysPlanDto
        {
            GeneratedForDate = DateOnly.FromDateTime(plan.Date),
            GeneratedAtUtc = DateTime.SpecifyKind(plan.GeneratedAtUtc, DateTimeKind.Utc),
            Strategy = string.IsNullOrWhiteSpace(plan.Strategy) ? "deterministic" : plan.Strategy,
            Items = items,
            FocusVocabularyIds = focusVocabularyIds,
            EstimatedTotalMinutes = estimatedTotal,
            CompletedCount = completedCount,
            TotalCount = totalCount,
            CompletionPercentage = percent,
            Streak = streak,
            Narrative = narrative,
            Rationale = rationale,
        };
    }

    private PlanItemDto MapItem(DailyPlanCompletion row, IReadOnlyList<string> planFocusVocabularyIds)
    {
        var activityType = Enum.TryParse<PlanActivityType>(row.ActivityType, out var parsed)
            ? parsed
            : PlanActivityType.VocabularyReview;
        var itemFocusVocabularyIds = ShouldUsePlanFocusVocabularyIds(activityType)
            ? planFocusVocabularyIds.ToList()
            : new List<string>();

        // v1: copy provider runs without resource/skill metadata cached on
        // the row; richer titles land with the resx lane.
        var (title, description) = _copy.GetItemCopy(activityType, vocabDueCount: null, resourceTitle: null, skillName: null);

        return new PlanItemDto
        {
            Id = row.PlanItemId,
            ActivityType = row.ActivityType,
            Title = title,
            Description = description,
            Priority = row.Priority,
            EstimatedMinutes = row.EstimatedMinutes,
            MinutesSpent = Math.Clamp(row.MinutesSpent, 0, MaxMinutesSpent),
            IsCompleted = row.IsCompleted,
            CompletedAtUtc = row.CompletedAt is null ? null : DateTime.SpecifyKind(row.CompletedAt.Value, DateTimeKind.Utc),
            ResourceId = row.ResourceId,
            ResourceTitle = null,
            SkillId = row.SkillId,
            SkillName = null,
            FocusVocabularyIds = itemFocusVocabularyIds,
            VocabDueCount = null,
            DifficultyLevel = null,
        };
    }

    /// <summary>
    /// Persist a minimal language-neutral facts JSON. v1 stores resource +
    /// vocab summary fields from the generator; richer facts (struggling
    /// tags, sample words, etc.) land with the narrative-localization-resx
    /// lane.
    /// </summary>
    private static (string? Rationale, string? Narrative, string? FocusVocabulary) SerializeFacts(PlanSkeleton skeleton)
    {
        var rationaleJson = PlanFactsSerializer.SerializeRationaleFacts(skeleton.ResourceSelectionReason);

        NarrativeFactsDto? narrativeDto = null;
        if (skeleton.Narrative is not null
            || skeleton.PrimaryResource is not null
            || skeleton.VocabularyReview is not null)
        {
            narrativeDto = new NarrativeFactsDto
            {
                Story = skeleton.Narrative?.Story,
                FocusAreas = skeleton.Narrative?.FocusAreas ?? new List<string>(),
                Resources = skeleton.Narrative?.Resources?.Select(r => new NarrativeResourceFactsDto
                {
                    Id = r.Id,
                    Title = r.Title,
                    MediaType = r.MediaType,
                    SelectionReason = r.SelectionReason,
                }).ToList() ?? new List<NarrativeResourceFactsDto>(),
                VocabInsight = skeleton.Narrative?.VocabInsight is { } vi
                    ? new NarrativeVocabInsightFactsDto
                    {
                        TotalDue = vi.TotalDue,
                        ReviewCount = vi.ReviewCount,
                        NewCount = vi.NewCount,
                        AverageMastery = vi.AverageMastery,
                        SampleStrugglingWords = vi.SampleStrugglingWords ?? new List<string>(),
                        PreviewWords = vi.PreviewWords?.Select(w => new NarrativePreviewWordFactsDto
                        {
                            WordId = w.WordId,
                            TargetTerm = w.TargetTerm,
                            NativeTerm = w.NativeTerm,
                        }).ToList() ?? new List<NarrativePreviewWordFactsDto>(),
                        StrugglingCategories = vi.StrugglingCategories?.Select(t => new NarrativeTagInsightFactsDto
                        {
                            Tag = t.Tag,
                            WordCount = t.WordCount,
                            AverageAccuracy = t.AverageAccuracy,
                            TotalAttempts = t.TotalAttempts,
                        }).ToList() ?? new List<NarrativeTagInsightFactsDto>(),
                        PatternInsight = vi.PatternInsight,
                    }
                    : null,
            };
        }
        var narrativeJson = PlanFactsSerializer.SerializeNarrativeFacts(narrativeDto);

        var focusVocabularyJson = PlanFactsSerializer.SerializeFocusVocabularyFacts(skeleton.FocusVocabularyIds);

        return (rationaleJson, narrativeJson, focusVocabularyJson);
    }

    private static List<string> DeserializeFocusVocabularyFacts(string? json) =>
        PlanFactsSerializer.DeserializeFocusVocabularyFacts(json);

    private static List<string> NormalizeFocusVocabularyIds(IEnumerable<string>? focusVocabularyIds) =>
        PlanFactsSerializer.NormalizeFocusVocabularyIds(focusVocabularyIds);

    private static bool ShouldUsePlanFocusVocabularyIds(PlanActivityType activityType)
    {
        return activityType is PlanActivityType.VocabularyReview
            or PlanActivityType.VocabularyGame
            or PlanActivityType.Cloze
            or PlanActivityType.Writing
            or PlanActivityType.Translation
            or PlanActivityType.Reading;
    }

    private string? TryDeserializeRationale(string? json)
    {
        var facts = PlanFactsSerializer.DeserializeRationaleFacts(json);
        if (facts is null || string.IsNullOrWhiteSpace(facts.ResourceSelectionReason))
        {
            return null;
        }
        return _copy.GetRationale(facts.ResourceSelectionReason!);
    }

    private PlanNarrativeDto? TryDeserializeNarrative(string? json)
    {
        var facts = PlanFactsSerializer.DeserializeNarrativeFacts(json);
        if (facts is null)
        {
            return null;
        }
        return new PlanNarrativeDto
        {
            Story = string.IsNullOrWhiteSpace(facts.Story) ? string.Empty : facts.Story!,
            FocusAreas = facts.FocusAreas?.Select(_copy.GetFocusArea).ToList() ?? new List<string>(),
            Resources = facts.Resources?.Select(r => new PlanResourceSummaryDto
            {
                Id = r.Id ?? string.Empty,
                Title = r.Title ?? string.Empty,
                MediaType = r.MediaType ?? string.Empty,
                SelectionReason = _copy.GetSelectionReason(r.SelectionReason ?? string.Empty),
            }).ToList() ?? new List<PlanResourceSummaryDto>(),
            VocabInsight = facts.VocabInsight is null ? null : new VocabInsightDto
            {
                TotalDue = facts.VocabInsight.TotalDue,
                ReviewCount = facts.VocabInsight.ReviewCount,
                NewCount = facts.VocabInsight.NewCount,
                AverageMastery = facts.VocabInsight.AverageMastery,
                SampleStrugglingWords = facts.VocabInsight.SampleStrugglingWords ?? new List<string>(),
                PreviewWords = facts.VocabInsight.PreviewWords?
                    .Where(w => !string.IsNullOrWhiteSpace(w.TargetTerm) && !string.IsNullOrWhiteSpace(w.NativeTerm))
                    .Select(w => new PlanPreviewWordDto
                    {
                        WordId = w.WordId ?? string.Empty,
                        TargetTerm = w.TargetTerm ?? string.Empty,
                        NativeTerm = w.NativeTerm ?? string.Empty,
                    }).ToList() ?? new List<PlanPreviewWordDto>(),
                StrugglingCategories = facts.VocabInsight.StrugglingCategories?
                    .Select(t => new TagInsightDto
                    {
                        Tag = t.Tag ?? string.Empty,
                        WordCount = t.WordCount,
                        AverageAccuracy = t.AverageAccuracy,
                        TotalAttempts = t.TotalAttempts,
                    }).ToList() ?? new List<TagInsightDto>(),
                PatternInsight = facts.VocabInsight.PatternInsight,
            },
        };
    }
}
