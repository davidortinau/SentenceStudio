using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SentenceStudio.Abstractions;
using SentenceStudio.Data;
using SentenceStudio.Native;
using SentenceStudio.Native.Android;
using SentenceStudio.Services;

namespace SentenceStudio.Native.Android.Tests;

/// <summary>
/// Drives the Android Resources coordinator over the real shared presenter
/// with an in-memory owner-scoped store (no Android UI types involved).
/// </summary>
public sealed class AndroidResourcesCoordinatorTests
{
    private const string Owner = "android-owner-a";

    [Fact]
    public async Task AddWithPastedVocabulary_SavesForOwnerClosesFormAndReportsSuccess()
    {
        var (store, coordinator, _) = Create();
        coordinator.OnBrowserAppeared();
        await Until(() => coordinator.ListState.Phase == NativeResourceListPhase.Empty);

        coordinator.OpenCreate();
        Assert.True(coordinator.IsFormOpen);
        Assert.True(coordinator.IsCreating);
        Assert.Equal("Video", coordinator.FormState.MediaType);
        Assert.Equal("English", coordinator.FormState.Language);

        coordinator.ChangeDraft(NativeResourceField.Title, "Android add");
        coordinator.ChangeDraft(NativeResourceField.Language, "Korean");
        coordinator.PasteTextChanged("강, river\n산, mountain");
        coordinator.Save();

        await Until(() => !coordinator.IsFormOpen);
        Assert.Equal("ResourceAdd_SavedSuccess", coordinator.ListFeedback);
        var created = Assert.Single(store.Created);
        Assert.Equal(Owner, created.Owner);
        Assert.Equal("Android add", created.Draft.Title);
        Assert.Equal(2, created.Draft.StagedVocabulary.Length);
        await Until(() => coordinator.ListState.Phase == NativeResourceListPhase.Populated);
    }

    [Fact]
    public async Task DeleteRequiresConfirmationAndRemovesOnlyTheOwnersResource()
    {
        var (store, coordinator, _) = Create();
        store.Add("r1", "Mine");
        coordinator.OnBrowserAppeared();
        await Until(() => coordinator.ListState.Phase == NativeResourceListPhase.Populated);

        coordinator.Open("r1");
        await Until(() => coordinator.FormState.Phase == NativeResourceDetailPhase.Ready);
        coordinator.ConfirmDelete();
        await Task.Delay(50);
        Assert.Empty(store.Deleted);

        coordinator.RequestDelete();
        Assert.True(coordinator.FormState.ShowDeleteConfirmation);
        coordinator.CancelDelete();
        Assert.False(coordinator.FormState.ShowDeleteConfirmation);

        coordinator.RequestDelete();
        coordinator.ConfirmDelete();
        await Until(() => !coordinator.IsFormOpen);
        Assert.Equal(("r1", Owner), Assert.Single(store.Deleted));
        Assert.Equal("ResourceEdit_DeletedSuccess", coordinator.ListFeedback);
    }

    [Fact]
    public async Task StarterDuplicateGuardIsReportedWithoutCreatingAnother()
    {
        var (store, coordinator, _) = Create();
        store.StarterResult = NativeResourceMutationResult.AlreadyPresent;
        coordinator.OnBrowserAppeared();
        await Until(() => coordinator.ListState.Phase == NativeResourceListPhase.Empty);

        coordinator.CreateStarter();
        await Until(() => coordinator.ListFeedback is not null);
        Assert.Equal("Resources_StarterExists", coordinator.ListFeedback);
        Assert.Equal(1, store.StarterCalls);
        Assert.Empty(store.Created);
    }

    [Fact]
    public async Task FiltersAndViewModeUseTheSharedPresenterAndPersistTheMode()
    {
        var (store, coordinator, preferences) = Create();
        store.Add("v", "Video one");
        store.Add("a", "Article one", mediaType: "Article");
        coordinator.OnBrowserAppeared();
        await Until(() => coordinator.ListState.Phase == NativeResourceListPhase.Populated);

        coordinator.FilterType("Article");
        Assert.Equal("a", Assert.Single(coordinator.ListState.Items).Id);
        coordinator.FilterType(null);
        coordinator.Search("zzz");
        Assert.Equal(NativeResourceListPhase.NoMatches, coordinator.ListState.Phase);
        coordinator.SetViewMode(NativeResourceViewMode.List);
        Assert.Equal("list", preferences.Get("resources-view-mode", string.Empty));
    }

    [Fact]
    public async Task MissingOwnerFailsClosedWithoutOpeningAForm()
    {
        var (store, coordinator, preferences) = Create(activate: false);
        coordinator.OnBrowserAppeared();
        await Task.Delay(50);
        coordinator.OpenCreate();
        Assert.False(coordinator.IsFormOpen);
        Assert.Equal(0, store.ListCalls);
        Assert.Empty(store.Created);
        Assert.Equal(string.Empty,
            preferences.Get(UserProfileRepository.ActiveProfileIdKey, string.Empty));
    }

    [Fact]
    public void RouteAndShellSourcesExposeResourcesWithStableTags()
    {
        var browser = Source("AndroidSliceShell.Resources.Compose.cs");
        var form = Source("AndroidSliceShell.ResourceForm.Compose.cs");
        var compose = Source("AndroidSliceShell.Compose.cs");
        foreach (var tag in new[]
        {
            "NativeAccess.ResourceSearch", "NativeAccess.ResourceType",
            "NativeAccess.ResourceLanguage", "native.resources.view.grid",
            "native.resources.view.list", "native.resources.empty.add",
            "native.resources.empty.starter", "native.resources.add",
        })
            Assert.Contains(tag, browser);
        // The Library tab hosts the Resources page; Study keeps the Dashboard tab id.
        var tabs = Source("AndroidSliceShell.Tabs.Compose.cs");
        Assert.Contains("NativeAccess.DashboardTab", tabs);
        Assert.Contains("\"native.tab.library\"", tabs);
        Assert.Contains("\"native.library.row.resources\"", tabs);
        foreach (var tag in new[]
        {
            "NativeAccess.ResourceTitle", "NativeAccess.ResourceSave",
            "native.resources.form.delete.menu", "native.resources.edit.delete.confirm",
            "native.resources.edit.quick.search", "native.resources.edit.generate",
            "native.resources.form.vocabulary.paste", "native.resources.form.vocabulary.import",
            "native.resources.form.vocabulary.file",
        })
            Assert.Contains(tag, form);
        Assert.Contains("NativeDestination.Resources => RenderResources(palette)", compose);
        Assert.Contains("OpenResourceFromDashboard(resourceId!)", compose);
        Assert.DoesNotContain("Android.Widget", browser + form);
        Assert.DoesNotContain("AppCompat", browser + form);
    }

    private static (FakeStore Store, AndroidResourcesCoordinator Coordinator,
        ScratchPreferences Preferences) Create(bool activate = true)
    {
        var preferences = new ScratchPreferences();
        var scope = new DeviceUserScopeProvider();
        if (activate)
        {
            preferences.Set(UserProfileRepository.ActiveProfileIdKey, Owner);
            scope.SetActiveUser(Owner);
        }
        var store = new FakeStore();
        var presenter = new NativeResourcePresenter(scope, preferences, store,
            new OfflineSync(), new ServiceCollection()
                .AddSingleton<IPreferencesService>(preferences)
                .AddSentenceStudioNativeSlice()
                .BuildServiceProvider()
                .GetRequiredService<NativePendingWrites>(),
            NullLogger<NativeResourcePresenter>.Instance);
        return (store, new AndroidResourcesCoordinator(presenter,
            NullLogger.Instance, key => key), preferences);
    }

    private static async Task Until(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 200 && !condition(); attempt++)
            await Task.Delay(10);
        Assert.True(condition());
    }

    private static string Source(string name)
    {
        var root = typeof(AndroidResourcesCoordinatorTests).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .Single(attribute => attribute.Key == "NativeAndroidHeadRoot").Value!;
        return File.ReadAllText(Path.Combine(root, name));
    }

    private sealed class OfflineSync : INativeSyncReadiness
    {
        public Task<NativeSyncOutcome> SynchronizeAsync(
            string userId, CancellationToken cancellationToken) =>
            Task.FromResult(NativeSyncOutcome.Offline);
    }

    private sealed class ScratchPreferences : IPreferencesService
    {
        private readonly ConcurrentDictionary<string, object> _values = new();
        public T Get<T>(string key, T defaultValue) =>
            _values.TryGetValue(key, out var value) && value is T typed ? typed : defaultValue;
        public void Set<T>(string key, T value) => _values[key] = value!;
        public void Remove(string key) => _values.TryRemove(key, out _);
        public void Clear() => _values.Clear();
    }

    private sealed class FakeStore : INativeResourceStore
    {
        private static readonly DateTime Stamp = new(2026, 10, 1, 10, 0, 0, DateTimeKind.Utc);
        private readonly Dictionary<string, NativeResourceDetail> _resources = new();

        public List<(string Owner, NativeResourceDraft Draft)> Created { get; } = [];
        public List<(string Id, string Owner)> Deleted { get; } = [];
        public NativeResourceMutationResult StarterResult { get; set; } =
            NativeResourceMutationResult.Success;
        public int StarterCalls { get; private set; }
        public int ListCalls { get; private set; }

        public void Add(string id, string title, string mediaType = "Video") =>
            _resources[id] = new NativeResourceDetail(id, title, null, null, mediaType,
                "Korean", null, false, Stamp, Stamp, false, null, false, null, 0,
                new ResourceMetadataSnapshot(Stamp, title, null, null, "stamp"));

        public Task<IReadOnlyList<NativeResourceItem>> ListAsync(
            string owner, CancellationToken cancellationToken)
        {
            Assert.Equal(Owner, owner);
            ListCalls++;
            return Task.FromResult<IReadOnlyList<NativeResourceItem>>(_resources.Values
                .Select(resource => new NativeResourceItem(resource.Id, resource.Title,
                    resource.Description, resource.Tags, resource.MediaType,
                    resource.Language, false, resource.CreatedAt, resource.UpdatedAt))
                .ToArray());
        }

        public Task<NativeResourceDetail?> GetAsync(
            string owner, string resourceId, CancellationToken cancellationToken)
        {
            Assert.Equal(Owner, owner);
            return Task.FromResult(_resources.GetValueOrDefault(resourceId));
        }

        public Task<ResourceMetadataUpdateResult> SaveMetadataAsync(
            string owner, string resourceId, ResourceMetadataSnapshot original,
            string title, string? description, string? tags,
            CancellationToken cancellationToken) =>
            Task.FromResult(ResourceMetadataUpdateResult.Success);

        public Task<NativeResourceCreateResult> CreateAsync(
            string owner, NativeResourceDraft draft, CancellationToken cancellationToken)
        {
            Created.Add((owner, draft));
            Add("created", draft.Title);
            return Task.FromResult(new NativeResourceCreateResult(
                NativeResourceMutationResult.Success, "created"));
        }

        public Task<NativeResourceMutationResult> DeleteAsync(
            string owner, string resourceId, CancellationToken cancellationToken)
        {
            Deleted.Add((resourceId, owner));
            _resources.Remove(resourceId);
            return Task.FromResult(NativeResourceMutationResult.Success);
        }

        public Task<NativeResourceCreateResult> CreateStarterAsync(
            string owner, CancellationToken cancellationToken)
        {
            StarterCalls++;
            return Task.FromResult(new NativeResourceCreateResult(StarterResult));
        }
    }
}
