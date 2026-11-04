using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Reflection;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SentenceStudio.Abstractions;
using SentenceStudio.Data;
using SentenceStudio.Native;
using SentenceStudio.Native.Android;
using SentenceStudio.Services;

namespace SentenceStudio.Native.Android.Tests;

/// <summary>
/// AF-R06/AF-R14 (Android file import), AF-R12 (quick-add focus) and AF-R11
/// (lazy vocabulary rows): the coordinator over the real shared presenter with an
/// owner-checking in-memory store, plus source contracts for the Compose form and
/// the system document picker.
/// </summary>
public sealed class AndroidResourceVocabularyImportTests
{
    private const string Owner = "android-owner-a";

    [Fact]
    public async Task PickedTextFileStagesOnAddAndSavesForTheOwner()
    {
        var (store, coordinator) = Create();
        coordinator.OpenCreate();
        coordinator.ChangeDraft(NativeResourceField.Title, "File words");
        coordinator.ChangeDraft(NativeResourceField.Language, "Korean");

        var pick = Assert.IsType<long>(coordinator.BeginFilePick());
        Assert.True(coordinator.IsFilePickPending);
        coordinator.ImportFile(pick, Encoding.UTF8.GetBytes("강, river\n산, mountain"), "words.txt");

        await Until(() => coordinator.FormState.Vocabulary.Count == 2);
        Assert.False(coordinator.IsFilePickPending);
        Assert.Equal("ResourceAdd_FileImportStatus", coordinator.FormState.FileStatus);
        Assert.Equal("ResourceAdd_ImportedCount", coordinator.FormState.Status);
        Assert.Null(coordinator.FormState.ErrorKey);

        coordinator.Save();
        await Until(() => !coordinator.IsFormOpen);
        var created = Assert.Single(store.Created);
        Assert.Equal(Owner, created.Owner);
        Assert.Equal(["강", "산"], created.Draft.StagedVocabulary.Select(word => word.TargetTerm));
    }

    [Fact]
    public async Task PickedCsvImportsIntoTheOpenResourceThroughTheOwnersStore()
    {
        var (store, coordinator) = Create();
        store.Add("r1", "Mine");
        coordinator.Open("r1");
        await Until(() => coordinator.FormState.Phase == NativeResourceDetailPhase.Ready);
        Assert.Null(coordinator.BeginFilePick());

        coordinator.ToggleBulk();
        var pick = Assert.IsType<long>(coordinator.BeginFilePick());
        coordinator.ImportFile(pick, Encoding.UTF8.GetBytes("물,water\n불,fire\n"), "list.CSV");

        await Until(() => coordinator.FormState.FileStatus is not null);
        var import = Assert.Single(store.Imports);
        Assert.Equal((Owner, "r1"), (import.Owner, import.ResourceId));
        Assert.Equal(["물", "불"], import.Words.Select(word => word.TargetTerm));
        Assert.Equal("ResourceEdit_FileImportStatus", coordinator.FormState.FileStatus);
        Assert.Equal(2, coordinator.FormState.Vocabulary.Count);
    }

    [Theory]
    [InlineData("words.pdf", "NativeResources_UnsupportedFile")]
    [InlineData("empty.txt", "ResourceAdd_FileEmpty")]
    [InlineData("big.txt", "NativeResources_FileTooLarge")]
    public async Task RejectedFilesStageNothingAndExplainWhy(string name, string errorKey)
    {
        var (_, coordinator) = Create();
        coordinator.OpenCreate();
        var content = name switch
        {
            "empty.txt" => [],
            // The shell reads at most one byte past the limit.
            "big.txt" => new byte[NativeResourcePresenter.MaxVocabularyFileBytes + 1],
            _ => Encoding.UTF8.GetBytes("강, river"),
        };
        var pick = Assert.IsType<long>(coordinator.BeginFilePick());
        coordinator.ImportFile(pick, content, name);

        await Until(() => coordinator.FormState.ErrorKey is not null);
        Assert.Equal(errorKey, coordinator.FormState.ErrorKey);
        Assert.Empty(coordinator.FormState.Vocabulary);
        Assert.False(coordinator.FormState.IsImporting);
    }

    [Fact]
    public void CancelledOrUnreadablePickIsDistinctAndAStaleResultIsIgnored()
    {
        var (_, coordinator) = Create();
        coordinator.OpenCreate();

        var cancelled = Assert.IsType<long>(coordinator.BeginFilePick());
        Assert.Null(coordinator.BeginFilePick());
        coordinator.CancelFilePick(cancelled);
        Assert.False(coordinator.IsFilePickPending);
        Assert.Null(coordinator.FormState.FileStatus);

        var failed = Assert.IsType<long>(coordinator.BeginFilePick());
        coordinator.FilePickFailed(failed, "NativeResources_FileReadError");
        Assert.Equal("NativeResources_FileReadError", coordinator.FormState.FileStatus);

        var stale = Assert.IsType<long>(coordinator.BeginFilePick());
        Assert.True(coordinator.CloseForm());
        coordinator.OpenCreate();
        coordinator.ImportFile(stale, Encoding.UTF8.GetBytes("강, river"), "late.txt");
        Assert.Empty(coordinator.FormState.Vocabulary);
        Assert.Null(coordinator.FormState.FileStatus);
        Assert.False(coordinator.IsFilePickPending);
    }

    [Fact]
    public async Task QuickAddAttachAndCreateEachAskForFocusBack()
    {
        var (store, coordinator) = Create();
        store.Add("r1", "Mine");
        store.Searchable.Add(new NativeResourceVocabularyRow("w1", "강", "river"));
        coordinator.Open("r1");
        await Until(() => coordinator.FormState.Phase == NativeResourceDetailPhase.Ready);
        var before = coordinator.QuickFocusVersion;

        coordinator.QuickQueryChanged("강");
        await Until(() => coordinator.FormState.QuickAddResults.Count == 1);
        coordinator.AttachWord("w1");
        await Until(() => coordinator.QuickFocusVersion == before + 1);
        Assert.Equal(("r1", "w1"), Assert.Single(store.Attached));
        Assert.Equal(string.Empty, coordinator.QuickQuery);
        Assert.False(coordinator.FormState.IsQuickAddAttaching);

        coordinator.QuickQueryChanged("바다");
        await Until(() => coordinator.FormState.QuickAddResultsAvailable);
        coordinator.ExpandCreateWord();
        coordinator.QuickNativeTermChanged("sea");
        coordinator.CreateWord();
        await Until(() => coordinator.QuickFocusVersion == before + 2);
        Assert.Equal("바다", Assert.Single(store.CreatedWords).TargetTerm);
    }

    [Fact]
    public async Task FailedAttachDoesNotMoveFocus()
    {
        var (store, coordinator) = Create();
        store.Add("r1", "Mine");
        store.Searchable.Add(new NativeResourceVocabularyRow("w1", "강", "river"));
        store.AttachResult = NativeResourceMutationResult.Failed;
        coordinator.Open("r1");
        await Until(() => coordinator.FormState.Phase == NativeResourceDetailPhase.Ready);

        coordinator.QuickQueryChanged("강");
        await Until(() => coordinator.FormState.QuickAddResults.Count == 1);
        coordinator.AttachWord("w1");
        await Until(() => coordinator.FormState.ErrorKey is not null);
        Assert.Equal(0, coordinator.QuickFocusVersion);
        Assert.Equal("강", coordinator.QuickQuery);
    }

    [Fact]
    public void DocumentPickerIsTheOnlyNewActivityResultAndAcceptsTextAndCsvOnly()
    {
        var activity = Source("MainActivity.cs");
        var onCreate = Method(activity, "OnCreate");
        var register = onCreate.IndexOf("new ActivityResultContracts.OpenDocument()",
            StringComparison.Ordinal);
        Assert.True(register >= 0);
        Assert.True(register < onCreate.IndexOf("this.SetContent(", StringComparison.Ordinal));
        Assert.Equal(2, CountOf(activity, "RegisterForActivityResult("));
        Assert.Contains("\"text/plain\", \"text/csv\"", activity);
        Assert.DoesNotContain("TakePersistableUriPermission", activity);

        var file = Source("AndroidSliceShell.ResourceFile.cs");
        Assert.Contains("activity.LaunchVocabularyFilePicker(", file);
        Assert.Contains("NativeResourcePresenter.MaxVocabularyFileBytes + 1", file);
        Assert.Contains("_ = Task.Run(", file);
        Assert.Contains("ReferenceEquals(_resources, resources)", file);
        Assert.DoesNotContain("Android.Widget", file + activity);
        Assert.DoesNotContain("AppCompat", file + activity);
    }

    [Fact]
    public void ResourceFormIsOneLazyColumnWithAWordPerRowAndAFocusableQuickAdd()
    {
        var form = Source("AndroidSliceShell.ResourceForm.Compose.cs");
        Assert.Contains("new LazyColumn<ResourceFormRow>(fields,", form);
        Assert.Contains("Key = row => row.Key,", form);
        Assert.Contains("$\"vocabulary.word.{word.Id}\"", form);
        Assert.DoesNotContain(".VerticalScroll(", form);
        Assert.DoesNotContain("foreach (var word in state.Vocabulary)", form);
        // Source contract for the rows: word, count heading, No Vocab Yet, read-only smart.
        Assert.Contains("\"ResourceEdit_VocabularySection\"), state.Vocabulary.Count", form);
        Assert.Contains("\"native.resources.form.vocabulary.empty\"", form);
        Assert.Contains("if (!smart)\n            row.Add(RemoveWordButton(", form);
        Assert.Contains(".FocusRequester(quickFocus)", form);
        Assert.Contains("quickFocus.RequestFocus();", form);
        Assert.Contains("\"native.resources.form.vocabulary.file\", () => ChooseResourceFile(resources)", form);
        Assert.Contains("\"native.resources.form.vocabulary.file.status\"", form);
        Assert.DoesNotContain("file.unavailable", form);
        // The per-corner facade shape takes pixels; the segments must not use it.
        Assert.DoesNotContain("RoundedCorners(AndroidVisualTheme.CardRadius, ", form);
    }

    private static (FakeStore Store, AndroidResourcesCoordinator Coordinator) Create()
    {
        var preferences = new ScratchPreferences();
        var scope = new DeviceUserScopeProvider();
        preferences.Set(UserProfileRepository.ActiveProfileIdKey, Owner);
        scope.SetActiveUser(Owner);
        var store = new FakeStore();
        var presenter = new NativeResourcePresenter(scope, preferences, store,
            new OfflineSync(), new ServiceCollection()
                .AddSingleton<IPreferencesService>(preferences)
                .AddSentenceStudioNativeSlice()
                .BuildServiceProvider()
                .GetRequiredService<NativePendingWrites>(),
            NullLogger<NativeResourcePresenter>.Instance);
        return (store, new AndroidResourcesCoordinator(presenter,
            NullLogger.Instance, key => key));
    }

    private static async Task Until(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 300 && !condition(); attempt++)
            await Task.Delay(10);
        Assert.True(condition());
    }

    private static int CountOf(string source, string value)
    {
        var count = 0;
        for (var index = source.IndexOf(value, StringComparison.Ordinal); index >= 0;
             index = source.IndexOf(value, index + value.Length, StringComparison.Ordinal))
            count++;
        return count;
    }

    private static string Method(string source, string name)
    {
        var start = source.IndexOf($" {name}(", StringComparison.Ordinal);
        Assert.True(start >= 0, $"{name} not found.");
        var open = source.IndexOf('{', start);
        var depth = 0;
        for (var i = open; i < source.Length; i++)
        {
            depth += source[i] == '{' ? 1 : source[i] == '}' ? -1 : 0;
            if (depth == 0)
                return source[open..(i + 1)];
        }
        return source[open..];
    }

    private static string Source(string name)
    {
        var root = typeof(AndroidResourceVocabularyImportTests).Assembly
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

    /// <summary>In-memory owner-checking store: every call asserts the active owner.</summary>
    private sealed class FakeStore : INativeResourceStore
    {
        private static readonly DateTime Stamp = new(2026, 10, 1, 10, 0, 0, DateTimeKind.Utc);
        private readonly Dictionary<string, NativeResourceDetail> _resources = new();

        public List<(string Owner, NativeResourceDraft Draft)> Created { get; } = [];
        public List<(string Owner, string ResourceId, IReadOnlyList<NativeResourceVocabularyRow> Words)>
            Imports { get; } = [];
        public List<(string ResourceId, string WordId)> Attached { get; } = [];
        public List<NativeResourceVocabularyRow> CreatedWords { get; } = [];
        public List<NativeResourceVocabularyRow> Searchable { get; } = [];
        public NativeResourceMutationResult AttachResult { get; set; } =
            NativeResourceMutationResult.Success;

        public void Add(string id, string title) =>
            _resources[id] = new NativeResourceDetail(id, title, null, null, "Video",
                "Korean", null, false, Stamp, Stamp, false, null, false, null, 0,
                new ResourceMetadataSnapshot(Stamp, title, null, null, "stamp"))
            {
                Vocabulary = [],
            };

        public Task<IReadOnlyList<NativeResourceItem>> ListAsync(
            string owner, CancellationToken cancellationToken)
        {
            Assert.Equal(Owner, owner);
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
            Assert.Equal(Owner, owner);
            Created.Add((owner, draft));
            Add("created", draft.Title);
            return Task.FromResult(new NativeResourceCreateResult(
                NativeResourceMutationResult.Success, "created"));
        }

        public Task<IReadOnlyList<NativeResourceVocabularyRow>> SearchVocabularyAsync(
            string owner, string resourceId, string language, string query,
            CancellationToken cancellationToken)
        {
            Assert.Equal(Owner, owner);
            return Task.FromResult<IReadOnlyList<NativeResourceVocabularyRow>>(Searchable
                .Where(word => word.TargetTerm.Contains(query, StringComparison.Ordinal))
                .ToArray());
        }

        public Task<NativeResourceMutationResult> AttachVocabularyAsync(
            string owner, string resourceId, string wordId, string language,
            CancellationToken cancellationToken)
        {
            Assert.Equal(Owner, owner);
            if (AttachResult != NativeResourceMutationResult.Success)
                return Task.FromResult(AttachResult);
            Attached.Add((resourceId, wordId));
            Link(resourceId, Searchable.Single(word => word.Id == wordId));
            return Task.FromResult(NativeResourceMutationResult.Success);
        }

        public Task<NativeResourceMutationResult> CreateVocabularyAsync(
            string owner, string resourceId, NativeResourceVocabularyRow word, string language,
            CancellationToken cancellationToken)
        {
            Assert.Equal(Owner, owner);
            CreatedWords.Add(word);
            Link(resourceId, word);
            return Task.FromResult(NativeResourceMutationResult.Success);
        }

        public Task<NativeResourceImportResult> ImportVocabularyAsync(
            string owner, string resourceId, string? language,
            IReadOnlyList<NativeResourceVocabularyRow> words,
            CancellationToken cancellationToken)
        {
            Assert.Equal(Owner, owner);
            Imports.Add((owner, resourceId, words));
            foreach (var word in words)
                Link(resourceId, word);
            return Task.FromResult(new NativeResourceImportResult(words.Count, 0,
                NativeResourceMutationResult.Success));
        }

        private void Link(string resourceId, NativeResourceVocabularyRow word)
        {
            var resource = _resources[resourceId];
            _resources[resourceId] = resource with
            {
                Vocabulary = resource.Vocabulary.Add(word),
                VocabularyCount = resource.VocabularyCount + 1,
            };
        }
    }
}
