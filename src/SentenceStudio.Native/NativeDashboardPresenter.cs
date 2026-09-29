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

public sealed class NativeDashboardPresenter(
    NativePlanLeaseFactory plans,
    DeviceUserScopeProvider userScope,
    IPreferencesService preferences,
    INativeSyncReadiness sync,
    INativePlanCreation creation,
    IActivitySessionService sessions,
    NativePendingWrites pendingWrites,
    ILogger<NativeDashboardPresenter> logger)
{
    private NativeDashboardState _state = new(NativePhase.Idle);
    private string? _stateOwner;

    public NativeDashboardState State =>
        _stateOwner is not null
        && (!TryCurrentUser(out var active) || active != _stateOwner)
            ? new(NativePhase.Blocked, ErrorKey: NativeText.PlanError)
            : _state;

    public event Action<NativeDashboardState>? StateChanged;

    public Task<NativeDashboardState> LoadAsync(CancellationToken cancellationToken = default) =>
        LoadCoreAsync(generateIfMissing: false, synchronize: false, cancellationToken);

    public Task<NativeDashboardState> GenerateAsync(CancellationToken cancellationToken = default) =>
        LoadCoreAsync(generateIfMissing: true, synchronize: false, cancellationToken);

    public Task<NativeDashboardState> RefreshAsync(CancellationToken cancellationToken = default) =>
        LoadCoreAsync(generateIfMissing: false, synchronize: true, cancellationToken);

    public async Task<NativeDashboardState> RegenerateAsync(CancellationToken cancellationToken = default)
    {
        if (!TryCurrentUser(out var owner))
            return SetState(new(NativePhase.Blocked, ErrorKey: NativeText.PlanError));

        SetState(new(NativePhase.Loading, HasPendingSync: pendingWrites.IsPending(owner)), owner);
        try
        {
            using var lease = plans.ForOwner(owner);
            if (!lease.Database.Database.IsSqlite())
                throw new NotSupportedException("Native plan regeneration requires SQLite.");

            await using var transaction = await lease.Database.Database.BeginTransactionAsync(
                IsolationLevel.Serializable, cancellationToken);
            if (!TryCurrentUser(out var currentOwner) || currentOwner != owner)
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

            await transaction.CommitAsync(cancellationToken);
            pendingWrites.MarkPending(owner);
            return generated.Items.Count == 0
                ? SetState(new(NativePhase.Empty, generated.GeneratedForDate,
                    ErrorKey: NativeText.NoPlan, HasPendingSync: true), owner)
                : SetState(new(NativePhase.Ready, generated.GeneratedForDate,
                    MapCards(generated.Items), HasPendingSync: true,
                    CompletedCount: generated.CompletedCount), owner);
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
        {
            await ReconcileCompletedQuizSessionsAsync(userId, plan, cancellationToken);
            return null;
        }
        if (item is null)
        {
            logger.LogWarning("Native quiz card launch refused an unavailable or completed item.");
            return null;
        }

        return new NativeQuizLaunch(
            userId,
            plan.GeneratedForDate,
            item.Id,
            item.ResourceId is null ? [] : [item.ResourceId],
            item.SkillId,
            item.FocusVocabularyIds.ToImmutableArray());
    }

    private async Task<NativeDashboardState> LoadCoreAsync(
        bool generateIfMissing, bool synchronize, CancellationToken cancellationToken)
    {
        if (!TryCurrentUser(out var owner))
        {
            logger.LogWarning("Native dashboard refused an absent or mismatched owner.");
            return SetState(new(NativePhase.Blocked, ErrorKey: NativeText.PlanError));
        }

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
            var plan = await planService.GetTodayAsync(cancellationToken);
            if (generateIfMissing && plan is null)
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

            if (plan is null)
                return SetState(new(NativePhase.Empty,
                    ErrorKey: NativeText.NoPlan,
                    HasPendingSync: pendingSync), owner);
            if (plan.Items.Count == 0)
                return SetState(new(NativePhase.Empty, plan.GeneratedForDate,
                    ErrorKey: generateIfMissing ? NativeText.PlanGenerationError : NativeText.NoPlan,
                    HasPendingSync: pendingSync), owner);

            return SetState(new(NativePhase.Ready, plan.GeneratedForDate,
                MapCards(plan.Items),
                HasPendingSync: pendingSync,
                CompletedCount: plan.CompletedCount), owner);
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

    private static ImmutableArray<NativePlanCard> MapCards(IEnumerable<PlanItemDto> items) =>
        items.Select(item =>
        {
            if (!Enum.TryParse<PlanActivityType>(item.ActivityType, false, out var activity))
                throw new InvalidOperationException($"Unknown plan activity type: {item.ActivityType}");
            return new NativePlanCard(
                item.Id, activity, item.Title, item.Description,
                item.EstimatedMinutes, item.MinutesSpent, item.IsCompleted,
                item.ResourceId, item.SkillId,
                item.FocusVocabularyIds.ToImmutableArray(),
                TitleKey: activity == PlanActivityType.VocabularyReview
                    ? NativeText.PlanVocabTitle : null,
                DescriptionKey: activity == PlanActivityType.VocabularyReview
                    ? NativeText.PlanVocabDescription : null);
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
        _stateOwner = expectedOwner;
        _state = value;
        StateChanged?.Invoke(State);
        return State;
    }
}
