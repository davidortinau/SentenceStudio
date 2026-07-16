using SentenceStudio.Abstractions;

namespace SentenceStudio.Services;

public sealed record ChooseOwnSelection(
    IReadOnlyList<string> ResourceIds,
    string? SkillId);

public sealed record ChooseOwnSelectionReconciliation(
    ChooseOwnSelection Selection,
    int RejectedCount)
{
    public bool IsFullyOwned => RejectedCount == 0;
}

public sealed class ChooseOwnSelectionState
{
    public string? ProfileId { get; private set; }
    public List<string> ResourceIds { get; } = [];
    public string? SkillId { get; private set; }

    public bool ActivateProfile(string? profileId)
    {
        var normalizedProfileId = NormalizeValue(profileId);
        if (string.Equals(ProfileId, normalizedProfileId, StringComparison.Ordinal))
            return false;

        ProfileId = normalizedProfileId;
        ResourceIds.Clear();
        SkillId = null;
        return true;
    }

    public void Replace(ChooseOwnSelection selection)
    {
        ResourceIds.Clear();
        ResourceIds.AddRange(ChooseOwnSelectionPreferences.NormalizeIds(selection.ResourceIds));
        SkillId = NormalizeValue(selection.SkillId);
    }

    public void SetResources(IEnumerable<string>? resourceIds)
    {
        ResourceIds.Clear();
        ResourceIds.AddRange(ChooseOwnSelectionPreferences.NormalizeIds(resourceIds));
    }

    public void SetSkill(string? skillId) => SkillId = NormalizeValue(skillId);

    private static string? NormalizeValue(string? value)
    {
        var normalized = value?.Trim();
        return string.IsNullOrEmpty(normalized) ? null : normalized;
    }
}

public static class ChooseOwnSelectionPreferences
{
    public const string LegacyResourceIdsKey = "SelectedResourceIds";
    public const string LegacySkillProfileIdKey = "SelectedSkillProfileId";

    private const string ResourceIdsKeyPrefix = "SelectedResourceIds";
    private const string SkillProfileIdKeyPrefix = "SelectedSkillProfileId";

    public static ChooseOwnSelection Load(IPreferencesService preferences, string? profileId)
    {
        ArgumentNullException.ThrowIfNull(preferences);

        var resourceKey = BuildScopedKey(ResourceIdsKeyPrefix, profileId);
        var skillKey = BuildScopedKey(SkillProfileIdKeyPrefix, profileId);
        if (resourceKey is null || skillKey is null)
            return new ChooseOwnSelection([], null);

        var resourceIds = NormalizeIds(
            preferences.Get(resourceKey, string.Empty)
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        var skillId = NormalizeValue(preferences.Get(skillKey, string.Empty));

        return new ChooseOwnSelection(resourceIds, skillId);
    }

    public static void Save(
        IPreferencesService preferences,
        string? profileId,
        ChooseOwnSelection selection)
    {
        ArgumentNullException.ThrowIfNull(preferences);

        var resourceKey = BuildScopedKey(ResourceIdsKeyPrefix, profileId);
        var skillKey = BuildScopedKey(SkillProfileIdKeyPrefix, profileId);
        if (resourceKey is null || skillKey is null)
            return;

        var resourceIds = NormalizeIds(selection.ResourceIds);
        if (resourceIds.Count == 0)
            preferences.Remove(resourceKey);
        else
            preferences.Set(resourceKey, string.Join(',', resourceIds));

        var skillId = NormalizeValue(selection.SkillId);
        if (skillId is null)
            preferences.Remove(skillKey);
        else
            preferences.Set(skillKey, skillId);
    }

    public static ChooseOwnSelectionReconciliation Reconcile(
        ChooseOwnSelection selection,
        IEnumerable<string>? ownedResourceIds,
        IEnumerable<string>? ownedSkillIds)
    {
        var requestedResources = NormalizeIds(selection.ResourceIds);
        var ownedResources = NormalizeIds(ownedResourceIds).ToHashSet(StringComparer.Ordinal);
        var reconciledResources = requestedResources
            .Where(ownedResources.Contains)
            .ToList();

        var requestedSkill = NormalizeValue(selection.SkillId);
        var ownedSkills = NormalizeIds(ownedSkillIds).ToHashSet(StringComparer.Ordinal);
        var reconciledSkill = requestedSkill is not null && ownedSkills.Contains(requestedSkill)
            ? requestedSkill
            : null;

        var rejectedCount = requestedResources.Count - reconciledResources.Count;
        if (requestedSkill is not null && reconciledSkill is null)
            rejectedCount++;

        return new ChooseOwnSelectionReconciliation(
            new ChooseOwnSelection(reconciledResources, reconciledSkill),
            rejectedCount);
    }

    public static IReadOnlyList<string> NormalizeIds(IEnumerable<string>? ids) =>
        (ids ?? [])
            .Select(id => id?.Trim())
            .Where(id => !string.IsNullOrEmpty(id))
            .OfType<string>()
            .Distinct(StringComparer.Ordinal)
            .ToList();

    private static string? BuildScopedKey(string prefix, string? profileId)
    {
        var normalizedProfileId = NormalizeValue(profileId);
        return normalizedProfileId is null ? null : $"{prefix}:{normalizedProfileId}";
    }

    private static string? NormalizeValue(string? value)
    {
        var normalized = value?.Trim();
        return string.IsNullOrEmpty(normalized) ? null : normalized;
    }
}
