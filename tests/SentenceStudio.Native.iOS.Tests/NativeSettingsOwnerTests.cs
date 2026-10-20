using System.Collections.Concurrent;
using System.Reflection;
using System.Xml.Linq;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using SentenceStudio.Abstractions;
using SentenceStudio.Contracts.Theme;
using SentenceStudio.Data;
using SentenceStudio.Native;
using SentenceStudio.Native.Adapters;
using SentenceStudio.Native.iOS.Features.Settings;
using SentenceStudio.Native.iOS.Platform;
using SentenceStudio.Native.iOS.Styling;
using SentenceStudio.Services;
using SentenceStudio.Shared.Models;

namespace SentenceStudio.Native.iOS.Tests;

public sealed class NativeSettingsOwnerTests
{
    private const string OwnerA = "settings-owner-a";
    private const string OwnerB = "settings-owner-b";

#if DEBUG
    [Fact]
    public async Task ValidatedDebugFixtureReadsAndSavesOnlyLocalPreferences()
    {
        using var fixture = new SettingsSqlite();
        var preview = NativePreviewFixture.FromAttributes(
            PreviewMetadata(), NativeApiEndpoint.Validate(
                "http://127.0.0.1:5081/", true))!;
        var noAuth = new NativeAuthState(
            NativePhase.Loading, NativeDestination.SignIn);
        Assert.Equal(OwnerA, SettingsOwnerAccess.Resolve(
            noAuth, preview: true, debugBuild: true,
            SettingsOwnerAccess.FixtureBundleId, () => preview.Owner,
            fixture.Scope, fixture.Preferences, NullLogger.Instance));

        using var settings = fixture.CreateSettings(allowVoiceDiscovery: false);
        var beforeRows = fixture.SqliteChanges();
        Assert.True(settings.Open(preview.Owner));
        Assert.False(settings.State.VoiceLoading);
        Assert.Equal("Korean", settings.State.Quiz?.VoiceLanguage);
        await settings.LoadVoicesAsync(OwnerA, "Korean", CancellationToken.None);
        Assert.Equal(0, fixture.Speech.CallCount);
        Assert.Equal("NativeSettings_VoicesRequireSignIn",
            settings.State.VoiceErrorKey);
        Assert.False(string.IsNullOrWhiteSpace(settings.State.Quiz?.VoiceId));

        var selection = AppearanceSelection.Default.WithMode(ThemeMode.Light);
        Assert.Equal(selection, settings.SaveAppearance(selection)?.Selection);
        Assert.Equal(selection.ToToken(), fixture.Preferences.Get(
            SourceUiAppearance.PreferenceKey, ""));
        Assert.True(settings.Edit(draft => draft with {
            AutoplayAudio = false, AutoAdvanceSeconds = 4.5
        }));
        settings.SaveQuiz();
        Assert.Equal("Settings_PreferencesSaved", settings.State.FeedbackKey);
        Assert.False(settings.State.Quiz!.AutoplayAudio);
        Assert.Equal(4500d, settings.State.Quiz.AutoAdvanceSeconds * 1000);
        Assert.Equal(0, fixture.Speech.CallCount);

        settings.Close();
        using var reopened = fixture.CreateSettings(allowVoiceDiscovery: false);
        Assert.True(reopened.Open(OwnerA));
        Assert.False(reopened.State.Quiz!.AutoplayAudio);
        Assert.Equal(4.5, reopened.State.Quiz.AutoAdvanceSeconds);
        Assert.Equal(selection, SourceUiAppearance.Load(
            fixture.Preferences, preview.AppearanceToken,
            preferStoredPreviewAppearance: true).Selection);
        Assert.Equal(preview.AppearanceToken,
            SourceUiAppearance.Load(
                fixture.Preferences, preview.AppearanceToken).Selection.ToToken());
        Assert.Equal(beforeRows, fixture.SqliteChanges());
        Assert.Equal(new[] { "Owned A", "Owned B" }, fixture.ProfileNames());
    }

    [Fact]
    public void LocalOnlySettingsSaveImmediatelyAndAfterVoiceLanguageChange()
    {
        using var fixture = new SettingsSqlite();
        using var settings = fixture.CreateSettings(allowVoiceDiscovery: false);
        Assert.True(settings.Open(OwnerA));
        Assert.False(settings.State.VoiceLoading);
        Assert.Equal("NativeSettings_VoicesRequireSignIn",
            settings.State.VoiceErrorKey);
        Assert.True(settings.Edit(draft => draft with { AutoplayAudio = false }));

        settings.SaveQuiz();

        Assert.Equal("Settings_PreferencesSaved", settings.State.FeedbackKey);
        Assert.False(fixture.Quiz.Read(OwnerA)!.AutoPlayAudio);
        Assert.Equal(0, fixture.Speech.CallCount);

        Assert.Equal(OwnerA, settings.SelectVoiceLanguage("English"));
        Assert.False(settings.State.VoiceLoading);
        Assert.Equal("NativeSettings_VoicesRequireSignIn",
            settings.State.VoiceErrorKey);
        var selectedVoice = settings.State.Quiz!.VoiceId;
        Assert.Equal(fixture.Quiz.Read(OwnerA, "English")!.VoiceId,
            selectedVoice);

        settings.SaveQuiz();

        Assert.Equal("Settings_PreferencesSaved", settings.State.FeedbackKey);
        Assert.Equal(selectedVoice, fixture.Preferences.Get(
            SpeechVoicePreferences.GetPreferenceKey("English"), ""));
        Assert.False(fixture.Quiz.Read(OwnerA, "English")!.AutoPlayAudio);
        Assert.Equal(0, fixture.Speech.CallCount);
    }

    [Fact]
    public void NonFixtureReleaseAndInvalidPreviewMetadataCannotBorrowActiveOwner()
    {
        using var fixture = new SettingsSqlite();
        var apparentlyAuthenticated = new NativeAuthState(
            NativePhase.Ready, NativeDestination.Dashboard, OwnerA);
        var previewCalls = 0;
        string? PreviewOwner()
        {
            previewCalls++;
            return OwnerA;
        }
        Assert.Null(SettingsOwnerAccess.Resolve(apparentlyAuthenticated,
            preview: true, debugBuild: true,
            "com.simplyprofound.sentencestudio.native.review",
            PreviewOwner, fixture.Scope, fixture.Preferences, NullLogger.Instance));
        Assert.Null(SettingsOwnerAccess.Resolve(apparentlyAuthenticated,
            preview: true, debugBuild: false,
            SettingsOwnerAccess.FixtureBundleId, PreviewOwner,
            fixture.Scope, fixture.Preferences, NullLogger.Instance));
        Assert.Equal(0, previewCalls);

        var endpoint = NativeApiEndpoint.Validate(
            "http://127.0.0.1:5081/", true);
        Assert.Null(SettingsOwnerAccess.Resolve(apparentlyAuthenticated,
            preview: true, debugBuild: true,
            SettingsOwnerAccess.FixtureBundleId,
            () => NativePreviewFixture.FromAttributes([], endpoint)?.Owner,
            fixture.Scope, fixture.Preferences, NullLogger.Instance));
        Assert.Null(SettingsOwnerAccess.Resolve(apparentlyAuthenticated,
            preview: true, debugBuild: true,
            SettingsOwnerAccess.FixtureBundleId,
            () => NativePreviewFixture.FromAttributes(
                PreviewMetadata().Where(item =>
                    item.Key != "SentenceStudio.Native.PreviewSchema"),
                endpoint)?.Owner,
            fixture.Scope, fixture.Preferences, NullLogger.Instance));
        Assert.Null(SettingsOwnerAccess.Resolve(apparentlyAuthenticated,
            preview: true, debugBuild: true,
            SettingsOwnerAccess.FixtureBundleId,
            () => NativePreviewFixture.FromAttributes(
                PreviewMetadata().Where(item =>
                    item.Key != "SentenceStudio.Native.PreviewOwnerId"),
                endpoint)?.Owner,
            fixture.Scope, fixture.Preferences, NullLogger.Instance));
        Assert.Null(SettingsOwnerAccess.Resolve(apparentlyAuthenticated,
            preview: true, debugBuild: true,
            SettingsOwnerAccess.FixtureBundleId,
            () => NativePreviewFixture.FromAttributes(
                PreviewMetadata(), NativeApiEndpoint.Validate(
                    "https://api.example.invalid/", false))?.Owner,
            fixture.Scope, fixture.Preferences, NullLogger.Instance));
        var foreign = NativePreviewFixture.FromAttributes(
            PreviewMetadata().Select(item =>
                item.Key == "SentenceStudio.Native.PreviewOwnerId"
                    ? new AssemblyMetadataAttribute(item.Key, OwnerB)
                    : item), endpoint)!;
        Assert.Null(SettingsOwnerAccess.Resolve(apparentlyAuthenticated,
            preview: true, debugBuild: true,
            SettingsOwnerAccess.FixtureBundleId,
            () => foreign.Owner, fixture.Scope, fixture.Preferences,
            NullLogger.Instance));
    }

    private static AssemblyMetadataAttribute[] PreviewMetadata() =>
    [
        new("SentenceStudio.Native.PreviewSurface", "dashboard"),
        new("SentenceStudio.Native.PreviewSchema", "true"),
        new("SentenceStudio.Native.PreviewOwnerId", OwnerA),
        new("SentenceStudio.Native.PreviewEmail", "preview@example.invalid"),
        new("SentenceStudio.Native.PreviewAppearance", "v1.seoul-pop.dark.100")
    ];
#endif

    [Fact]
    public void NormalSettingsRequireReadyAuthenticatedOwnerAndMatchingLocalScope()
    {
        using var fixture = new SettingsSqlite();
        var ready = new NativeAuthState(
            NativePhase.Ready, NativeDestination.Dashboard, OwnerA);
        string? PreviewMustNotRun() =>
            throw new InvalidOperationException("Normal auth never calls preview.");
        Assert.Equal(OwnerA, SettingsOwnerAccess.Resolve(
            ready, preview: false, debugBuild: false, null,
            PreviewMustNotRun, fixture.Scope, fixture.Preferences,
            NullLogger.Instance));
        Assert.Null(SettingsOwnerAccess.Resolve(
            new NativeAuthState(NativePhase.Loading, NativeDestination.SignIn),
            preview: false, debugBuild: true, null,
            PreviewMustNotRun, fixture.Scope, fixture.Preferences,
            NullLogger.Instance));
        Assert.Null(SettingsOwnerAccess.Resolve(
            ready with { UserId = OwnerB },
            preview: false, debugBuild: true, null,
            PreviewMustNotRun, fixture.Scope, fixture.Preferences,
            NullLogger.Instance));

        using var settings = fixture.CreateSettings();
        fixture.Preferences.Set(UserProfileRepository.ActiveProfileIdKey, OwnerB);
        Assert.Null(SettingsOwnerAccess.Resolve(
            ready, preview: false, debugBuild: true, null,
            PreviewMustNotRun, fixture.Scope, fixture.Preferences,
            NullLogger.Instance));
        Assert.False(settings.Open(OwnerA));
        Assert.Null(settings.State.Quiz);
        fixture.Preferences.Remove(UserProfileRepository.ActiveProfileIdKey);
        Assert.Null(SettingsOwnerAccess.Resolve(
            ready, preview: false, debugBuild: true, null,
            PreviewMustNotRun, fixture.Scope, fixture.Preferences,
            NullLogger.Instance));
        Assert.False(settings.Open(OwnerA));
        fixture.Preferences.Set(UserProfileRepository.ActiveProfileIdKey, OwnerA);
        fixture.Scope.SetActiveUser(null);
        Assert.Null(SettingsOwnerAccess.Resolve(
            ready, preview: false, debugBuild: true, null,
            PreviewMustNotRun, fixture.Scope, fixture.Preferences,
            NullLogger.Instance));
        Assert.False(settings.Open(OwnerA));
    }

    [Theory]
    [InlineData(OwnerB)]
    [InlineData(null)]
    [InlineData(OwnerA)]
    public void ScopeRevocationClearsDraftBeforeAnyFurtherSettingsAction(
        string? nextOwner)
    {
        using var fixture = new SettingsSqlite();
        using var settings = fixture.CreateSettings(allowVoiceDiscovery: false);
        Assert.True(settings.Open(OwnerA));
        Assert.True(settings.Edit(draft => draft with {
            VoiceId = "unsaved-private-voice", AutoplayAudio = false
        }));
        var beforeRows = fixture.SqliteChanges();
        var beforeWrites = fixture.Preferences.WriteCount;
        var beforeVersion = settings.ViewVersion;
        var notifications = new List<SettingsState>();
        settings.Changed += () => notifications.Add(settings.State);

        fixture.Scope.SetActiveUser(nextOwner);

        var blocked = Assert.Single(notifications);
        Assert.True(blocked.IsOpen);
        Assert.True(blocked.OwnerBlocked);
        Assert.Null(blocked.Quiz);
        Assert.Null(blocked.FeedbackKey);
        Assert.False(blocked.VoiceLoading);
        Assert.True(settings.ViewVersion > beforeVersion);
        Assert.False(settings.Edit(draft => draft with { Direction = "Mixed" }));
        Assert.Null(settings.SelectVoiceLanguage("French"));
        Assert.Null(settings.SaveAppearance(
            AppearanceSelection.Default.WithMode(ThemeMode.Light)));
        settings.SaveQuiz();
        Assert.Equal(beforeWrites, fixture.Preferences.WriteCount);
        Assert.Equal(beforeRows, fixture.SqliteChanges());
        Assert.Single(notifications);
    }

    [Fact]
    public async Task OldLanguageAndSameOwnerGenerationCannotPublishLateVoices()
    {
        using var fixture = new SettingsSqlite();
        using var settings = fixture.CreateSettings();
        Assert.True(settings.Open(OwnerA));
        var korean = fixture.Speech.Enqueue();
        var oldLoad = settings.LoadVoicesAsync(
            OwnerA, "Korean", CancellationToken.None);
        Assert.Equal(1, fixture.Speech.CallCount);
        Assert.Equal(OwnerA, settings.SelectVoiceLanguage("English"));
        var english = fixture.Speech.Enqueue();
        var currentLoad = settings.LoadVoicesAsync(
            OwnerA, "English", CancellationToken.None);
        Assert.Equal(2, fixture.Speech.CallCount);

        korean.SetResult([
            new NativeQuizVoice("stale-korean", "Old owner option", "Korean")
        ]);
        await oldLoad.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("English", settings.State.Quiz?.VoiceLanguage);
        Assert.DoesNotContain(settings.State.Quiz!.VoiceOptions,
            option => option.Id == "stale-korean");
        Assert.True(settings.State.VoiceLoading);

        fixture.Scope.SetActiveUser(OwnerA);
        Assert.True(settings.State.OwnerBlocked);
        english.SetResult([
            new NativeQuizVoice("stale-english", "Late option", "English")
        ]);
        await currentLoad.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Null(settings.State.Quiz);
        Assert.False(settings.State.VoiceLoading);
        Assert.Null(settings.State.VoiceErrorKey);
    }

    [Fact]
    public async Task SaveDuringVoiceDiscoveryWaitsForAStableVoiceSelection()
    {
        using var fixture = new SettingsSqlite();
        var voiceKey = SpeechVoicePreferences.GetPreferenceKey("Korean");
        fixture.Preferences.Set(voiceKey, "voice-v1");
        using var settings = fixture.CreateSettings();
        Assert.True(settings.Open(OwnerA));
        Assert.True(settings.State.VoiceLoading);
        Assert.Equal("voice-v1", settings.State.Quiz!.VoiceId);
        var voices = fixture.Speech.Enqueue();
        var pending = settings.LoadVoicesAsync(
            OwnerA, "Korean", CancellationToken.None);
        Assert.Equal(1, fixture.Speech.CallCount);
        Assert.True(settings.Edit(draft => draft with { AutoplayAudio = false }));
        var appearance = AppearanceSelection.Default.WithMode(ThemeMode.Light);
        Assert.Equal(appearance,
            settings.SaveAppearance(appearance)?.Selection);
        Assert.Equal(appearance.ToToken(), fixture.Preferences.Get(
            SourceUiAppearance.PreferenceKey, ""));
        var beforeWrites = fixture.Preferences.WriteCount;

        settings.SaveQuiz();

        Assert.Equal(beforeWrites, fixture.Preferences.WriteCount);
        Assert.Null(settings.State.FeedbackKey);
        Assert.Equal("voice-v1", fixture.Quiz.Read(OwnerA)!.VoiceId);
        Assert.True(fixture.Quiz.Read(OwnerA)!.AutoPlayAudio);

        voices.SetResult([
            new NativeQuizVoice("voice-v2", "Available voice", "Korean")
        ]);
        await pending.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(settings.State.VoiceLoading);
        Assert.Equal("voice-v2", settings.State.Quiz!.VoiceId);
        Assert.Equal("voice-v1", fixture.Preferences.Get(voiceKey, ""));
        Assert.Null(settings.State.FeedbackKey);

        settings.Close();
        using var reopened = fixture.CreateSettings();
        Assert.True(reopened.Open(OwnerA));
        Assert.Equal("voice-v1", reopened.State.Quiz!.VoiceId);
        Assert.True(reopened.State.Quiz.AutoplayAudio);
        Assert.Null(reopened.State.FeedbackKey);

        var renewedVoices = fixture.Speech.Enqueue();
        var renewed = reopened.LoadVoicesAsync(
            OwnerA, "Korean", CancellationToken.None);
        renewedVoices.SetResult([
            new NativeQuizVoice("voice-v2", "Available voice", "Korean")
        ]);
        await renewed.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("voice-v2", reopened.State.Quiz!.VoiceId);
        Assert.True(reopened.Edit(draft => draft with { AutoplayAudio = false }));
        reopened.SaveQuiz();
        Assert.Equal("Settings_PreferencesSaved", reopened.State.FeedbackKey);
        Assert.Equal("voice-v2", fixture.Preferences.Get(voiceKey, ""));
        Assert.False(fixture.Quiz.Read(OwnerA)!.AutoPlayAudio);

        reopened.Close();
        using var saved = fixture.CreateSettings();
        Assert.True(saved.Open(OwnerA));
        Assert.Equal("voice-v2", saved.State.Quiz!.VoiceId);
        Assert.False(saved.State.Quiz.AutoplayAudio);
        Assert.Null(saved.State.FeedbackKey);
    }

    [Fact]
    public async Task VoiceReplacementAfterSaveClearsSavedFeedbackUntilResaved()
    {
        using var fixture = new SettingsSqlite();
        var voiceKey = SpeechVoicePreferences.GetPreferenceKey("Korean");
        fixture.Preferences.Set(voiceKey, "voice-v1");
        using var settings = fixture.CreateSettings();
        Assert.True(settings.Open(OwnerA));
        var currentVoices = fixture.Speech.Enqueue();
        var firstLoad = settings.LoadVoicesAsync(
            OwnerA, "Korean", CancellationToken.None);
        currentVoices.SetResult([
            new NativeQuizVoice("voice-v1", "Current voice", "Korean")
        ]);
        await firstLoad.WaitAsync(TimeSpan.FromSeconds(5));
        settings.SaveQuiz();
        Assert.Equal("Settings_PreferencesSaved", settings.State.FeedbackKey);
        Assert.Equal("voice-v1", fixture.Preferences.Get(voiceKey, ""));

        var replacement = fixture.Speech.Enqueue();
        var refresh = settings.LoadVoicesAsync(
            OwnerA, "Korean", CancellationToken.None);
        Assert.True(settings.State.VoiceLoading);
        var beforeRefreshSave = fixture.Preferences.WriteCount;
        settings.SaveQuiz();
        Assert.Equal(beforeRefreshSave, fixture.Preferences.WriteCount);
        replacement.SetResult([
            new NativeQuizVoice("voice-v2", "New voice", "Korean")
        ]);
        await refresh.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal("voice-v2", settings.State.Quiz!.VoiceId);
        Assert.Equal("voice-v1", fixture.Preferences.Get(voiceKey, ""));
        Assert.Null(settings.State.FeedbackKey);
    }

    [Fact]
    public async Task CloseAndDisposeIgnoreCapturedCallbacksAndVoiceCompletions()
    {
        using var fixture = new SettingsSqlite();
        var settings = fixture.CreateSettings();
        Assert.True(settings.Open(OwnerA));
        var voices = fixture.Speech.Enqueue();
        var pending = settings.LoadVoicesAsync(
            OwnerA, "Korean", CancellationToken.None);
        var notifications = 0;
        settings.Changed += () => notifications++;
        settings.Close();
        var afterClose = settings.ViewVersion;
        fixture.Scope.SetActiveUser(OwnerB);
        Assert.Equal(afterClose, settings.ViewVersion);
        Assert.Equal(0, notifications);
        Assert.False(settings.Edit(draft => draft with { VoiceId = "closed" }));
        Assert.Null(settings.SaveAppearance(AppearanceSelection.Default));
        settings.SaveQuiz();
        voices.SetResult([
            new NativeQuizVoice("late", "After Back", "Korean")
        ]);
        await pending.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Null(settings.State.Quiz);
        Assert.Equal(0, notifications);

        fixture.Preferences.Set(UserProfileRepository.ActiveProfileIdKey, OwnerB);
        Assert.True(settings.Open(OwnerB));
        settings.Dispose();
        var afterDispose = settings.ViewVersion;
        fixture.Scope.SetActiveUser(null);
        Assert.Equal(afterDispose, settings.ViewVersion);
        Assert.Equal(0, notifications);
        Assert.Null(settings.State.Quiz);
    }

    [Fact]
    public void UnpersistedAppearanceOrQuizPreferenceNeverShowsSavedFeedback()
    {
        using var fixture = new SettingsSqlite();
        using var settings = fixture.CreateSettings(allowVoiceDiscovery: false);
        Assert.True(settings.Open(OwnerA));
        fixture.Preferences.DropKey = SourceUiAppearance.PreferenceKey;
        Assert.Null(settings.SaveAppearance(
            AppearanceSelection.Default.WithMode(ThemeMode.Light)));
        Assert.Equal(NativeText.SaveFailed, settings.State.FeedbackKey);
        fixture.Preferences.DropKey = "vocab_quiz_autoplay_vocab";
        Assert.True(settings.Edit(draft => draft with { AutoplayAudio = false }));
        settings.SaveQuiz();
        Assert.Equal(NativeText.SaveFailed, settings.State.FeedbackKey);
        Assert.False(settings.State.Quiz!.AutoplayAudio);
        Assert.True(fixture.Quiz.Read(OwnerA)!.AutoPlayAudio);
    }

    [Fact]
    public void ExistingAllOffSafeguardSavesTextInsteadOfAValuelessPrompt()
    {
        using var fixture = new SettingsSqlite();
        using var settings = fixture.CreateSettings(allowVoiceDiscovery: false);
        Assert.True(settings.Open(OwnerA));
        Assert.True(settings.Edit(draft => draft with {
            TextPrompt = false, AudioPrompt = false, PhotoPrompt = false
        }));

        settings.SaveQuiz();

        Assert.Equal("Settings_ModalityRequired", settings.State.FeedbackKey);
        var saved = fixture.Quiz.Read(OwnerA)!;
        Assert.True(saved.UseTextPrompt);
        Assert.False(saved.UseAudioPrompt);
        Assert.False(saved.UsePhotoPrompt);
    }

    [Fact]
    public void SameOwnerRepublishDuringSaveStopsFurtherPreferenceWrites()
    {
        using var fixture = new SettingsSqlite();
        using var settings = fixture.CreateSettings(allowVoiceDiscovery: false);
        Assert.True(settings.Open(OwnerA));
        Assert.True(settings.Edit(draft => draft with {
            AutoAdvanceSeconds = 4, AutoplayAudio = false
        }));
        var beforeWrites = fixture.Preferences.WriteCount;
        fixture.Preferences.AfterWrite = key =>
        {
            if (key == "vocab_quiz_auto_advance_duration")
                fixture.Scope.SetActiveUser(OwnerA);
        };

        settings.SaveQuiz();

        Assert.True(settings.State.OwnerBlocked);
        Assert.Null(settings.State.Quiz);
        Assert.Null(settings.State.FeedbackKey);
        Assert.Equal(beforeWrites + 1, fixture.Preferences.WriteCount);
        Assert.True(fixture.Quiz.Read(OwnerA)!.AutoPlayAudio);
    }

    [Theory]
    [InlineData("AppResources.resx")]
    [InlineData("AppResources.ko.resx")]
    public void OwnerAndUnauthenticatedVoiceFeedbackAreLocalized(string file)
    {
        var root = Path.GetFullPath(Path.Combine(
            Path.GetDirectoryName(CurrentFile())!,
            "../../src/SentenceStudio.Shared/Resources/Strings"));
        var entries = XDocument.Load(Path.Combine(root, file))
            .Descendants("data").ToArray();
        foreach (var key in new[]
        {
            "NativeSettings_OwnerUnavailable",
            "NativeSettings_CurrentVoice",
            "NativeSettings_VoicesRequireSignIn",
            "Settings_PreferencesSaved",
            "Settings_ModalityRequired"
        })
            Assert.Contains(entries, entry =>
                entry.Attribute("name")?.Value == key
                && !string.IsNullOrWhiteSpace(entry.Element("value")?.Value));
    }

    private static string CurrentFile(
        [System.Runtime.CompilerServices.CallerFilePath] string file = "") => file;

    private sealed class SettingsSqlite : IDisposable
    {
        private readonly SqliteConnection _connection =
            new("Data Source=:memory:");
        private readonly DbContextOptions<ApplicationDbContext> _options;

        internal SettingsSqlite()
        {
            _connection.Open();
            _options = new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseSqlite(_connection).Options;
            using (var db = new ApplicationDbContext(_options))
            {
                db.Database.EnsureCreated();
                db.UserProfiles.AddRange(
                    new UserProfile { Id = OwnerA, Name = "Owned A" },
                    new UserProfile { Id = OwnerB, Name = "Owned B" });
                db.SaveChanges();
            }
            Preferences.Set(UserProfileRepository.ActiveProfileIdKey, OwnerA);
            Scope.SetActiveUser(OwnerA);
            var voices = new SpeechVoicePreferences(
                NullLogger<SpeechVoicePreferences>.Instance, Preferences);
            var quiz = new VocabularyQuizPreferences(
                NullLogger<VocabularyQuizPreferences>.Instance,
                voices, Preferences);
            Quiz = new NativeQuizSettings(Scope, Preferences, quiz, voices, Speech);
        }

        internal DeviceUserScopeProvider Scope { get; } = new();
        internal MemoryPreferences Preferences { get; } = new();
        internal HeldVoices Speech { get; } = new();
        internal NativeQuizSettings Quiz { get; }

        internal SettingsCoordinator CreateSettings(
            bool allowVoiceDiscovery = true) =>
            new(Scope, Preferences, Quiz,
                NullLogger<SettingsCoordinator>.Instance,
                allowVoiceDiscovery);

        internal long SqliteChanges()
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "SELECT total_changes()";
            return Convert.ToInt64(command.ExecuteScalar());
        }

        internal string[] ProfileNames()
        {
            using var db = new ApplicationDbContext(_options);
            return db.UserProfiles.AsNoTracking().OrderBy(row => row.Name)
                .Select(row => row.Name!).ToArray();
        }

        public void Dispose() => _connection.Dispose();
    }

    private sealed class MemoryPreferences : IPreferencesService
    {
        private readonly ConcurrentDictionary<string, object> _values = new();
        private int _writes;

        internal string? DropKey { get; set; }
        internal Action<string>? AfterWrite { get; set; }
        internal int WriteCount => Volatile.Read(ref _writes);

        public T Get<T>(string key, T defaultValue) =>
            _values.TryGetValue(key, out var value) && value is T typed
                ? typed : defaultValue;

        public void Set<T>(string key, T value)
        {
            if (key == DropKey)
                return;
            _values[key] = value!;
            Interlocked.Increment(ref _writes);
            AfterWrite?.Invoke(key);
        }

        public void Remove(string key) => _values.TryRemove(key, out _);
        public void Clear() => throw new NotSupportedException();
    }

    private sealed class HeldVoices : INativeQuizSpeechSource
    {
        private readonly Queue<TaskCompletionSource<IReadOnlyList<NativeQuizVoice>>>
            _queued = new();
        internal int CallCount { get; private set; }

        internal TaskCompletionSource<IReadOnlyList<NativeQuizVoice>> Enqueue()
        {
            var next = new TaskCompletionSource<IReadOnlyList<NativeQuizVoice>>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            _queued.Enqueue(next);
            return next;
        }

        public Task<IReadOnlyList<NativeQuizVoice>> GetVoicesAsync(
            string owner, string language, CancellationToken cancellationToken)
        {
            CallCount++;
            return _queued.Dequeue().Task;
        }

        public Task<Stream?> SynthesizeAsync(
            string owner, string promptText, string voiceId,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
