using System.Text.Json;
using Microsoft.EntityFrameworkCore;
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

    private static readonly JsonSerializerOptions FactsJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    };

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
                _logger.LogWarning(
                    "LLM generator returned null for user '{UserId}'. Falling back to deterministic.",
                    userId);
                skeleton = await _deterministic.GenerateAsync(userId, ct);
                strategy = "deterministic";
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex,
                "Plan generator threw for user '{UserId}'. Falling back to empty plan.", userId);
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
                    "Skipping unknown activity type '{ActivityType}' returned by generator for user '{UserId}'.",
                    activity.ActivityType, userId);
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

        var (rationaleFactsJson, narrativeFactsJson) = SerializeFacts(skeleton);

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

        return MapItem(row);
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
        var items = completions
            .OrderBy(c => c.Priority)
            .ThenBy(c => c.PlanItemId, StringComparer.Ordinal)
            .Select(MapItem)
            .ToList();

        var estimatedTotal = items.Sum(i => i.EstimatedMinutes);
        var completedCount = items.Count(i => i.IsCompleted);
        var totalCount = items.Count;
        var percent = totalCount == 0 ? 0d : (completedCount * 100d) / totalCount;

        var narrative = TryDeserializeNarrative(plan.NarrativeFacts);
        var rationale = TryDeserializeRationale(plan.RationaleFacts);

        return new TodaysPlanDto
        {
            GeneratedForDate = DateOnly.FromDateTime(plan.Date),
            GeneratedAtUtc = DateTime.SpecifyKind(plan.GeneratedAtUtc, DateTimeKind.Utc),
            Strategy = string.IsNullOrWhiteSpace(plan.Strategy) ? "deterministic" : plan.Strategy,
            Items = items,
            EstimatedTotalMinutes = estimatedTotal,
            CompletedCount = completedCount,
            TotalCount = totalCount,
            CompletionPercentage = percent,
            Streak = streak,
            Narrative = narrative,
            Rationale = rationale,
        };
    }

    private PlanItemDto MapItem(DailyPlanCompletion row)
    {
        var activityType = Enum.TryParse<PlanActivityType>(row.ActivityType, out var parsed)
            ? parsed
            : PlanActivityType.VocabularyReview;

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
    private static (string? Rationale, string? Narrative) SerializeFacts(PlanSkeleton skeleton)
    {
        string? rationaleJson = null;
        if (!string.IsNullOrWhiteSpace(skeleton.ResourceSelectionReason))
        {
            rationaleJson = JsonSerializer.Serialize(new RationaleFacts
            {
                ResourceSelectionReason = skeleton.ResourceSelectionReason,
            }, FactsJsonOptions);
        }

        string? narrativeJson = null;
        if (skeleton.Narrative is not null
            || skeleton.PrimaryResource is not null
            || skeleton.VocabularyReview is not null)
        {
            var facts = new NarrativeFacts
            {
                Story = skeleton.Narrative?.Story,
                FocusAreas = skeleton.Narrative?.FocusAreas ?? new List<string>(),
                Resources = skeleton.Narrative?.Resources?.Select(r => new NarrativeResourceFacts
                {
                    Id = r.Id,
                    Title = r.Title,
                    MediaType = r.MediaType,
                    SelectionReason = r.SelectionReason,
                }).ToList() ?? new List<NarrativeResourceFacts>(),
                VocabInsight = skeleton.Narrative?.VocabInsight is { } vi
                    ? new NarrativeVocabInsightFacts
                    {
                        TotalDue = vi.TotalDue,
                        ReviewCount = vi.ReviewCount,
                        NewCount = vi.NewCount,
                        AverageMastery = vi.AverageMastery,
                        SampleStrugglingWords = vi.SampleStrugglingWords ?? new List<string>(),
                        PreviewWords = vi.PreviewWords?.Select(w => new NarrativePreviewWordFacts
                        {
                            WordId = w.WordId,
                            TargetTerm = w.TargetTerm,
                            NativeTerm = w.NativeTerm,
                        }).ToList() ?? new List<NarrativePreviewWordFacts>(),
                        StrugglingCategories = vi.StrugglingCategories?.Select(t => new NarrativeTagInsightFacts
                        {
                            Tag = t.Tag,
                            WordCount = t.WordCount,
                            AverageAccuracy = t.AverageAccuracy,
                            TotalAttempts = t.TotalAttempts,
                        }).ToList() ?? new List<NarrativeTagInsightFacts>(),
                        PatternInsight = vi.PatternInsight,
                    }
                    : null,
            };
            narrativeJson = JsonSerializer.Serialize(facts, FactsJsonOptions);
        }

        return (rationaleJson, narrativeJson);
    }

    private string? TryDeserializeRationale(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }
        try
        {
            var facts = JsonSerializer.Deserialize<RationaleFacts>(json, FactsJsonOptions);
            if (string.IsNullOrWhiteSpace(facts?.ResourceSelectionReason))
            {
                return null;
            }
            return _copy.GetRationale(facts!.ResourceSelectionReason!);
        }
        catch (JsonException ex)
        {
            _logger.LogDebug(ex, "Failed to deserialize RationaleFacts; returning null.");
            return null;
        }
    }

    private PlanNarrativeDto? TryDeserializeNarrative(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }
        try
        {
            var facts = JsonSerializer.Deserialize<NarrativeFacts>(json, FactsJsonOptions);
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
        catch (JsonException ex)
        {
            _logger.LogDebug(ex, "Failed to deserialize NarrativeFacts; returning null.");
            return null;
        }
    }

    private sealed class RationaleFacts
    {
        public string? ResourceSelectionReason { get; set; }
    }

    private sealed class NarrativeFacts
    {
        public string? Story { get; set; }
        public List<string>? FocusAreas { get; set; }
        public List<NarrativeResourceFacts>? Resources { get; set; }
        public NarrativeVocabInsightFacts? VocabInsight { get; set; }
    }

    private sealed class NarrativeResourceFacts
    {
        public string? Id { get; set; }
        public string? Title { get; set; }
        public string? MediaType { get; set; }
        public string? SelectionReason { get; set; }
    }

    private sealed class NarrativeVocabInsightFacts
    {
        public int TotalDue { get; set; }
        public int ReviewCount { get; set; }
        public int NewCount { get; set; }
        public float AverageMastery { get; set; }
        public List<NarrativeTagInsightFacts>? StrugglingCategories { get; set; }
        public List<string>? SampleStrugglingWords { get; set; }
        public List<NarrativePreviewWordFacts>? PreviewWords { get; set; }
        public string? PatternInsight { get; set; }
    }

    private sealed class NarrativePreviewWordFacts
    {
        public string? WordId { get; set; }
        public string? TargetTerm { get; set; }
        public string? NativeTerm { get; set; }
    }

    private sealed class NarrativeTagInsightFacts
    {
        public string? Tag { get; set; }
        public int WordCount { get; set; }
        public float AverageAccuracy { get; set; }
        public int TotalAttempts { get; set; }
    }
}
