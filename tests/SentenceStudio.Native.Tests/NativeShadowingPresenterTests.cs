using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SentenceStudio.Data;
using SentenceStudio.Services.Progress;
using SentenceStudio.Shared.Models;

namespace SentenceStudio.Native.Tests;

/// <summary>
/// Shadowing source contract (activities.md 1.1-1.3 and 8) on the shared
/// activity foundation, against real SQLite: content modes, translation toggle,
/// speed, navigation, playback state, audio unavailable/failed, owner refusal,
/// and plan/ad-hoc timer rows for both Shadowing and Listening plan cards.
/// </summary>
public sealed class NativeShadowingPresenterTests
{
    private const string Route = NativeActivityCatalog.Shadowing;

    [Fact]
    public void Catalog_RegistersShadowingForShadowingAndListeningPlanTypes()
    {
        var definition = NativeActivityCatalog.ForRoute(Route);
        Assert.NotNull(definition);
        Assert.Equal(PlanActivityType.Shadowing, definition.TimerType);
        Assert.Same(definition, NativeActivityCatalog.ForPlanType(PlanActivityType.Listening));
        Assert.Same(definition, NativeActivityCatalog.ForPlanType(PlanActivityType.Shadowing));
        Assert.True(definition.TimerRecordsSkill);
        Assert.False(NativeActivityCatalog.IsVocabularyDriven(PlanActivityType.Shadowing));
        Assert.Contains(NativeDashboardPresenter.ChooseOwnActivities, tile => tile.Route == Route);
    }

    [Theory]
    [InlineData(nameof(PlanActivityType.Shadowing))]
    [InlineData(nameof(PlanActivityType.Listening))]
    public async Task PlanCard_StartsTheTimerWithTheCardTypeAndExitSavesItsRow(string type)
    {
        using var fixture = new NativeSliceFixture();
        await fixture.SeedAsync(NativeSliceFixture.UserA, wordCount: 3);
        fixture.SetPlanActivities(NativeSliceFixture.UserA,
            (nameof(PlanActivityType.VocabularyReview), null, null, 6, 1),
            (type, fixture.ResourceId(NativeSliceFixture.UserA), null, 15, 2));
        await SignInAsync(fixture);
        var dashboard = fixture.Dashboard;
        var plan = await dashboard.GenerateAsync();
        var card = plan.Cards.Single(item => item.Activity.ToString() == type);
        using (var lease = fixture.PlanLease(NativeSliceFixture.UserA))
            Assert.True(await lease.Service.UpdateProgressAsync(plan.PlanDate!.Value, card.Id, 2));

        var launch = await fixture.Provider.GetRequiredService<NativeActivityLaunchService>()
            .FromPlanCardAsync(card.Id);
        Assert.NotNull(launch);
        Assert.Equal(Route, launch.Route);
        Assert.Equal([fixture.ResourceId(NativeSliceFixture.UserA)], launch.ResourceIds.ToArray());
        Assert.Equal(Enum.Parse<PlanActivityType>(type), launch.ActivityType);

        var (presenter, _, player) = Create(fixture);
        using (presenter)
        {
            var state = await presenter.OpenAsync(launch);
            Assert.Equal(NativePhase.Ready, state.Phase);
            Assert.Equal(3, state.Count);
            var exit = await presenter.ExitAsync();
            Assert.Equal(NativeDestination.TodaysPlan, exit.Destination);
            Assert.Null(exit.ErrorKey);
        }
        var (row, _) = await fixture.RowsAsync(NativeSliceFixture.UserA, card.Id);
        Assert.Equal(type, row.ActivityType);
        // The validated timer resumed 2 saved minutes; exit saves, never resets.
        Assert.Equal(2, row.MinutesSpent);
        // Shadowing writes no vocabulary progress (source: plan minutes only).
        Assert.Equal(0, await fixture.AttemptCountAsync(NativeSliceFixture.UserA));
    }

    [Fact]
    public async Task VocabularyMode_UsesAsIsTermsAndTheSourcePromptForWordsInSourceOrder()
    {
        using var fixture = new NativeSliceFixture();
        await fixture.SeedAsync(NativeSliceFixture.UserA, wordCount: 3);
        await SetLexicalTypeAsync(fixture, fixture.WordId(NativeSliceFixture.UserA, 0), LexicalUnitType.Word);
        await SignInAsync(fixture);
        var ai = new ScratchShadowingAi
        {
            Response = new()
            {
                Sentences =
                [
                    new() { TargetLanguageText = "가방이 어디 있어요?", NativeLanguageText = "Where is the bag?",
                        PronunciationNotes = "Rising intonation at the end." }
                ]
            }
        };
        var (presenter, speech, player) = Create(fixture, ai);
        using var _ = presenter;

        var state = await presenter.OpenAsync(ChooseOwnLaunch(fixture));

        Assert.Equal(NativePhase.Ready, state.Phase);
        Assert.Equal(3, state.Count);
        // Phrase/Sentence/Unknown terms first (as-is, no notes), then AI sentences.
        Assert.All(state.Sentences.Take(2), sentence =>
        {
            Assert.StartsWith("target-", sentence.TargetText);
            Assert.StartsWith("native-", sentence.NativeText);
            Assert.Null(sentence.PronunciationNotes);
        });
        Assert.Equal("가방이 어디 있어요?", state.Sentences[2].TargetText);
        Assert.Equal("Rising intonation at the end.", state.Sentences[2].PronunciationNotes);
        Assert.Equal(NativeSliceFixture.UserA, ai.LastOwner);
        Assert.Contains("I am a English speaker learning Korean", ai.LastPrompt);
        Assert.Contains("target-native-owner-a-0", ai.LastPrompt);
        Assert.DoesNotContain("target-native-owner-a-1", ai.LastPrompt);
        // First sentence audio is prepared without autoplay.
        Assert.Equal(0, state.Index);
        Assert.True(state.HasAudio);
        Assert.False(state.IsPlaying);
        Assert.False(state.IsBuffering);
        Assert.Equal([(NativeSliceFixture.UserA, state.Sentences[0].TargetText, "Korean")], speech.Requests);
        Assert.Empty(player.Rates);
    }

    [Fact]
    public async Task TranscriptMode_ExtractsSentencesWithoutTranslationOrAi()
    {
        using var fixture = new NativeSliceFixture();
        await fixture.SeedAsync(NativeSliceFixture.UserA, wordCount: 3);
        using (var scope = fixture.Scope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var resource = await db.LearningResources.SingleAsync(row =>
                row.Id == fixture.ResourceId(NativeSliceFixture.UserA));
            resource.Transcript = "저는 매일 아침 학교에 가요. 오늘은 하늘이 정말 맑고 파랗습니다. 친구와 같이 점심을 먹었어요.";
            await db.SaveChangesAsync();
        }
        await SignInAsync(fixture);
        var ai = new ScratchShadowingAi();
        var (presenter, _, _) = Create(fixture, ai);
        using var _ = presenter;

        var state = await presenter.OpenAsync(ChooseOwnLaunch(fixture));

        Assert.Equal(NativePhase.Ready, state.Phase);
        Assert.Equal(3, state.Count);
        Assert.All(state.Sentences, sentence =>
        {
            Assert.False(sentence.HasTranslation);
            Assert.Null(sentence.PronunciationNotes);
        });
        Assert.Equal(0, ai.Calls);
        // No translation: the toggle cannot reveal anything.
        state = await presenter.ToggleTranslationAsync();
        Assert.False(state.ShowTranslation);
    }

    [Fact]
    public async Task Controls_FollowTheSourceTranslationSpeedPlaybackAndNavigationRules()
    {
        using var fixture = new NativeSliceFixture();
        await fixture.SeedAsync(NativeSliceFixture.UserA, wordCount: 3);
        await SignInAsync(fixture);
        var (presenter, speech, player) = Create(fixture);
        using var _ = presenter;
        var state = await presenter.OpenAsync(ChooseOwnLaunch(fixture));
        Assert.False(state.CanGoPrevious);
        Assert.True(state.CanGoNext);
        Assert.False(state.ShowTranslation);
        Assert.Equal(NativeShadowingSpeed.Normal, state.Speed);

        state = await presenter.ToggleTranslationAsync();
        Assert.True(state.ShowTranslation);

        state = await presenter.TogglePlaybackAsync();
        Assert.True(state.IsPlaying);
        Assert.Equal([NativeShadowingSpeed.Normal], player.Rates);
        state = await presenter.TogglePlaybackAsync();
        Assert.False(state.IsPlaying);

        // Speed change reloads the sentence audio and autoplays at the new rate.
        state = await presenter.SetSpeedAsync(NativeShadowingSpeed.Slow);
        Assert.Equal(NativeShadowingSpeed.Slow, state.Speed);
        Assert.True(state.IsPlaying);
        Assert.Equal(NativeShadowingSpeed.Slow, player.Rates[^1]);
        Assert.Equal(2, speech.Requests.Count);

        state = await presenter.SeekAsync(0.5);
        Assert.Equal(0.5, state.Position, 3);
        Assert.Equal(0.5, player.LastSeek, 3);

        // Playback reaching the end stops; Play then replays from the start.
        player.Finish();
        state = await WaitAsync(presenter, s => !s.IsPlaying);
        Assert.Equal(1, state.Position);
        state = await presenter.TogglePlaybackAsync();
        Assert.True(state.IsPlaying);
        Assert.Equal(0, player.LastSeek);

        // Next stops audio, hides the translation, and prepares without autoplay; speed is kept.
        state = await presenter.NextAsync();
        Assert.Equal(1, state.Index);
        Assert.False(state.ShowTranslation);
        Assert.False(state.IsPlaying);
        Assert.True(state.HasAudio);
        Assert.Equal(0, state.Position);
        Assert.Equal(NativeShadowingSpeed.Slow, state.Speed);
        Assert.Equal(state.Sentences[1].TargetText, speech.Requests[^1].Text);
        state = await presenter.NextAsync();
        Assert.Equal(2, state.Index);
        Assert.False(state.CanGoNext);
        state = await presenter.NextAsync();
        Assert.Equal(2, state.Index);
        state = await presenter.PreviousAsync();
        Assert.Equal(1, state.Index);
        Assert.True(state.CanGoPrevious);

        await presenter.ExitAsync();
        // Every command is refused after Back.
        var after = await presenter.TogglePlaybackAsync();
        Assert.Equal(NativeDestination.TodaysPlan, after.Destination);
        Assert.False(player.IsPlaying);
    }

    [Fact]
    public async Task UnavailableAudio_ShowsOfflineOrFailedAndPlayRetries()
    {
        using var fixture = new NativeSliceFixture();
        await fixture.SeedAsync(NativeSliceFixture.UserA, wordCount: 2);
        await SignInAsync(fixture);
        var (presenter, speech, player) = Create(fixture);
        using var _ = presenter;
        speech.Status = NativeSpeechClipStatus.Offline;

        var state = await presenter.OpenAsync(ChooseOwnLaunch(fixture));
        Assert.Equal(NativePhase.Ready, state.Phase);
        Assert.Equal(NativeShadowingAudioIssue.Offline, state.AudioIssue);
        Assert.False(state.HasAudio);
        // The target sentence stays visible without audio.
        Assert.NotNull(state.Current);

        speech.Status = NativeSpeechClipStatus.Failed;
        state = await presenter.TogglePlaybackAsync();
        Assert.Equal(NativeShadowingAudioIssue.Failed, state.AudioIssue);
        Assert.False(state.IsPlaying);

        speech.Status = NativeSpeechClipStatus.Ready;
        state = await presenter.TogglePlaybackAsync();
        Assert.Equal(NativeShadowingAudioIssue.None, state.AudioIssue);
        Assert.True(state.IsPlaying);
        Assert.Equal(3, speech.Requests.Count);

        player.Decodes = false;
        state = await presenter.NextAsync();
        Assert.Equal(NativeShadowingAudioIssue.Failed, state.AudioIssue);
    }

    [Fact]
    public async Task EmptyAndFailedContent_OfferOnlyGoBack()
    {
        using var fixture = new NativeSliceFixture();
        await fixture.SeedAsync(NativeSliceFixture.UserA, wordCount: 0);
        await SignInAsync(fixture);
        var (empty, _, _) = Create(fixture);
        using (empty)
        {
            var state = await empty.OpenAsync(ChooseOwnLaunch(fixture));
            Assert.Equal(NativePhase.Empty, state.Phase);
            Assert.Equal(NativeShadowingText.NoSentences, state.ErrorKey);
            Assert.Equal(NativeDestination.TodaysPlan, (await empty.ExitAsync()).Destination);
        }

        // No resource selected: source returns silently to "No sentences".
        var (noResource, _, _) = Create(fixture);
        using (noResource)
        {
            var launch = ChooseOwnLaunch(fixture) with { ResourceIds = [] };
            Assert.Equal(NativePhase.Empty, (await noResource.OpenAsync(launch)).Phase);
        }

        using var second = new NativeSliceFixture();
        await second.SeedAsync(NativeSliceFixture.UserA, wordCount: 1);
        await SetLexicalTypeAsync(second, second.WordId(NativeSliceFixture.UserA, 0), LexicalUnitType.Word);
        await SignInAsync(second);
        var (failed, _, _) = Create(second, new ScratchShadowingAi { Throws = true });
        using (failed)
        {
            var state = await failed.OpenAsync(ChooseOwnLaunch(second));
            Assert.Equal(NativePhase.Error, state.Phase);
            Assert.Equal(NativeShadowingText.LoadFailed, state.ErrorKey);
        }
    }

    [Fact]
    public async Task AbsentChangedOrForeignOwner_FailsClosedWithoutAudioOrForeignRows()
    {
        using var fixture = new NativeSliceFixture();
        await fixture.SeedAsync(NativeSliceFixture.UserA, wordCount: 2);
        await fixture.SeedAsync(NativeSliceFixture.UserB, wordCount: 2);
        await SignInAsync(fixture);

        var (foreign, foreignSpeech, _) = Create(fixture);
        using (foreign)
        {
            var refused = await foreign.OpenAsync(ChooseOwnLaunch(fixture, NativeSliceFixture.UserB));
            Assert.Equal(NativePhase.Blocked, refused.Phase);
            Assert.Empty(foreignSpeech.Requests);
        }

        // A launch for the owner that names a foreign resource finds nothing.
        var (stale, staleSpeech, _) = Create(fixture);
        using (stale)
        {
            var state = await stale.OpenAsync(ChooseOwnLaunch(fixture) with {
                ResourceIds = [fixture.ResourceId(NativeSliceFixture.UserB)] });
            Assert.Equal(NativePhase.Empty, state.Phase);
            Assert.Empty(staleSpeech.Requests);
            await stale.ExitAsync();
        }

        var (presenter, speech, player) = Create(fixture);
        using var _ = presenter;
        var open = await presenter.OpenAsync(ChooseOwnLaunch(fixture));
        Assert.Equal(NativePhase.Ready, open.Phase);
        await presenter.TogglePlaybackAsync();
        Assert.True(player.IsPlaying);
        fixture.LoseOwner();

        var blocked = await presenter.NextAsync();
        Assert.Equal(NativePhase.Blocked, blocked.Phase);
        Assert.False(player.IsPlaying);
        Assert.Single(speech.Requests);

        using var scope = fixture.Scope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        Assert.False(await db.DailyPlanCompletions.AnyAsync(row =>
            row.UserProfileId == NativeSliceFixture.UserB));
    }

    [Fact]
    public async Task Speech_UsesTheOwnersCacheAndReportsOfflineWhenUncached()
    {
        using var fixture = new NativeSliceFixture();
        await fixture.SeedAsync(NativeSliceFixture.UserA, wordCount: 1);
        await SignInAsync(fixture);
        var provider = fixture.Provider;
        var history = new StreamHistoryRepository(provider,
            NullLogger<StreamHistoryRepository>.Instance);
        var speech = new NativeShadowingSpeech(
            provider.GetRequiredService<NativeActivityOwner>(),
            provider.GetRequiredService<SentenceStudio.Abstractions.IConnectivityService>(),
            provider.GetRequiredService<SentenceStudio.Abstractions.IFileSystemService>(),
            provider.GetRequiredService<SentenceStudio.Services.SpeechVoicePreferences>(),
            history, NullLogger<NativeShadowingSpeech>.Instance);

        fixture.SetConnectivity(false);
        var offline = await speech.GetAsync(NativeSliceFixture.UserA, "안녕하세요 여러분", "Korean", default);
        Assert.Equal(NativeSpeechClipStatus.Offline, offline.Status);

        var directory = Path.Combine(fixture.Root, "AudioCache");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "cached.mp3");
        await File.WriteAllBytesAsync(path, [1, 2, 3]);
        var voice = provider.GetRequiredService<SentenceStudio.Services.SpeechVoicePreferences>()
            .GetVoiceForLanguage("Korean");
        await history.SaveStreamHistoryAsync(new StreamHistory
        {
            UserProfileId = NativeSliceFixture.UserA,
            Phrase = "안녕하세요 여러분",
            VoiceId = voice,
            // Stored under an older sandbox path: resolved by file name in today's cache.
            AudioFilePath = Path.Combine("/old-sandbox/Documents/AudioCache", "cached.mp3"),
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        });
        var cached = await speech.GetAsync(NativeSliceFixture.UserA, "안녕하세요 여러분", "Korean", default);
        Assert.Equal(NativeSpeechClipStatus.Ready, cached.Status);
        Assert.Equal([1, 2, 3], cached.Audio);

        fixture.LoseOwner();
        Assert.Equal(NativeSpeechClipStatus.Failed,
            (await speech.GetAsync(NativeSliceFixture.UserA, "안녕하세요 여러분", "Korean", default)).Status);
        // Task-owned scratch: the fixture removes only its own database files.
        Directory.Delete(directory, recursive: true);
    }

    private static (NativeShadowingPresenter Presenter, ScratchShadowingSpeech Speech, ScratchShadowingPlayer Player)
        Create(NativeSliceFixture fixture, ScratchShadowingAi? ai = null)
    {
        var provider = fixture.Provider;
        var owner = provider.GetRequiredService<NativeActivityOwner>();
        var content = new NativeShadowingContent(owner,
            provider.GetRequiredService<LearningResourceRepository>(),
            provider.GetRequiredService<UserProfileRepository>(),
            provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<NativeShadowingContent>.Instance, ai ?? new ScratchShadowingAi());
        var speech = new ScratchShadowingSpeech();
        var player = new ScratchShadowingPlayer();
        var presenter = new NativeShadowingPresenter(owner,
            provider.GetRequiredService<NativeActivityTimer>(), content, speech, player,
            provider.GetRequiredService<NativePendingWrites>(),
            NullLogger<NativeShadowingPresenter>.Instance)
        {
            TickInterval = TimeSpan.FromMilliseconds(10)
        };
        return (presenter, speech, player);
    }

    private static async Task<NativeShadowingState> WaitAsync(
        NativeShadowingPresenter presenter, Func<NativeShadowingState, bool> condition)
    {
        for (var i = 0; i < 200 && !condition(presenter.State); i++)
            await Task.Delay(10);
        Assert.True(condition(presenter.State));
        return presenter.State;
    }

    private static NativeActivityLaunch ChooseOwnLaunch(
        NativeSliceFixture fixture, string owner = NativeSliceFixture.UserA) =>
        new(Route, owner, new DateOnly(2026, 9, 28), null,
            [fixture.ResourceId(owner)], null, [], false, fixture.ResourceId(owner),
            PlanActivityType.Shadowing);

    private static async Task SetLexicalTypeAsync(NativeSliceFixture fixture, string wordId, LexicalUnitType type)
    {
        using var scope = fixture.Scope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var word = await db.VocabularyWords.SingleAsync(row => row.Id == wordId);
        word.LexicalUnitType = type;
        await db.SaveChangesAsync();
    }

    private static async Task SignInAsync(NativeSliceFixture fixture) =>
        Assert.Equal(NativePhase.Ready,
            (await fixture.Auth.SignInAsync(NativeSliceFixture.EmailA, "fixture-password")).Phase);
}

internal sealed class ScratchShadowingAi : IOwnerBoundNativeAiGateway
{
    public ShadowingSentencesResponse? Response { get; set; } = new();
    public bool Throws { get; set; }
    public int Calls { get; private set; }
    public string? LastOwner { get; private set; }
    public string? LastPrompt { get; private set; }

    public Task<T?> SendPromptAsync<T>(string owner, string prompt, CancellationToken cancellationToken)
    {
        Calls++;
        LastOwner = owner;
        LastPrompt = prompt;
        if (Throws)
            throw new HttpRequestException("Scratch AI unavailable.");
        return Task.FromResult((T?)(object?)Response);
    }
}

internal sealed class ScratchShadowingSpeech : INativeShadowingSpeech
{
    public NativeSpeechClipStatus Status { get; set; } = NativeSpeechClipStatus.Ready;
    public List<(string Owner, string Text, string Language)> Requests { get; } = [];

    public Task<NativeSpeechClip> GetAsync(
        string owner, string text, string language, CancellationToken cancellationToken)
    {
        Requests.Add((owner, text, language));
        return Task.FromResult(new NativeSpeechClip(Status,
            Status == NativeSpeechClipStatus.Ready ? [1, 2, 3] : null));
    }
}

internal sealed class ScratchShadowingPlayer : INativeShadowingPlayer
{
    private double _position;
    public bool Decodes { get; set; } = true;
    public int Loaded { get; private set; }
    public List<float> Rates { get; } = [];
    public double LastSeek { get; private set; } = -1;
    public bool IsLoaded { get; private set; }
    public bool IsPlaying { get; private set; }
    public double DurationSeconds => IsLoaded ? 4 : 0;
    public double PositionSeconds => _position;
    public event Action? Ended;

    public bool Load(byte[] audio)
    {
        IsPlaying = false;
        IsLoaded = Decodes && audio.Length > 0;
        _position = 0;
        if (IsLoaded) Loaded++;
        return IsLoaded;
    }

    public bool Play(float rate)
    {
        if (!IsLoaded) return false;
        Rates.Add(rate);
        IsPlaying = true;
        return true;
    }

    public void Pause() => IsPlaying = false;

    public void Seek(double fraction)
    {
        LastSeek = fraction;
        _position = fraction * DurationSeconds;
    }

    public void Stop()
    {
        IsPlaying = false;
        IsLoaded = false;
        _position = 0;
    }

    public void Finish()
    {
        _position = DurationSeconds;
        IsPlaying = false;
        Ended?.Invoke();
    }

    public void Dispose() => Stop();
}
