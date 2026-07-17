using Microsoft.Extensions.Logging;
using SentenceStudio.Abstractions;
using SentenceStudio.Data;
using SentenceStudio.Shared.Models;

namespace SentenceStudio.Services;

public sealed record VocabQuizRouteValidation(
    bool IsValid,
    string UserId,
    IReadOnlyList<LearningResource> Resources,
    SkillProfile? Skill,
    int RejectedCount);

public sealed class VocabQuizLaunchValidator
{
    private readonly LearningResourceRepository _resourceRepository;
    private readonly SkillProfileRepository _skillRepository;
    private readonly IPreferencesService _preferences;
    private readonly ILogger<VocabQuizLaunchValidator> _logger;

    public VocabQuizLaunchValidator(
        LearningResourceRepository resourceRepository,
        SkillProfileRepository skillRepository,
        IPreferencesService preferences,
        ILogger<VocabQuizLaunchValidator> logger)
    {
        _resourceRepository = resourceRepository;
        _skillRepository = skillRepository;
        _preferences = preferences;
        _logger = logger;
    }

    public async Task<VocabQuizRouteValidation> ValidateRouteAsync(
        string? userId,
        IEnumerable<string>? resourceIds,
        string? skillId)
    {
        var normalizedUserId = NormalizeValue(userId);
        var normalizedResourceIds = NormalizeIds(resourceIds);
        var normalizedSkillId = NormalizeValue(skillId);

        if (normalizedUserId is null)
        {
            _logger.LogWarning("Vocab Quiz launch refused because no active user was available.");
            return new VocabQuizRouteValidation(
                false,
                string.Empty,
                [],
                null,
                normalizedResourceIds.Count + (normalizedSkillId is null ? 0 : 1) + 1);
        }
        if (!IsCurrentUser(normalizedUserId))
        {
            _logger.LogWarning("Vocab Quiz launch refused because the active profile changed.");
            return new VocabQuizRouteValidation(
                false,
                string.Empty,
                [],
                null,
                normalizedResourceIds.Count + (normalizedSkillId is null ? 0 : 1) + 1);
        }

        var resources = new List<LearningResource>(normalizedResourceIds.Count);
        var rejectedCount = 0;
        foreach (var resourceId in normalizedResourceIds)
        {
            var resource = await _resourceRepository.GetResourceAsync(resourceId, normalizedUserId);
            if (!IsCurrentUser(normalizedUserId))
            {
                _logger.LogWarning("Vocab Quiz launch refused because the active profile changed during validation.");
                return new VocabQuizRouteValidation(
                    false,
                    string.Empty,
                    [],
                    null,
                    normalizedResourceIds.Count + (normalizedSkillId is null ? 0 : 1) + 1);
            }
            if (resource is null)
                rejectedCount++;
            else
                resources.Add(resource);
        }

        SkillProfile? skill = null;
        if (normalizedSkillId is not null)
        {
            skill = await _skillRepository.GetSkillProfileAsync(normalizedSkillId, normalizedUserId);
            if (!IsCurrentUser(normalizedUserId))
            {
                _logger.LogWarning("Vocab Quiz launch refused because the active profile changed during validation.");
                return new VocabQuizRouteValidation(
                    false,
                    string.Empty,
                    [],
                    null,
                    normalizedResourceIds.Count + 2);
            }
            if (skill is null)
                rejectedCount++;
        }

        if (rejectedCount > 0)
        {
            _logger.LogWarning(
                "Vocab Quiz launch refused because {RejectedCount} requested references were unavailable.",
                rejectedCount);
        }

        return new VocabQuizRouteValidation(
            rejectedCount == 0,
            normalizedUserId,
            resources,
            skill,
            rejectedCount);
    }

    public static int CountRejectedSnapshotReferences(
        VocabQuizSessionSnapshot snapshot,
        string? expectedPlanItemId,
        IEnumerable<string>? expectedFocusVocabularyIds,
        IEnumerable<string>? expectedResourceIds,
        bool expectedDueOnly,
        string? expectedSkillId,
        IEnumerable<string>? reachableWordIds)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var rejectedCount = 0;
        if (!string.Equals(NormalizeValue(snapshot.PlanItemId), NormalizeValue(expectedPlanItemId), StringComparison.Ordinal))
            rejectedCount++;
        if (!SetEquals(snapshot.FocusVocabularyIds, expectedFocusVocabularyIds))
            rejectedCount++;
        if (!SetEquals(snapshot.ResourceIds, expectedResourceIds))
            rejectedCount++;
        if (snapshot.DueOnly != expectedDueOnly)
            rejectedCount++;
        if (!string.Equals(NormalizeValue(snapshot.SkillId), NormalizeValue(expectedSkillId), StringComparison.Ordinal))
            rejectedCount++;

        var reachable = NormalizeIds(reachableWordIds).ToHashSet(StringComparer.Ordinal);
        var batchWordIds = snapshot.BatchPool
            .Select(item => NormalizeValue(item.WordId))
            .ToList();
        var batchSet = batchWordIds
            .OfType<string>()
            .ToHashSet(StringComparer.Ordinal);

        if (batchWordIds.Count == 0)
            rejectedCount++;

        rejectedCount += CountRejectedWordReferences(batchWordIds, reachable, requiredContainer: null);
        rejectedCount += CountRejectedWordReferences(snapshot.RoundWordOrder, reachable, batchSet);
        rejectedCount += CountRejectedWordReferences(snapshot.SessionItemsWordIds, reachable, batchSet);

        return rejectedCount;
    }

    private static int CountRejectedWordReferences(
        IEnumerable<string?> references,
        IReadOnlySet<string> reachable,
        IReadOnlySet<string>? requiredContainer)
    {
        var rejectedCount = 0;
        foreach (var reference in references)
        {
            var normalized = NormalizeValue(reference);
            if (normalized is null
                || !reachable.Contains(normalized)
                || (requiredContainer is not null && !requiredContainer.Contains(normalized)))
            {
                rejectedCount++;
            }
        }

        return rejectedCount;
    }

    private static bool SetEquals(IEnumerable<string>? left, IEnumerable<string>? right) =>
        NormalizeIds(left).ToHashSet(StringComparer.Ordinal)
            .SetEquals(NormalizeIds(right));

    private static IReadOnlyList<string> NormalizeIds(IEnumerable<string>? ids) =>
        (ids ?? [])
            .Select(NormalizeValue)
            .OfType<string>()
            .Distinct(StringComparer.Ordinal)
            .ToList();

    private static string? NormalizeValue(string? value)
    {
        var normalized = value?.Trim();
        return string.IsNullOrEmpty(normalized) ? null : normalized;
    }

    private bool IsCurrentUser(string userId)
    {
        var activeUserId = _preferences.Get(UserProfileRepository.ActiveProfileIdKey, string.Empty);
        return string.Equals(activeUserId, userId, StringComparison.Ordinal);
    }
}
