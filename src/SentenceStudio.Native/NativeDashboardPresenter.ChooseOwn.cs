using System.Collections.Immutable;
using Microsoft.Extensions.Logging;
using SentenceStudio.Services;

namespace SentenceStudio.Native;

public sealed partial class NativeDashboardPresenter
{
    public static ImmutableArray<NativeChooseOwnActivity> ChooseOwnActivities { get; } =
    [
        new("conversation", "Activity_Conversation"),
        new("scene", "Activity_DescribeScene"),
        new("translation", "Activity_Translate"),
        new("cloze", "Activity_Cloze"),
        new("writing", "Activity_Writing"),
        new("diary", "Activity_Diary"),
        new("reading", "Activity_Reading"),
        new("vocab-quiz", "Activity_VocabQuiz"),
        new("vocab-matching", "Activity_VocabMatching"),
        new("shadowing", "Activity_Shadowing"),
        new("how-do-you-say", "Activity_HowDoYouSay"),
        new("minimal-pairs", "Activity_MinimalPairs"),
        new("word-association", "Activity_WordAssociation"),
        new("numberdrill", "Activity_NumberDrill")
    ];

    public async Task<NativeDashboardState> LoadChooseOwnAsync(
        CancellationToken cancellationToken = default)
    {
        if (!TryCurrentUser(out var owner))
            return SetState(new(NativePhase.Blocked, ErrorKey: NativeText.PlanError));
        EnsureMode(owner);
        if (_mode != NativeDashboardMode.ChooseOwn)
            return State;
        await LoadChooseOwnDataAsync(owner, cancellationToken);
        return SetState(_state with { ChooseOwn = _chooseOwn }, owner);
    }

    public NativeDashboardState SelectChooseOwn(
        IEnumerable<string>? resourceIds, string? skillId)
    {
        if (!TryCurrentUser(out var owner))
            return SetState(new(NativePhase.Blocked, ErrorKey: NativeText.PlanError));
        EnsureMode(owner);
        if (_mode != NativeDashboardMode.ChooseOwn || _chooseOwn is null
            || _chooseOwn.ErrorKey is not null)
            return SetState(_state with { ErrorKey = "NativeDashboard_ChooseOwnUnavailable" },
                owner);

        var reconciliation = ChooseOwnSelectionPreferences.Reconcile(
            new ChooseOwnSelection(resourceIds?.ToArray() ?? [], skillId),
            _chooseOwn.Resources.Select(option => option.Id),
            _chooseOwn.Skills.Select(option => option.Id));
        if (reconciliation.RejectedCount > 0)
        {
            logger.LogWarning(
                "Native Choose My Own refused {RejectedCount} unavailable selections.",
                reconciliation.RejectedCount);
            ChooseOwnSelectionPreferences.Save(preferences, owner,
                new ChooseOwnSelection([], null));
            _chooseOwn = _chooseOwn with {
                SelectedResourceIds = [],
                SelectedSkillId = null,
                ErrorKey = "NativeDashboard_SelectionUnavailable"
            };
        }
        else
        {
            ChooseOwnSelectionPreferences.Save(preferences, owner,
                reconciliation.Selection);
            _chooseOwn = _chooseOwn with {
                SelectedResourceIds = reconciliation.Selection.ResourceIds.ToImmutableArray(),
                SelectedSkillId = reconciliation.Selection.SkillId,
                ErrorKey = null
            };
        }
        return SetState(_state with { ChooseOwn = _chooseOwn }, owner);
    }

    public NativeDashboardState SetChooseOwnExpanded(bool expanded)
    {
        if (!TryCurrentUser(out var owner))
            return SetState(new(NativePhase.Blocked, ErrorKey: NativeText.PlanError));
        EnsureMode(owner);
        if (_mode != NativeDashboardMode.ChooseOwn || _chooseOwn is null)
            return State;
        _chooseOwn = _chooseOwn with { ShowAllActivities = expanded };
        return SetState(_state with { ChooseOwn = _chooseOwn }, owner);
    }

    public async Task<NativeQuizLaunch?> SelectChooseOwnQuizAsync(
        CancellationToken cancellationToken = default)
    {
        if (!TryCurrentUser(out var owner))
            return null;
        EnsureMode(owner);
        if (_mode != NativeDashboardMode.ChooseOwn
            || _chooseOwn is not { ErrorKey: null })
            return null;
        await LoadChooseOwnDataAsync(owner, cancellationToken);
        if (!TryCurrentUser(out var currentOwner) || currentOwner != owner
            || _chooseOwn is not { ErrorKey: null } selection)
        {
            if (currentOwner == owner)
                SetState(_state with { ChooseOwn = _chooseOwn }, owner);
            return null;
        }

        using var lease = plans.ForOwner(owner);
        if (!TryCurrentUser(out currentOwner) || currentOwner != owner)
            return null;
        SetState(_state with { ChooseOwn = selection }, owner);
        return new NativeQuizLaunch(owner, lease.LocalDate,
            string.Empty, selection.SelectedResourceIds, selection.SelectedSkillId,
            []);
    }

    private async Task LoadChooseOwnDataAsync(
        string owner, CancellationToken cancellationToken)
    {
        var generation = Interlocked.Increment(ref _chooseOwnGeneration);
        var stamp = userScope.CaptureStamp();
        try
        {
            var ownedResources = await resources.GetAllResourcesLightweightAsync(
                userProfileId: owner);
            cancellationToken.ThrowIfCancellationRequested();
            var ownedSkills = await skills.ListAsync(owner);
            cancellationToken.ThrowIfCancellationRequested();
            if (generation != Volatile.Read(ref _chooseOwnGeneration)
                || !userScope.IsCurrent(stamp)
                || !TryCurrentUser(out var current) || current != owner)
                return;

            var resourceOptions = ownedResources.Select(resource =>
                new NativeChooseOwnOption(resource.Id,
                    resource.Title ?? $"Resource {resource.Id}")).ToImmutableArray();
            var skillOptions = ownedSkills.Select(skill =>
                new NativeChooseOwnOption(skill.Id,
                    skill.Title ?? $"Skill {skill.Id}")).ToImmutableArray();
            var saved = ChooseOwnSelectionPreferences.Load(preferences, owner);
            var reconciled = ChooseOwnSelectionPreferences.Reconcile(
                saved, resourceOptions.Select(option => option.Id),
                skillOptions.Select(option => option.Id));
            ChooseOwnSelectionPreferences.Save(preferences, owner,
                reconciled.Selection);
            if (reconciled.RejectedCount > 0)
                logger.LogWarning(
                    "Native Choose My Own removed {RejectedCount} stale saved selections.",
                    reconciled.RejectedCount);
            _chooseOwn = new(resourceOptions, skillOptions,
                reconciled.Selection.ResourceIds.ToImmutableArray(),
                reconciled.Selection.SkillId,
                _chooseOwn?.ShowAllActivities ?? false,
                reconciled.RejectedCount > 0
                    ? "NativeDashboard_SelectionUnavailable" : null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Native Choose My Own selectors could not be loaded.");
            if (generation == Volatile.Read(ref _chooseOwnGeneration)
                && userScope.IsCurrent(stamp)
                && TryCurrentUser(out var current) && current == owner)
                _chooseOwn = new([], [], [], null,
                    ErrorKey: "NativeDashboard_ChooseOwnUnavailable");
        }
    }
}
