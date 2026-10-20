using System.Collections.Immutable;
using SentenceStudio.Abstractions;
using SentenceStudio.Data;
using SentenceStudio.Services;
using SentenceStudio.Services.Plans;
using SentenceStudio.Shared.Services;

namespace SentenceStudio.Native;

/// <summary>Validates Choose Own context without generating Today's Plan.</summary>
public sealed class NativeQuizLaunchService(
    NativePlanLeaseFactory plans,
    DeviceUserScopeProvider scope,
    IPreferencesService preferences,
    VocabQuizLaunchValidator validator,
    LearningResourceRepository resources)
{
    public async Task<NativeQuizLaunch?> CreateChooseOwnAsync(
        string owner, IEnumerable<string>? resourceIds, string? skillId,
        bool dueOnly = false, IEnumerable<string>? focusVocabularyIds = null,
        CancellationToken cancellationToken = default)
    {
        bool HasOwner() =>
            !string.IsNullOrWhiteSpace(owner)
            && scope.TryGetUserProfileId(out var active)
            && active == owner
            && preferences.Get(UserProfileRepository.ActiveProfileIdKey, string.Empty)
                == owner;
        if (!HasOwner())
            return null;
        cancellationToken.ThrowIfCancellationRequested();
        var resourceList = resourceIds?.ToArray() ?? [];
        if (resourceList.Any(string.IsNullOrWhiteSpace)
            || resourceList.Distinct(StringComparer.Ordinal).Count()
                != resourceList.Length)
            return null;
        var route = await validator.ValidateRouteAsync(owner, resourceList, skillId);
        if (!route.IsValid || !HasOwner()
            || route.Resources.Count != resourceList.Length)
            return null;
        var focusRequested = focusVocabularyIds?.ToArray() ?? [];
        var focus = FocusVocabularySelection.NormalizeFocusVocabularyIds(focusRequested);
        if (focusRequested.Length != focus.Count)
            return null;
        if (focus.Count > 0)
        {
            var ownedWords = await resources.GetAllVocabularyWordsWithResourcesAsync(owner);
            if (!HasOwner() || FocusVocabularySelection
                .SelectFocusWords(ownedWords, focus).Count != focus.Count)
                return null;
        }
        using var lease = plans.ForOwner(owner);
        if (!HasOwner())
            return null;
        return new NativeQuizLaunch(
            owner, lease.LocalDate, string.Empty,
            route.Resources.Select(resource => resource.Id).ToImmutableArray(),
            route.Skill?.Id,
            focus.ToImmutableArray(),
            dueOnly);
    }
}
