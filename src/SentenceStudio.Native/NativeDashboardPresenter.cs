using System.Collections.Immutable;
using System.Data;
using System.Data.Common;
using System.Net.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SentenceStudio.Abstractions;
using SentenceStudio.Contracts.Plans;
using SentenceStudio.Data;
using SentenceStudio.Services;
using SentenceStudio.Services.Plans;
using SentenceStudio.Services.Progress;
using SentenceStudio.Shared.Models;

namespace SentenceStudio.Native;

public sealed partial class NativeDashboardPresenter(
    NativePlanLeaseFactory plans,
    DeviceUserScopeProvider userScope,
    IPreferencesService preferences,
    INativeSyncReadiness sync,
    INativePlanCreation creation,
    IActivitySessionService sessions,
    NativePendingWrites pendingWrites,
    VocabularyProgressRepository vocabulary,
    LearningResourceRepository resources,
    SkillProfileRepository skills,
    ILogger<NativeDashboardPresenter> logger)
{
    private const string ModeKeyPrefix = "DashboardMode:";
    private NativeDashboardState _state = new(NativePhase.Idle);
    private string? _stateOwner;
    private string? _modeOwner;
    private NativeDashboardMode _mode = NativeDashboardMode.TodaysPlan;
    private ImmutableArray<NativeNumberContextStat> _numberContexts = [];
    private string? _numberErrorKey;
    private NativeChooseOwnState? _chooseOwn;
    private long _chooseOwnGeneration;

    public NativeDashboardState State =>
        _stateOwner is not null
        && (!TryCurrentUser(out var active) || active != _stateOwner)
            ? new(NativePhase.Blocked, ErrorKey: NativeText.PlanError)
            : _state;

    public event Action<NativeDashboardState>? StateChanged;

    public NativeDashboardState SetMode(NativeDashboardMode mode)
    {
        if (!TryCurrentUser(out var owner))
            return SetState(new(NativePhase.Blocked, ErrorKey: NativeText.PlanError));
        if (!Enum.IsDefined(mode))
            throw new ArgumentOutOfRangeException(nameof(mode));
        EnsureMode(owner);
        _mode = mode;
        preferences.Set(ModeKeyPrefix + owner,
            mode == NativeDashboardMode.TodaysPlan ? "TodaysPlan" : "ChooseOwn");
        return SetState(_state with { Mode = mode }, owner);
    }

    public Task<NativeDashboardState> LoadAsync(CancellationToken cancellationToken = default) =>
        LoadCoreAsync(generateIfMissing: true, synchronize: false, cancellationToken);

    public Task<NativeDashboardState> GenerateAsync(CancellationToken cancellationToken = default) =>
        LoadCoreAsync(generateIfMissing: true, synchronize: false, cancellationToken);

    public Task<NativeDashboardState> RefreshAsync(CancellationToken cancellationToken = default) =>
        LoadCoreAsync(generateIfMissing: true, synchronize: true, cancellationToken);

    public async Task<NativeDashboardState> RegenerateAsync(CancellationToken cancellationToken = default)
    {
        if (!TryCurrentUser(out var owner))
            return SetState(new(NativePhase.Blocked, ErrorKey: NativeText.PlanError));

        EnsureMode(owner);
        if (_mode != NativeDashboardMode.TodaysPlan)
            return State;
        SetState(new(NativePhase.Loading, HasPendingSync: pendingWrites.IsPending(owner)), owner);
        try
        {
            using var lease = plans.ForOwner(owner);
            if (!lease.Database.Database.IsSqlite())
                throw new NotSupportedException("Native plan regeneration requires SQLite.");

            var vocabSummary = await LoadVocabSummaryAsync(owner, cancellationToken);
            if (!TryCurrentUser(out var currentOwner) || currentOwner != owner)
                return SetState(new(NativePhase.Blocked, ErrorKey: NativeText.PlanError));
            await LoadNumberContextsAsync(lease.Database, owner, cancellationToken);

            await using var transaction = await lease.Database.Database.BeginTransactionAsync(
                IsolationLevel.Serializable, cancellationToken);
            if (!TryCurrentUser(out currentOwner) || currentOwner != owner)
                return SetState(new(NativePhase.Blocked, ErrorKey: NativeText.PlanError));

            var current = await lease.Service.GetTodayAsync(cancellationToken);
            if (!TryCurrentUser(out currentOwner) || currentOwner != owner)
                return SetState(new(NativePhase.Blocked, ErrorKey: NativeText.PlanError));

            var generated = await lease.GenerateNativeTodayAsync(cancellationToken);
            if (generated.GeneratedForDate != lease.LocalDate
                || current is { Items.Count: > 0 } && generated.Items.Count == 0)
                throw new InvalidOperationException(
                    "Plan regeneration returned an empty or mismatched replacement.");
            if (!TryCurrentUser(out currentOwner) || currentOwner != owner)
                return SetState(new(NativePhase.Blocked, ErrorKey: NativeText.PlanError));

            var resourceTitles = await GetOwnedResourceTitlesAsync(
                lease.Database, owner, generated.Items, cancellationToken);
            if (!TryCurrentUser(out currentOwner) || currentOwner != owner)
                return SetState(new(NativePhase.Blocked, ErrorKey: NativeText.PlanError));

            await transaction.CommitAsync(cancellationToken);
            pendingWrites.MarkPending(owner);
            return generated.Items.Count == 0
                ? SetState(new(NativePhase.Empty, generated.GeneratedForDate,
                    ErrorKey: NativeText.NoPlan, HasPendingSync: true,
                    VocabSummary: vocabSummary), owner)
                : SetState(new(NativePhase.Ready, generated.GeneratedForDate,
                    MapCards(generated.Items, resourceTitles), HasPendingSync: true,
                    CompletedCount: generated.CompletedCount,
                    TotalCount: generated.TotalCount,
                    EstimatedTotalMinutes: generated.EstimatedTotalMinutes,
                    MinutesSpent: generated.Items.Sum(item => item.MinutesSpent),
                    CompletionPercentage: generated.CompletionPercentage,
                    CurrentStreak: generated.Streak.CurrentStreak,
                    LongestStreak: generated.Streak.LongestStreak,
                    Narrative: generated.Narrative,
                    Rationale: generated.Rationale,
                    VocabSummary: vocabSummary), owner);
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning(ex, "Native plan regeneration failed while offline.");
            return SetState(new(NativePhase.Error,
                ErrorKey: NativeText.PlanNetworkError, HasPendingSync: true), owner);
        }
        catch (Exception ex) when (ex is DbUpdateException or DbException
            or InvalidOperationException or NotSupportedException)
        {
            logger.LogError(ex, "Native plan regeneration was not committed.");
            return SetState(new(NativePhase.Error,
                ErrorKey: NativeText.PlanGenerationError, HasPendingSync: true), owner);
        }
    }

    public async Task<NativeQuizLaunch?> SelectAssignedQuizAsync(
        string planItemId, CancellationToken cancellationToken = default)
    {
        if (!TryCurrentUser(out var userId) || string.IsNullOrWhiteSpace(planItemId))
        {
            logger.LogWarning("Native plan card launch refused an absent owner or item.");
            return null;
        }

        using var lease = plans.ForOwner(userId);
        var plan = await lease.Service.GetTodayAsync(cancellationToken);
        if (!TryCurrentUser(out var currentUser) || currentUser != userId
            || plan is null)
            return null;

        var item = plan.Items.SingleOrDefault(card =>
            card.Id == planItemId && card.ActivityType == nameof(PlanActivityType.VocabularyReview));
        if (item?.IsCompleted == true)
            await ReconcileCompletedQuizSessionsAsync(userId, plan, cancellationToken);
        if (item is null)
        {
            logger.LogWarning("Native quiz card launch refused an unavailable item.");
            return null;
        }

        return new NativeQuizLaunch(
            userId,
            plan.GeneratedForDate,
            item.Id,
            item.ResourceId is null ? [] : [item.ResourceId],
            item.SkillId,
            item.FocusVocabularyIds.ToImmutableArray())
        {
            IsRepractice = item.IsCompleted
        };
    }

    private async Task<NativeDashboardState> LoadCoreAsync(
        bool generateIfMissing, bool synchronize, CancellationToken cancellationToken)
    {
        if (!TryCurrentUser(out var owner))
        {
            logger.LogWarning("Native dashboard refused an absent or mismatched owner.");
            return SetState(new(NativePhase.Blocked, ErrorKey: NativeText.PlanError));
        }

        EnsureMode(owner);
        var pendingSync = State.Phase == NativePhase.Idle
            || State.HasPendingSync || pendingWrites.IsPending(owner);
        SetState(new(NativePhase.Loading, HasPendingSync: pendingSync), owner);
        try
        {
            var syncOutcome = NativeSyncOutcome.Unconfirmed;
            if (synchronize)
            {
                var syncRevision = pendingWrites.Revision(owner);
                syncOutcome = await sync.SynchronizeAsync(owner, cancellationToken);
                pendingSync = syncOutcome != NativeSyncOutcome.Confirmed;
                if (!pendingSync && TryCurrentUser(out var syncedOwner) && syncedOwner == owner)
                {
                    pendingWrites.MarkConfirmedIfUnchanged(owner, syncRevision);
                    pendingSync = pendingWrites.IsPending(owner);
                }
            }
            if (!TryCurrentUser(out var current) || current != owner)
                return SetState(new(NativePhase.Blocked, ErrorKey: NativeText.PlanError));

            using var lease = plans.ForOwner(owner);
            var planService = lease.Service;
            var hasResources = await lease.Database.LearningResources.AsNoTracking()
                .AnyAsync(resource => resource.UserProfileId == owner, cancellationToken);
            if (!hasResources)
            {
                if (!synchronize)
                {
                    var revision = pendingWrites.Revision(owner);
                    var readiness = await sync.SynchronizeAsync(owner, cancellationToken);
                    if (!TryCurrentUser(out var syncedOwner) || syncedOwner != owner)
                        return SetState(new(NativePhase.Blocked, ErrorKey: NativeText.PlanError));
                    if (readiness == NativeSyncOutcome.Confirmed)
                        pendingWrites.MarkConfirmedIfUnchanged(owner, revision);
                    pendingSync = readiness != NativeSyncOutcome.Confirmed
                        || pendingWrites.IsPending(owner);
                    hasResources = await lease.Database.LearningResources.AsNoTracking()
                        .AnyAsync(resource => resource.UserProfileId == owner,
                            cancellationToken);
                }
                var hasVocabulary = await lease.Database.VocabularyProgresses
                    .AsNoTracking()
                    .AnyAsync(progress => progress.UserId == owner,
                        cancellationToken);
                if (!hasResources && pendingSync)
                    return SetState(new(NativePhase.PendingSync,
                        HasPendingSync: true), owner);
                if (!hasResources && !hasVocabulary)
                    return SetState(new(NativePhase.Empty) {
                        IsNewUser = true
                    }, owner);
            }
            await LoadNumberContextsAsync(lease.Database, owner, cancellationToken);
            if (_mode == NativeDashboardMode.ChooseOwn)
                await LoadChooseOwnDataAsync(owner, cancellationToken);
            var plan = await planService.GetTodayAsync(cancellationToken);
            if (generateIfMissing && _mode == NativeDashboardMode.TodaysPlan
                && plan is null)
            {
                NativePlanCreateResult created;
                try
                {
                    created = await creation.CreateIfAbsentAsync(
                        owner, lease.LocalDate, cancellationToken);
                }
                catch (NotSupportedException ex)
                {
                    logger.LogWarning(ex, "Native plan create-if-absent requires a shared atomic operation.");
                    return SetState(new(NativePhase.Blocked,
                        ErrorKey: NativeText.PlanGenerationError), owner);
                }
                if (created.Plan.GeneratedForDate != lease.LocalDate)
                    throw new InvalidOperationException(
                        "Plan creation returned a different user-local date.");
                plan = await planService.GetTodayAsync(cancellationToken)
                    ?? throw new InvalidOperationException(
                        "Plan creation returned success without a persisted owner-scoped plan.");
                if (created.Created)
                {
                    pendingWrites.MarkPending(owner);
                    pendingSync = true;
                }
            }

            if (!TryCurrentUser(out current) || current != owner)
                return SetState(new(NativePhase.Blocked, ErrorKey: NativeText.PlanError));

            if (plan is not null)
            {
                await ReconcileCompletedQuizSessionsAsync(owner, plan, cancellationToken);
                if (!TryCurrentUser(out current) || current != owner)
                    return SetState(new(NativePhase.Blocked, ErrorKey: NativeText.PlanError));
            }

            var vocabSummary = await LoadVocabSummaryAsync(owner, cancellationToken);
            if (!TryCurrentUser(out current) || current != owner)
                return SetState(new(NativePhase.Blocked, ErrorKey: NativeText.PlanError));

            if (plan is null)
                return SetState(new(NativePhase.Empty,
                    ErrorKey: NativeText.NoPlan,
                    HasPendingSync: pendingSync,
                    VocabSummary: vocabSummary), owner);
            if (plan.Items.Count == 0)
                return SetState(new(NativePhase.Empty, plan.GeneratedForDate,
                    ErrorKey: generateIfMissing ? NativeText.PlanGenerationError : NativeText.NoPlan,
                    HasPendingSync: pendingSync,
                    VocabSummary: vocabSummary), owner);

            var resourceTitles = await GetOwnedResourceTitlesAsync(
                lease.Database, owner, plan.Items, cancellationToken);
            if (!TryCurrentUser(out current) || current != owner)
                return SetState(new(NativePhase.Blocked, ErrorKey: NativeText.PlanError));

            return SetState(new(NativePhase.Ready, plan.GeneratedForDate,
                MapCards(plan.Items, resourceTitles),
                HasPendingSync: pendingSync,
                CompletedCount: plan.CompletedCount,
                TotalCount: plan.TotalCount,
                EstimatedTotalMinutes: plan.EstimatedTotalMinutes,
                MinutesSpent: plan.Items.Sum(item => item.MinutesSpent),
                CompletionPercentage: plan.CompletionPercentage,
                CurrentStreak: plan.Streak.CurrentStreak,
                LongestStreak: plan.Streak.LongestStreak,
                Narrative: plan.Narrative,
                Rationale: plan.Rationale,
                VocabSummary: vocabSummary), owner);
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning(ex, "Native dashboard read failed while offline.");
            return SetState(new(NativePhase.Error,
                ErrorKey: NativeText.PlanNetworkError, HasPendingSync: true), owner);
        }
        catch (Exception ex) when (ex is DbUpdateException or DbException)
        {
            logger.LogError(ex, "Native dashboard plan generation could not be saved.");
            return SetState(new(NativePhase.Error,
                ErrorKey: NativeText.PlanGenerationError, HasPendingSync: true), owner);
        }
        catch (InvalidOperationException ex) when (generateIfMissing)
        {
            logger.LogWarning(ex, "Native dashboard could not generate a feasible plan.");
            return SetState(new(NativePhase.Error,
                ErrorKey: NativeText.PlanGenerationError, HasPendingSync: pendingSync), owner);
        }
    }

    private async Task ReconcileCompletedQuizSessionsAsync(
        string owner, TodaysPlanDto plan, CancellationToken cancellationToken)
    {
        foreach (var card in plan.Items.Where(item =>
            item.IsCompleted
            && item.ActivityType == nameof(PlanActivityType.VocabularyReview)))
        {
            if (!TryCurrentUser(out var active) || active != owner)
                throw new UnauthorizedAccessException(
                    "Plan owner changed before completed-session reconciliation.");

            var context = VocabQuizSessionSnapshot.BuildLaunchContextKey(
                card.Id, card.FocusVocabularyIds,
                card.ResourceId is null ? [] : [card.ResourceId],
                dueOnly: false, card.SkillId);
            var unfinished = await sessions.GetResumableAsync(
                owner, nameof(PlanActivityType.VocabularyReview), context);
            if (unfinished is null)
                continue;

            if (!TryCurrentUser(out active) || active != owner)
                throw new UnauthorizedAccessException(
                    "Plan owner changed before completed-session reconciliation.");
            await sessions.CompleteAsync(
                owner, nameof(PlanActivityType.VocabularyReview), context);
            logger.LogInformation("Completed an interrupted quiz session for an already-completed plan card.");
        }
    }

    private bool TryCurrentUser(out string userId)
    {
        if (userScope.TryGetUserProfileId(out userId)
            && !string.IsNullOrWhiteSpace(userId)
            && string.Equals(
                preferences.Get(UserProfileRepository.ActiveProfileIdKey, string.Empty),
                userId,
                StringComparison.Ordinal))
            return true;

        userId = string.Empty;
        return false;
    }

    private static async Task<Dictionary<string, string>> GetOwnedResourceTitlesAsync(
        ApplicationDbContext db, string owner, IEnumerable<PlanItemDto> items,
        CancellationToken cancellationToken)
    {
        var ids = items.Select(item => item.ResourceId)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id!)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (ids.Length == 0)
            return new(StringComparer.Ordinal);

        var resources = await db.LearningResources.AsNoTracking()
            .Where(resource => resource.UserProfileId == owner && ids.Contains(resource.Id))
            .Select(resource => new { resource.Id, resource.Title })
            .ToListAsync(cancellationToken);
        return resources.ToDictionary(resource => resource.Id, resource => resource.Title,
            StringComparer.Ordinal);
    }

    private async Task<VocabProgressSummary> LoadVocabSummaryAsync(
        string owner, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var (newCount, learning, familiar, review, known) =
            await vocabulary.GetVocabSummaryCountsAsync(owner);
        cancellationToken.ThrowIfCancellationRequested();
        var accuracy = await vocabulary.GetSuccessRate7dAsync(owner);
        cancellationToken.ThrowIfCancellationRequested();
        return new(newCount, learning, familiar, review, known, accuracy);
    }

    private async Task LoadNumberContextsAsync(
        ApplicationDbContext db, string owner, CancellationToken cancellationToken)
    {
        _numberContexts = [];
        _numberErrorKey = null;
        try
        {
            var targetLanguage = await db.UserProfiles.AsNoTracking()
                .Where(profile => profile.Id == owner)
                .Select(profile => profile.TargetLanguage)
                .SingleOrDefaultAsync(cancellationToken);
            if (!string.Equals(targetLanguage, "Korean", StringComparison.OrdinalIgnoreCase))
                return;
            var rows = await db.NumberMasteryProgresses.AsNoTracking()
                .Where(progress => progress.UserProfileId == owner
                    && progress.LanguageCode == "ko")
                .ToListAsync(cancellationToken);
            var now = DateTime.UtcNow;
            _numberContexts = rows.GroupBy(progress => progress.ContextCode)
                .Select(group =>
                {
                    var correct = group.Sum(item => item.CorrectCount);
                    var total = group.Sum(item => item.TotalCount);
                    return new NativeNumberContextStat(
                        group.Key, correct, total,
                        group.Count(item => item.DueDate <= now),
                        total > 0 ? (int)Math.Round(100.0 * correct / total) : 0);
                })
                .OrderBy(item => item.ContextCode, StringComparer.Ordinal)
                .ToImmutableArray();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Native dashboard number insight could not be loaded.");
            _numberErrorKey = NativeText.NumberInsightError;
        }
    }

    private static ImmutableArray<NativePlanCard> MapCards(
        IEnumerable<PlanItemDto> items, IReadOnlyDictionary<string, string> resourceTitles) =>
        items.Select(item =>
        {
            if (!Enum.TryParse<PlanActivityType>(item.ActivityType, false, out var activity))
                throw new InvalidOperationException($"Unknown plan activity type: {item.ActivityType}");
            return new NativePlanCard(
                item.Id, activity, item.Title, item.Description,
                item.EstimatedMinutes, item.MinutesSpent, item.IsCompleted,
                item.ResourceId, item.SkillId,
                item.FocusVocabularyIds.ToImmutableArray(),
                TitleKey: activity switch
                {
                    PlanActivityType.VocabularyReview => "Activity_VocabularyReview",
                    PlanActivityType.Reading => "Activity_Reading",
                    PlanActivityType.Listening => "Activity_Listening",
                    PlanActivityType.VideoWatching => "Activity_VideoWatching",
                    PlanActivityType.Shadowing => "Activity_Shadowing",
                    PlanActivityType.Cloze => "Activity_ClozeExercise",
                    PlanActivityType.Translation => "Activity_Translate",
                    PlanActivityType.Writing => "Activity_Writing",
                    PlanActivityType.SceneDescription => "Activity_DescribeScene",
                    PlanActivityType.Conversation => "Activity_Conversation",
                    PlanActivityType.VocabularyGame => "Activity_VocabMatching",
                    PlanActivityType.NumberDrill => "Activity_NumberDrill",
                    _ => "Activity_Generic",
                },
                DescriptionKey: activity == PlanActivityType.VocabularyReview
                    ? NativeText.PlanVocabDescription : null,
                ResourceTitle: item.ResourceId is { } id
                    && resourceTitles.TryGetValue(id, out var title) ? title : null,
                VocabDueCount: item.VocabDueCount);
        }).ToImmutableArray();

    private NativeDashboardState SetState(
        NativeDashboardState value, string? expectedOwner = null)
    {
        if (expectedOwner is not null
            && (!TryCurrentUser(out var current) || current != expectedOwner))
        {
            value = new(NativePhase.Blocked, ErrorKey: NativeText.PlanError);
            expectedOwner = null;
        }
        if (expectedOwner is not null)
        {
            EnsureMode(expectedOwner);
            value = value with {
                Mode = _mode,
                NumberContexts = _numberContexts,
                NumberErrorKey = _numberErrorKey,
                ChooseOwn = _chooseOwn
            };
        }
        _stateOwner = expectedOwner;
        _state = value;
        StateChanged?.Invoke(State);
        return State;
    }

    private void EnsureMode(string owner)
    {
        if (_modeOwner == owner)
            return;
        _modeOwner = owner;
        _numberContexts = [];
        _numberErrorKey = null;
        _chooseOwn = null;
        _chooseOwnGeneration++;
        _mode = preferences.Get(ModeKeyPrefix + owner, "TodaysPlan") == "ChooseOwn"
            ? NativeDashboardMode.ChooseOwn : NativeDashboardMode.TodaysPlan;
    }
}
