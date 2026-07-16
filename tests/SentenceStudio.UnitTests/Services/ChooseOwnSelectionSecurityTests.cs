using FluentAssertions;
using SentenceStudio.Abstractions;
using SentenceStudio.Services;

namespace SentenceStudio.UnitTests.Services;

public sealed class ChooseOwnSelectionSecurityTests
{
    [Fact]
    public void ProfileScopedKeys_IsolateTwoProfiles_AndIgnoreLegacyValues()
    {
        var preferences = new RecordingPreferences();
        preferences.Set(ChooseOwnSelectionPreferences.LegacyResourceIdsKey, "legacy-resource");
        preferences.Set(ChooseOwnSelectionPreferences.LegacySkillProfileIdKey, "legacy-skill");

        ChooseOwnSelectionPreferences.Save(
            preferences,
            "profile-a",
            new ChooseOwnSelection(["resource-a"], "skill-a"));
        ChooseOwnSelectionPreferences.Save(
            preferences,
            "profile-b",
            new ChooseOwnSelection(["resource-b"], "skill-b"));

        ChooseOwnSelectionPreferences.Load(preferences, "profile-a")
            .Should().BeEquivalentTo(new ChooseOwnSelection(["resource-a"], "skill-a"));
        ChooseOwnSelectionPreferences.Load(preferences, "profile-b")
            .Should().BeEquivalentTo(new ChooseOwnSelection(["resource-b"], "skill-b"));

        preferences.ReadKeys.Should().NotContain(
            ChooseOwnSelectionPreferences.LegacyResourceIdsKey);
        preferences.ReadKeys.Should().NotContain(
            ChooseOwnSelectionPreferences.LegacySkillProfileIdKey);
    }

    [Fact]
    public void EmptyProfile_PerformsNoPreferenceReadOrWrite()
    {
        var preferences = new RecordingPreferences();

        var loaded = ChooseOwnSelectionPreferences.Load(preferences, string.Empty);
        ChooseOwnSelectionPreferences.Save(
            preferences,
            " ",
            new ChooseOwnSelection(["resource-a"], "skill-a"));

        loaded.Should().BeEquivalentTo(new ChooseOwnSelection([], null));
        preferences.ReadKeys.Should().BeEmpty();
        preferences.WrittenKeys.Should().BeEmpty();
        preferences.RemovedKeys.Should().BeEmpty();
    }

    [Fact]
    public void ActivatingDifferentProfile_ResetsAllInMemorySelections()
    {
        var state = new ChooseOwnSelectionState();
        state.ActivateProfile("profile-a").Should().BeTrue();
        state.SetResources(["resource-a"]);
        state.SetSkill("skill-a");

        state.ActivateProfile("profile-b").Should().BeTrue();

        state.ProfileId.Should().Be("profile-b");
        state.ResourceIds.Should().BeEmpty();
        state.SkillId.Should().BeNull();
    }

    [Fact]
    public void ActivatingSameProfile_PreservesCurrentSelections()
    {
        var state = new ChooseOwnSelectionState();
        state.ActivateProfile("profile-a");
        state.SetResources(["resource-a"]);
        state.SetSkill("skill-a");

        state.ActivateProfile("profile-a").Should().BeFalse();

        state.ResourceIds.Should().Equal("resource-a");
        state.SkillId.Should().Be("skill-a");
    }

    [Fact]
    public void Reconcile_RejectsStaleValues_AndPersistsOnlyOwnedValues()
    {
        var preferences = new RecordingPreferences();
        var reconciliation = ChooseOwnSelectionPreferences.Reconcile(
            new ChooseOwnSelection(["owned-resource", "foreign-resource"], "foreign-skill"),
            ["owned-resource"],
            ["owned-skill"]);

        reconciliation.IsFullyOwned.Should().BeFalse(
            "mixed selections must refuse navigation instead of navigating with a filtered subset");
        reconciliation.RejectedCount.Should().Be(2);
        reconciliation.Selection.ResourceIds.Should().Equal("owned-resource");
        reconciliation.Selection.SkillId.Should().BeNull();

        ChooseOwnSelectionPreferences.Save(preferences, "profile-a", reconciliation.Selection);
        ChooseOwnSelectionPreferences.Load(preferences, "profile-a")
            .Should().BeEquivalentTo(reconciliation.Selection);
    }

    [Fact]
    public void Reconcile_WithNoOwnedOptions_ClearsBackingState()
    {
        var reconciliation = ChooseOwnSelectionPreferences.Reconcile(
            new ChooseOwnSelection(["stale-resource"], "stale-skill"),
            [],
            []);

        reconciliation.Selection.ResourceIds.Should().BeEmpty();
        reconciliation.Selection.SkillId.Should().BeNull();
        reconciliation.RejectedCount.Should().Be(2);
    }

    private sealed class RecordingPreferences : IPreferencesService
    {
        private readonly Dictionary<string, object?> _values = new(StringComparer.Ordinal);

        public List<string> ReadKeys { get; } = [];
        public List<string> WrittenKeys { get; } = [];
        public List<string> RemovedKeys { get; } = [];

        public T Get<T>(string key, T defaultValue)
        {
            ReadKeys.Add(key);
            return _values.TryGetValue(key, out var value) && value is T typed
                ? typed
                : defaultValue;
        }

        public void Set<T>(string key, T value)
        {
            WrittenKeys.Add(key);
            _values[key] = value;
        }

        public void Remove(string key)
        {
            RemovedKeys.Add(key);
            _values.Remove(key);
        }

        public void Clear() => _values.Clear();
    }
}
