using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SentenceStudio.Abstractions;
using SentenceStudio.Data;
using SentenceStudio.Native.Adapters;
using SentenceStudio.Services;
using SentenceStudio.Services.Progress;
using SentenceStudio.Shared.Models;

namespace SentenceStudio.Native.Tests;

/// <summary>
/// How Do You Say source contract (activities.md 13) against real SQLite and the
/// real native speech cache: voices, per-language voice save, speak/cache/history,
/// replay, delete, offline, owner refusal, no timer or plan rows, and Back while
/// a voice or speech request hangs.
/// </summary>
public sealed class NativeHowDoYouSayPresenterTests
{
    private const string Route = NativeActivityCatalog.HowDoYouSay;

    [Fact]
    public void Catalog_RegistersChooseOwnOnlyWithoutAPlanTypeOrTimer()
    {
        var definition = NativeActivityCatalog.ForRoute(Route);
        Assert.NotNull(definition);
        Assert.Empty(definition.PlanTypes);
        Assert.Null(definition.TimerType);
        Assert.Contains(NativeDashboardPresenter.ChooseOwnActivities, tile => tile.Route == Route);
        Assert.All(Enum.GetValues<PlanActivityType>(), type =>
            Assert.NotSame(definition, NativeActivityCatalog.ForPlanType(type)));
    }

    [Fact]
    public async Task Open_ListsVoicesWithGenderAndOnlyTheOwnersHistoryNewestFirst()
    {
        using var fixture = new NativeSliceFixture();
        using var audio = new ScratchAudioCache(fixture);
        await fixture.SeedAsync(NativeSliceFixture.UserA, wordCount: 1);
        await fixture.SeedAsync(NativeSliceFixture.UserB, wordCount: 1);
        await InsertHistoryAsync(fixture, NativeSliceFixture.UserA, "older", DateTime.UtcNow.AddHours(-2));
        await InsertHistoryAsync(fixture, NativeSliceFixture.UserA, "newer", DateTime.UtcNow.AddHours(-1));
        await InsertHistoryAsync(fixture, NativeSliceFixture.UserB, "foreign", DateTime.UtcNow);
        await SignInAsync(fixture);
        var test = Create(fixture);
        using var _ = test.Presenter;

        var state = await test.Presenter.OpenAsync(Launch(fixture));

        Assert.Equal(NativePhase.Ready, state.Phase);
        Assert.Equal("Korean", state.TargetLanguage);
        Assert.False(state.VoicesLoading);
        Assert.False(state.HistoryLoading);
        Assert.Equal(["Ji-Young (female)", "Min"], state.Voices.Select(voice => voice.Label));
        Assert.Equal(Voices(fixture).GetVoiceForLanguage("Korean"), state.SelectedVoiceId);
        Assert.Equal(["newer", "older"], state.History.Select(entry => entry.Phrase));
        Assert.Equal([(NativeSliceFixture.UserA, "Korean")], test.Source.VoiceRequests);
        // Source starts no timer and writes no plan/session row.
        Assert.Equal(0, await PlanRowCountAsync(fixture));
    }

    [Fact]
    public async Task Speak_SynthesizesCachesPlaysAddsHistoryAndClearsTheInput_ThenReusesTheCache()
    {
        using var fixture = new NativeSliceFixture();
        using var audio = new ScratchAudioCache(fixture);
        await fixture.SeedAsync(NativeSliceFixture.UserA, wordCount: 1);
        await SignInAsync(fixture);
        var test = Create(fixture);
        using var _ = test.Presenter;
        await test.Presenter.OpenAsync(Launch(fixture));

        Assert.False((await test.Presenter.SpeakAsync()).IsBusy);
        Assert.Empty(test.Source.Synthesized);

        test.Presenter.SetInput("안녕하세요");
        var spoken = await test.Presenter.SpeakAsync();

        var voice = Voices(fixture).GetVoiceForLanguage("Korean");
        Assert.Equal([(NativeSliceFixture.UserA, "안녕하세요", voice)], test.Source.Synthesized);
        Assert.Equal(string.Empty, spoken.Input);
        Assert.False(spoken.IsBusy);
        Assert.Equal(new NativeHowDoYouSayNotice(NativeHowDoYouSayNoticeKind.Success,
            NativeHowDoYouSayText.Generated), spoken.Notice);
        var entry = Assert.Single(spoken.History);
        Assert.Equal("안녕하세요", entry.Phrase);
        Assert.Equal(entry.Id, spoken.PlayingId);
        Assert.Equal([1.0f], test.Player.Rates);
        var row = await SingleRowAsync(fixture, entry.Id);
        Assert.Equal(NativeSliceFixture.UserA, row.UserProfileId);
        Assert.Equal(voice, row.VoiceId);
        Assert.True(File.Exists(Path.Combine(fixture.Root, "AudioCache", Path.GetFileName(row.AudioFilePath!))));

        test.Player.Finish();
        await WaitAsync(test.Presenter, state => state.PlayingId is null);

        // The same phrase and voice plays from the owner's cache: no new synthesis or row.
        test.Presenter.SetInput("안녕하세요");
        var again = await test.Presenter.SpeakAsync();
        Assert.Single(test.Source.Synthesized);
        Assert.Single(again.History);
        Assert.Equal(entry.Id, again.PlayingId);
        Assert.Equal(0, await PlanRowCountAsync(fixture));
    }

    [Fact]
    public async Task SelectVoice_SavesImmediatelyForTheTargetLanguageAndSpeaksWithIt()
    {
        using var fixture = new NativeSliceFixture();
        using var audio = new ScratchAudioCache(fixture);
        await fixture.SeedAsync(NativeSliceFixture.UserA, wordCount: 1);
        await SignInAsync(fixture);
        var test = Create(fixture);
        using var _ = test.Presenter;
        await test.Presenter.OpenAsync(Launch(fixture));

        var unknown = await test.Presenter.SelectVoiceAsync("not-listed");
        Assert.NotEqual("not-listed", unknown.SelectedVoiceId);

        var selected = await test.Presenter.SelectVoiceAsync("voice-min");
        Assert.Equal("voice-min", selected.SelectedVoiceId);
        Assert.Equal("voice-min", Voices(fixture).GetVoiceForLanguage("Korean"));

        test.Presenter.SetInput("감사합니다");
        await test.Presenter.SpeakAsync();
        Assert.Equal("voice-min", Assert.Single(test.Source.Synthesized).Voice);
    }

    [Fact]
    public async Task Offline_UncachedPhraseWarnsAndKeepsTheInput_FailedSynthesisReportsAnError()
    {
        using var fixture = new NativeSliceFixture();
        using var audio = new ScratchAudioCache(fixture);
        await fixture.SeedAsync(NativeSliceFixture.UserA, wordCount: 1);
        await SignInAsync(fixture);
        var test = Create(fixture);
        using var _ = test.Presenter;
        await test.Presenter.OpenAsync(Launch(fixture));

        fixture.SetConnectivity(false);
        test.Presenter.SetInput("안녕");
        var offline = await test.Presenter.SpeakAsync();
        Assert.Equal(new NativeHowDoYouSayNotice(NativeHowDoYouSayNoticeKind.Warning,
            NativeHowDoYouSayText.AudioOffline), offline.Notice);
        Assert.Equal("안녕", offline.Input);
        Assert.True(offline.History.IsDefaultOrEmpty);

        fixture.SetConnectivity(true);
        test.Source.Fails = true;
        var failed = await test.Presenter.SpeakAsync();
        Assert.Equal(NativeHowDoYouSayNoticeKind.Error, failed.Notice!.Kind);
        Assert.Equal("안녕", failed.Input);
        Assert.Empty(test.Player.Rates);
    }

    [Fact]
    public async Task History_TogglesPlaybackReportsMissingFilesAndDeletesRowAndFile()
    {
        using var fixture = new NativeSliceFixture();
        using var audio = new ScratchAudioCache(fixture);
        await fixture.SeedAsync(NativeSliceFixture.UserA, wordCount: 1);
        var cache = Path.Combine(fixture.Root, "AudioCache");
        Directory.CreateDirectory(cache);
        var file = Path.Combine(cache, "kept.mp3");
        await File.WriteAllBytesAsync(file, [4, 5, 6]);
        var withFile = await InsertHistoryAsync(fixture, NativeSliceFixture.UserA, "with file",
            DateTime.UtcNow, Path.Combine("/old-sandbox/AudioCache", "kept.mp3"));
        var missing = await InsertHistoryAsync(fixture, NativeSliceFixture.UserA, "missing",
            DateTime.UtcNow.AddMinutes(-1), Path.Combine(cache, "gone.mp3"));
        await SignInAsync(fixture);
        var test = Create(fixture);
        using var _ = test.Presenter;
        await test.Presenter.OpenAsync(Launch(fixture));

        var playing = await test.Presenter.TogglePlayAsync(withFile);
        Assert.Equal(withFile, playing.PlayingId);
        Assert.True(test.Player.IsPlaying);
        var paused = await test.Presenter.TogglePlayAsync(withFile);
        Assert.Null(paused.PlayingId);
        Assert.False(test.Player.IsPlaying);

        var notFound = await test.Presenter.TogglePlayAsync(missing);
        Assert.Equal(new NativeHowDoYouSayNotice(NativeHowDoYouSayNoticeKind.Warning,
            NativeHowDoYouSayText.FileNotFound), notFound.Notice);

        await test.Presenter.TogglePlayAsync(withFile);
        var deleted = await test.Presenter.DeleteAsync(withFile);
        Assert.Null(deleted.PlayingId);
        Assert.False(test.Player.IsPlaying);
        Assert.Equal(["missing"], deleted.History.Select(entry => entry.Phrase));
        Assert.Equal(NativeHowDoYouSayText.Deleted, deleted.Notice!.Key);
        Assert.False(File.Exists(file));
        Assert.Null(await FindRowAsync(fixture, withFile));
    }

    [Fact]
    public async Task ForeignOrLostOwner_IsBlockedAndNothingIsRead()
    {
        using var fixture = new NativeSliceFixture();
        using var audio = new ScratchAudioCache(fixture);
        await fixture.SeedAsync(NativeSliceFixture.UserA, wordCount: 1);
        await SignInAsync(fixture);
        var foreign = Create(fixture);
        using (foreign.Presenter)
        {
            var refused = await foreign.Presenter.OpenAsync(Launch(fixture, NativeSliceFixture.UserB));
            Assert.Equal(NativePhase.Blocked, refused.Phase);
            Assert.Empty(foreign.Source.VoiceRequests);
        }

        var test = Create(fixture);
        using var _ = test.Presenter;
        await test.Presenter.OpenAsync(Launch(fixture));
        fixture.LoseOwner();
        test.Presenter.SetInput("안녕");
        Assert.Equal(NativePhase.Blocked, (await test.Presenter.SpeakAsync()).Phase);
        Assert.Empty(test.Source.Synthesized);
    }

    [Fact]
    public async Task Back_ReturnsPromptlyWhileVoicesOrSpeechHang_AndNoLateRowIsWritten()
    {
        using var fixture = new NativeSliceFixture();
        using var audio = new ScratchAudioCache(fixture);
        await fixture.SeedAsync(NativeSliceFixture.UserA, wordCount: 1);
        await SignInAsync(fixture);

        var hangingVoices = Create(fixture, new ScratchHowDoYouSaySource { HangVoices = true });
        using (hangingVoices.Presenter)
        {
            var open = hangingVoices.Presenter.OpenAsync(Launch(fixture));
            await WaitAsync(hangingVoices.Presenter, state => state.VoicesLoading);
            var exit = await hangingVoices.Presenter.ExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(NativeDestination.TodaysPlan, exit.Destination);
            var opened = await open.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(NativeDestination.TodaysPlan, opened.Destination);
        }

        var source = new ScratchHowDoYouSaySource();
        var hangingSpeech = Create(fixture, source);
        using (hangingSpeech.Presenter)
        {
            await hangingSpeech.Presenter.OpenAsync(Launch(fixture));
            source.HangSynthesis = true;
            hangingSpeech.Presenter.SetInput("기다려요");
            var speak = hangingSpeech.Presenter.SpeakAsync();
            await WaitAsync(hangingSpeech.Presenter, state => state.IsBusy);
            var exit = await hangingSpeech.Presenter.ExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(NativeDestination.TodaysPlan, exit.Destination);
            var late = await speak.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(NativeDestination.TodaysPlan, late.Destination);
            Assert.Empty(hangingSpeech.Player.Rates);
        }
        using var scope = fixture.Scope();
        Assert.False(await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>()
            .StreamHistories.AnyAsync(row => row.Phrase == "기다려요"));
        Assert.Equal(0, await PlanRowCountAsync(fixture));
    }

    private sealed record TestPresenter(
        NativeHowDoYouSayPresenter Presenter, ScratchHowDoYouSaySource Source, ScratchShadowingPlayer Player);

    private static TestPresenter Create(NativeSliceFixture fixture, ScratchHowDoYouSaySource? source = null)
    {
        var provider = fixture.Provider;
        source ??= new ScratchHowDoYouSaySource();
        var owner = provider.GetRequiredService<NativeActivityOwner>();
        var history = new StreamHistoryRepository(provider, NullLogger<StreamHistoryRepository>.Instance);
        var speech = new NativeShadowingSpeech(owner,
            provider.GetRequiredService<IConnectivityService>(),
            provider.GetRequiredService<IFileSystemService>(),
            Voices(fixture), history, NullLogger<NativeShadowingSpeech>.Instance,
            source, new ScratchSignedIn());
        var player = new ScratchShadowingPlayer();
        var presenter = new NativeHowDoYouSayPresenter(owner,
            provider.GetRequiredService<UserProfileRepository>(), history, Voices(fixture),
            provider.GetRequiredService<IFileSystemService>(), speech, player,
            NullLogger<NativeHowDoYouSayPresenter>.Instance, source);
        return new(presenter, source, player);
    }

    private static SpeechVoicePreferences Voices(NativeSliceFixture fixture) =>
        fixture.Provider.GetRequiredService<SpeechVoicePreferences>();

    private static NativeActivityLaunch Launch(
        NativeSliceFixture fixture, string owner = NativeSliceFixture.UserA) =>
        new(Route, owner, new DateOnly(2026, 9, 28), null,
            [fixture.ResourceId(owner)], null, [], false, fixture.ResourceId(owner), null);

    private static async Task<int> InsertHistoryAsync(NativeSliceFixture fixture, string owner,
        string phrase, DateTime createdAt, string? path = null)
    {
        using var scope = fixture.Scope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var row = new StreamHistory
        {
            UserProfileId = owner,
            Phrase = phrase,
            VoiceId = "voice-x",
            AudioFilePath = path ?? Path.Combine(fixture.Root, "AudioCache", $"{phrase}.mp3"),
            CreatedAt = createdAt,
            UpdatedAt = createdAt
        };
        db.StreamHistories.Add(row);
        await db.SaveChangesAsync();
        return row.Id;
    }

    private static async Task<StreamHistory?> FindRowAsync(NativeSliceFixture fixture, int id)
    {
        using var scope = fixture.Scope();
        return await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>()
            .StreamHistories.AsNoTracking().SingleOrDefaultAsync(row => row.Id == id);
    }

    private static async Task<StreamHistory> SingleRowAsync(NativeSliceFixture fixture, int id) =>
        Assert.IsType<StreamHistory>(await FindRowAsync(fixture, id));

    private static async Task<int> PlanRowCountAsync(NativeSliceFixture fixture)
    {
        using var scope = fixture.Scope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        return await db.DailyPlanCompletions.CountAsync() + await db.ActivitySessions.CountAsync();
    }

    private static async Task<NativeHowDoYouSayState> WaitAsync(
        NativeHowDoYouSayPresenter presenter, Func<NativeHowDoYouSayState, bool> condition)
    {
        for (var i = 0; i < 200 && !condition(presenter.State); i++)
            await Task.Delay(10);
        Assert.True(condition(presenter.State));
        return presenter.State;
    }

    private static async Task SignInAsync(NativeSliceFixture fixture) =>
        Assert.Equal(NativePhase.Ready,
            (await fixture.Auth.SignInAsync(NativeSliceFixture.EmailA, "fixture-password")).Phase);
}

/// <summary>
/// The fixture removes only its own database files; this removes the task-owned
/// AudioCache the speech path writes under the fixture's AppData directory.
/// </summary>
internal sealed class ScratchAudioCache(NativeSliceFixture fixture) : IDisposable
{
    public void Dispose()
    {
        var directory = Path.Combine(fixture.Root, "AudioCache");
        if (Directory.Exists(directory))
            Directory.Delete(directory, recursive: true);
    }
}

internal sealed class ScratchHowDoYouSaySource : INativeQuizSpeechSource
{
    public bool HangVoices { get; set; }
    public bool HangSynthesis { get; set; }
    public bool Fails { get; set; }
    public List<(string Owner, string Language)> VoiceRequests { get; } = [];
    public List<(string Owner, string Text, string Voice)> Synthesized { get; } = [];

    public async Task<Stream?> SynthesizeAsync(
        string owner, string promptText, string voiceId, CancellationToken cancellationToken)
    {
        if (HangSynthesis)
            await Task.Delay(Timeout.Infinite, cancellationToken);
        if (Fails)
            throw new HttpRequestException("Scratch speech failed.", null, System.Net.HttpStatusCode.BadGateway);
        Synthesized.Add((owner, promptText, voiceId));
        return new MemoryStream([1, 2, 3]);
    }

    public async Task<IReadOnlyList<NativeQuizVoice>> GetVoicesAsync(
        string owner, string language, CancellationToken cancellationToken)
    {
        VoiceRequests.Add((owner, language));
        if (HangVoices)
            await Task.Delay(Timeout.Infinite, cancellationToken);
        return
        [
            new("voice-jiyoung", "Ji-Young", language, "female"),
            new("voice-min", "Min", language),
            new(" ", "Blank", language),
        ];
    }
}

internal sealed class ScratchSignedIn : IAuthService
{
    public bool IsSignedIn => true;
    public string? UserName => null;
    public Task<string?> GetAccessTokenAsync(string[] scopes) => Task.FromResult<string?>("token");
    public Task<AuthResult?> SignInAsync() => Task.FromResult<AuthResult?>(null);
    public Task<AuthResult?> SignInAsync(string email, string password) => Task.FromResult<AuthResult?>(null);
    public Task<AuthResult?> RegisterAsync(string email, string password, string displayName) =>
        Task.FromResult<AuthResult?>(null);
    public Task SignOutAsync() => Task.CompletedTask;
    public Task<bool> DeleteAccountAsync() => Task.FromResult(false);
    public Task<bool> ChangePasswordAsync(string currentPassword, string newPassword) => Task.FromResult(false);
    public Task<bool> HasStoredSessionAsync() => Task.FromResult(false);
}
