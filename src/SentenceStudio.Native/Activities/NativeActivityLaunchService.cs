using System.Collections.Immutable;
using Microsoft.Extensions.Logging;
using SentenceStudio.Services;
using SentenceStudio.Services.Progress;
using SentenceStudio.Shared.Services;

namespace SentenceStudio.Native;

/// <summary>
/// Builds owner-validated launch contexts for registered activities from a
/// Today's Plan card or from the Choose My Own selection (activities.md 1.1).
/// Unregistered activities return null so heads keep them disabled.
/// </summary>
public sealed class NativeActivityLaunchService(
    NativePlanLeaseFactory plans,
    NativeActivityOwner owner,
    VocabQuizLaunchValidator validator,
    ILogger<NativeActivityLaunchService> logger)
{
    public async Task<NativeActivityLaunch?> FromPlanCardAsync(
        string planItemId, CancellationToken cancellationToken = default)
    {
        if (!owner.TryCurrent(out var userId) || string.IsNullOrWhiteSpace(planItemId))
        {
            logger.LogWarning("Native activity card launch refused an absent owner or item.");
            return null;
        }

        using var lease = plans.ForOwner(userId);
        var plan = await lease.Service.GetTodayAsync(cancellationToken);
        if (!owner.IsActive(userId) || plan is null
            || plan.GeneratedForDate != lease.LocalDate)
            return null;
        var item = plan.Items.SingleOrDefault(card => card.Id == planItemId);
        if (item is null
            || !Enum.TryParse<PlanActivityType>(item.ActivityType, false, out var type)
            || NativeActivityCatalog.ForPlanType(type) is not { } definition)
        {
            logger.LogWarning("Native activity card launch refused an unavailable or unimplemented item.");
            return null;
        }

        var vocabularyDriven = NativeActivityCatalog.IsVocabularyDriven(type);
        return new NativeActivityLaunch(
            definition.Route, userId, plan.GeneratedForDate, item.Id,
            vocabularyDriven || item.ResourceId is null ? [] : [item.ResourceId],
            item.SkillId,
            FocusVocabularySelection.NormalizeFocusVocabularyIds(item.FocusVocabularyIds)
                .ToImmutableArray(),
            DueOnly: vocabularyDriven,
            TimerResourceId: item.ResourceId,
            ActivityType: type);
    }

    /// <summary>
    /// <paramref name="selection"/> is the dashboard's re-validated Choose My
    /// Own selection; every resource and the skill are validated again against
    /// the same owner, and the launch is refused if any is rejected.
    /// </summary>
    public async Task<NativeActivityLaunch?> FromChooseOwnAsync(
        NativeQuizLaunch selection, string route,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(selection);
        if (NativeActivityCatalog.ForRoute(route) is not { } definition
            || definition.PlanOnly
            || !owner.IsActive(selection.UserId))
            return null;
        var requested = selection.ResourceIds.IsDefault ? [] : selection.ResourceIds.ToArray();
        if (requested.Any(string.IsNullOrWhiteSpace)
            || requested.Distinct(StringComparer.Ordinal).Count() != requested.Length)
            return null;
        cancellationToken.ThrowIfCancellationRequested();
        var validated = await validator.ValidateRouteAsync(
            selection.UserId, requested, selection.SkillId);
        if (!validated.IsValid || validated.RejectedCount > 0
            || validated.Resources.Count != requested.Length
            || !owner.IsActive(selection.UserId))
        {
            logger.LogWarning("Native Choose My Own activity launch refused an unowned selection.");
            return null;
        }

        var owned = validated.Resources.Select(resource => resource.Id).ToImmutableArray();
        var resources = NativeActivityCatalog.TakesResourceList(route)
            ? owned : owned.Take(1).ToImmutableArray();
        using var lease = plans.ForOwner(selection.UserId);
        return new NativeActivityLaunch(
            route, selection.UserId, lease.LocalDate, PlanItemId: null,
            resources, validated.Skill?.Id, [], DueOnly: false,
            TimerResourceId: resources.FirstOrDefault(),
            ActivityType: definition.TimerType);
    }
}
