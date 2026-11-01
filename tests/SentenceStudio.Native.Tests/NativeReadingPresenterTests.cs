using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SentenceStudio.Abstractions;
using SentenceStudio.Data;
using SentenceStudio.Models;
using SentenceStudio.Native.Adapters;
using SentenceStudio.Services.Progress;
using SentenceStudio.Shared.Models;

namespace SentenceStudio.Native.Tests;

/// <summary>
/// Reading source contract (activities.md 1.1-1.3 and 10) on the shared
/// activity foundation, against real SQLite and shared repositories.
/// </summary>
public sealed class NativeReadingPresenterTests
{
    private const string Reading = NativeActivityCatalog.Reading;
    private const string Transcript = "오늘은 날씨가 좋아요. 저는 공원에 가요! 친구를 만나요.";

    [Fact]
    public void Catalog_RegistersReadingForReadingPlanCardsAndItsTile()
    {
        var definition = NativeActivityCatalog.ForRoute(Reading);
        Assert.NotNull(definition);
        Assert.Equal(PlanActivityType.Reading, definition.TimerType);
        Assert.True(definition.TimerRecordsSkill);
        Assert.False(NativeActivityCatalog.IsVocabularyDriven(PlanActivityType.Reading));
        Assert.False(NativeActivityCatalog.TakesResourceList(Reading));
        Assert.Contains(NativeDashboardPresenter.ChooseOwnActivities, tile => tile.Route == Reading);
    }

    [Fact]
    public void Tokenize_MatchesInteractiveText()
    {
        Assert.Equal(["오늘은", " ", "날씨가", " ", "좋아요", "."],
            NativeReadingPresenter.Tokenize("오늘은 날씨가 좋아요."));
        Assert.Equal(["\"", "It's", " ", "ok", "!\""],
            NativeReadingPresenter.Tokenize("\"It's ok!\""));
        Assert.Equal("1:05", NativeReadingPresenter.FormatTime(65.4));
        Assert.Equal("0:00", NativeReadingPresenter.FormatTime(double.NaN));
    }

    [Theory]
    [InlineData("Korean", "ko-KR")]
    [InlineData(" Korean ", "ko-KR")]
    [InlineData("English", "en-US")]
    [InlineData("Japanese", "ja-JP")]
    [InlineData("Klingon", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void SpeechLanguageTag_MapsTheResourceLanguageOrKeepsTheSystemVoice(string? language, string? expected) =>
        Assert.Equal(expected, NativeReadingPresenter.SpeechLanguageTag(language));

    [Theory]
    [InlineData("Korean", "오늘은 날씨가 좋아요.", "ko-KR")]
    [InlineData("English", "오늘은 날씨가 좋아요.", "ko-KR")]
    [InlineData(null, "공원에 갔어요.", "ko-KR")]
    [InlineData("English", "The weather is nice today.", "en-US")]
    [InlineData("Spanish", "Hace buen tiempo.", "es-ES")]
    [InlineData(null, "No language.", null)]
    public void PassageLanguageTag_HangulIsKoreanOtherwiseTheResourceLanguage(
        string? language, string passage, string? expected) =>
        Assert.Equal(expected, NativeReadingPresenter.PassageLanguageTag(language, passage));

    [Fact]
    public async Task ChooseOwn_ReadsTranscriptPlaysAlongAndBackRecordsTheSession()
    {
        using var fixture = new NativeSliceFixture();
        await SeedReadingAsync(fixture, NativeSliceFixture.UserA);
        await SignInAsync(fixture, NativeSliceFixture.EmailA);
        var audio = new ScratchAudioSource();
        var player = new ScratchPlayerFactory();
        using var reading = Presenter(fixture, audio, player);

        var state = await reading.OpenAsync(ChooseOwnLaunch(fixture, NativeSliceFixture.UserA));

        Assert.Equal(NativePhase.Ready, state.Phase);
        Assert.Equal("Reading passage", state.ResourceTitle);
        Assert.Equal(3, state.SentenceCount);
        Assert.Equal(NativeReadingPresenter.DefaultFontSize, state.FontSize);
        var tokens = state.Sentences.SelectMany(sentence => sentence.Tokens).ToList();
        var vocabulary = Assert.Single(tokens, token => token.IsVocabulary);
        Assert.Equal("날씨가", vocabulary.Text);
        Assert.False(vocabulary.IsFocusVocabulary);
        Assert.Equal("— / 3", state.SentenceCounter);
        Assert.Equal("ko-KR", state.PassageLanguageTag);
        Assert.Null(state.CurrentSentenceAnchorId);

        await reading.AudioTask;
        state = reading.State;
        Assert.True(state.IsAudioLoaded);
        Assert.False(state.IsGeneratingAudio);
        Assert.Equal(NativeSliceFixture.UserA, audio.Owner);

        state = await reading.TogglePlaybackAsync();
        Assert.True(state.IsPlaying);
        Assert.Equal(0, state.CurrentSentenceIndex);
        Assert.True(player.Player!.IsPlaying);
        state = await reading.NextSentenceAsync();
        Assert.Equal(1, state.CurrentSentenceIndex);
        Assert.Equal(2.0, player.Player.Position, 1);
        state = await reading.TogglePlaybackAsync();
        Assert.False(state.IsPlaying);
        state = await reading.PreviousSentenceAsync();
        Assert.Equal(0, state.CurrentSentenceIndex);
        Assert.Equal(2.0, player.Player.Position, 1);

        state = await reading.JumpToSentenceAsync(2);
        Assert.Equal(2, state.CurrentSentenceIndex);
        Assert.Equal(state.Sentences[2].Tokens.First(token => token.IsWord).AccessibilityId,
            state.CurrentSentenceAnchorId);
        Assert.True(state.IsPlaying);
        Assert.Equal(NativeReadingText.JumpHint, state.Notice?.Key);
        Assert.True(fixture.Preferences.Get(NativeReadingPresenter.JumpHintKey, false));
        Assert.Null((await reading.JumpToSentenceAsync(1)).Notice);

        var exit = await reading.ExitAsync();
        Assert.Equal(NativeDestination.TodaysPlan, exit.Destination);
        Assert.Null(exit.ErrorKey);
        Assert.False(player.Player.IsPlaying);
        Assert.True(exit.HasPendingSync);

        using var scope = fixture.Scope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var row = Assert.Single(await db.UserActivities.AsNoTracking().ToListAsync());
        Assert.Equal(NativeSliceFixture.UserA, row.UserProfileId);
        Assert.Equal(nameof(Activity.Reading), row.Activity);
        Assert.Equal("Reading session: Reading passage", row.Input);
        var adHoc = Assert.Single(await db.DailyPlanCompletions.AsNoTracking()
            .Where(item => item.PlanItemId.StartsWith("adhoc-")).ToListAsync());
        Assert.Equal(nameof(PlanActivityType.Reading), adHoc.ActivityType);
        Assert.Equal(NativeSliceFixture.UserA, adHoc.UserProfileId);
        Assert.Equal(fixture.ResourceId(NativeSliceFixture.UserA), adHoc.ResourceId);
        Assert.Equal(NativeDestination.TodaysPlan, (await reading.TogglePlaybackAsync()).Destination);
    }

    [Fact]
    public async Task PlanCard_LaunchesWithItsResourceAndHighlightsFocusWords()
    {
        using var fixture = new NativeSliceFixture();
        var owner = NativeSliceFixture.UserA;
        await SeedReadingAsync(fixture, owner);
        fixture.SetPlanActivities(owner,
            (nameof(PlanActivityType.VocabularyReview), null, null, 6, 1),
            (nameof(PlanActivityType.Reading), fixture.ResourceId(owner), null, 15, 2));
        fixture.SetPlanFocus(owner, ReadingWordId(owner));
        await SignInAsync(fixture, NativeSliceFixture.EmailA);
        var plan = await fixture.Dashboard.GenerateAsync();
        var card = plan.Cards.Single(item => item.Activity == PlanActivityType.Reading);
        var launch = await fixture.Provider.GetRequiredService<NativeActivityLaunchService>()
            .FromPlanCardAsync(card.Id);

        Assert.NotNull(launch);
        Assert.Equal(Reading, launch.Route);
        Assert.Equal([fixture.ResourceId(owner)], launch.ResourceIds.ToArray());
        Assert.False(launch.DueOnly);
        Assert.Equal(PlanActivityType.Reading, launch.ActivityType);

        using var reading = Presenter(fixture, new ScratchAudioSource(), new ScratchPlayerFactory());
        var state = await reading.OpenAsync(launch);
        Assert.Equal(NativePhase.Ready, state.Phase);
        if (card.FocusVocabularyIds.Contains(ReadingWordId(owner)))
            Assert.True(state.Sentences.SelectMany(sentence => sentence.Tokens)
                .Single(token => token.IsVocabulary).IsFocusVocabulary);
        await reading.ExitAsync();
        var (row, _) = await fixture.RowsAsync(owner, card.Id);
        Assert.False(row.IsCompleted);
    }

    [Fact]
    public async Task WordTaps_ShowVocabularyLookUpOthersAndSaveToTheOwnersResource()
    {
        using var fixture = new NativeSliceFixture();
        var owner = NativeSliceFixture.UserA;
        await SeedReadingAsync(fixture, owner);
        await SignInAsync(fixture, NativeSliceFixture.EmailA);
        var ai = new ScratchTranslator("park");
        using var reading = Presenter(fixture, new ScratchAudioSource(), new ScratchPlayerFactory(), ai);
        var state = await reading.OpenAsync(ChooseOwnLaunch(fixture, owner));
        await reading.AudioTask;
        var tokens = state.Sentences.SelectMany(sentence => sentence.Tokens).ToList();

        state = await reading.TapWordAsync(tokens.Single(token => token.IsVocabulary).Id);
        Assert.Equal(new NativeReadingWordPanel("날씨가", "weather", false, true, false), state.WordPanel);
        Assert.False(state.WordPanel!.CanSave);
        Assert.Equal(0, ai.Calls);

        // A non-word token is not tappable.
        Assert.Null((await reading.CloseWordPanelAsync()).WordPanel);
        Assert.Null((await reading.TapWordAsync(tokens.First(token => !token.IsWord).Id)).WordPanel);

        // Context is the current sentence once one is selected.
        await reading.NextSentenceAsync();
        await reading.NextSentenceAsync();
        state = await reading.TapWordAsync(tokens.Single(token => token.Text == "공원에").Id);
        Assert.Equal("공원에", state.WordPanel!.Word);
        Assert.Equal("park", state.WordPanel.Definition);
        Assert.True(state.WordPanel.CanSave);
        Assert.Equal(1, ai.Calls);
        Assert.Contains("공원에", ai.LastPrompt);
        Assert.Contains("저는 공원에 가요!", ai.LastPrompt);
        Assert.Equal(owner, ai.Owner);

        state = await reading.SaveWordAsync();
        Assert.True(state.WordPanel!.IsInVocabulary);
        Assert.False(state.WordPanel.CanSave);
        Assert.Equal(NativeReadingText.WordSaved, state.Notice?.Key);
        Assert.Equal("공원에", state.Notice?.Argument);
        Assert.True(state.Sentences.SelectMany(sentence => sentence.Tokens)
            .Single(token => token.Text == "공원에").IsVocabulary);

        // A failed lookup offers no save.
        ai.Reply = "";
        state = await reading.TapWordAsync(tokens.Single(token => token.Text == "친구를").Id);
        Assert.Equal(NativeReadingPresenter.TranslationUnavailableMarker, state.WordPanel!.Definition);
        Assert.False(state.WordPanel.CanSave);

        using var scope = fixture.Scope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var inputs = await db.UserActivities.AsNoTracking()
            .Where(row => row.UserProfileId == owner).Select(row => row.Input).ToListAsync();
        Assert.Equal(3, inputs.Count);
        Assert.Contains("Dictionary lookup: 공원에", inputs);
        Assert.Contains("Added vocabulary: 공원에", inputs);
        Assert.Contains("Dictionary lookup: 친구를", inputs);
        var saved = await db.VocabularyWords.AsNoTracking()
            .SingleAsync(word => word.TargetLanguageTerm == "공원에");
        Assert.Equal(owner, saved.UserProfileId);
        Assert.Equal("park", saved.NativeLanguageTerm);
        Assert.Equal("Korean", saved.Language);
        Assert.True(await db.ResourceVocabularyMappings.AnyAsync(mapping =>
            mapping.ResourceId == fixture.ResourceId(owner) && mapping.VocabularyWordId == saved.Id));
        // The source's inverted, unowned translation cache word is not written.
        Assert.False(await db.VocabularyWords.AnyAsync(word => word.NativeLanguageTerm == "친구를"));
    }

    [Fact]
    public async Task ErrorAndEmptyStates_OfferGoBack()
    {
        using var fixture = new NativeSliceFixture();
        var owner = NativeSliceFixture.UserA;
        await fixture.SeedAsync(owner, wordCount: 1);
        await fixture.SeedAsync(NativeSliceFixture.UserB, wordCount: 1);
        await SignInAsync(fixture, NativeSliceFixture.EmailA);

        using (var noTranscript = Presenter(fixture, new ScratchAudioSource(), new ScratchPlayerFactory()))
        {
            var state = await noTranscript.OpenAsync(ChooseOwnLaunch(fixture, owner));
            Assert.Equal(NativePhase.Error, state.Phase);
            Assert.Equal(NativeReadingText.NoTranscript, state.ErrorKey);
            await noTranscript.ExitAsync();
        }
        using (var noResource = Presenter(fixture, new ScratchAudioSource(), new ScratchPlayerFactory()))
        {
            var state = await noResource.OpenAsync(ChooseOwnLaunch(fixture, owner) with { ResourceIds = [] });
            Assert.Equal(NativePhase.Empty, state.Phase);
            Assert.Equal(NativeReadingText.NoContent, state.ErrorKey);
            await noResource.ExitAsync();
        }
        using (var foreign = Presenter(fixture, new ScratchAudioSource(), new ScratchPlayerFactory()))
        {
            var state = await foreign.OpenAsync(ChooseOwnLaunch(fixture, owner) with {
                ResourceIds = [fixture.ResourceId(NativeSliceFixture.UserB)] });
            Assert.Equal(NativePhase.Error, state.Phase);
            Assert.Equal(NativeReadingText.ResourceNotFound, state.ErrorKey);
            await foreign.ExitAsync();
        }

        using var scope = fixture.Scope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        // Only the resource that loaded (without a transcript) records a session, as in the source.
        var row = Assert.Single(await db.UserActivities.AsNoTracking().ToListAsync());
        Assert.Equal(owner, row.UserProfileId);
        Assert.False(await db.UserActivities.AnyAsync(item => item.UserProfileId == NativeSliceFixture.UserB));
    }

    [Fact]
    public async Task MissingAudio_ReadsWithoutAudioAndPlayExplains()
    {
        using var fixture = new NativeSliceFixture();
        await SeedReadingAsync(fixture, NativeSliceFixture.UserA);
        await SignInAsync(fixture, NativeSliceFixture.EmailA);
        using var reading = Presenter(fixture, new ScratchAudioSource { Fail = true }, new ScratchPlayerFactory());

        var state = await reading.OpenAsync(ChooseOwnLaunch(fixture, NativeSliceFixture.UserA));
        await reading.AudioTask;
        state = reading.State;

        Assert.Equal(NativePhase.Ready, state.Phase);
        Assert.False(state.IsAudioLoaded);
        Assert.Equal(NativeReadingText.AudioFailed, state.AudioStatusKey);
        Assert.False(state.CanNavigate);
        Assert.Equal(NativeReadingText.NoAudio, (await reading.TogglePlaybackAsync()).Notice?.Key);
        Assert.Equal(-1, (await reading.NextSentenceAsync()).CurrentSentenceIndex);
    }

    [Fact]
    public async Task FontSizeAndSpeed_ClampAndPersistLikeTheSource()
    {
        using var fixture = new NativeSliceFixture();
        await SeedReadingAsync(fixture, NativeSliceFixture.UserA);
        await SignInAsync(fixture, NativeSliceFixture.EmailA);
        fixture.Preferences.Set(NativeReadingPresenter.FontSizeKey, 24.0);
        using var reading = Presenter(fixture, new ScratchAudioSource(), new ScratchPlayerFactory());

        var state = await reading.OpenAsync(ChooseOwnLaunch(fixture, NativeSliceFixture.UserA));
        Assert.Equal(24, state.FontSize);
        Assert.Equal(40, (await reading.SetFontSizeAsync(99)).FontSize);
        Assert.Equal(14, (await reading.SetFontSizeAsync(3)).FontSize);
        Assert.Equal(16, (await reading.SetFontSizeAsync(17)).FontSize);
        Assert.Equal(16.0, fixture.Preferences.Get(NativeReadingPresenter.FontSizeKey, 0.0));
        Assert.Equal(1.5f, (await reading.SetPlaybackSpeedAsync(1.5f)).PlaybackSpeed);
        Assert.Equal(1.5f, (await reading.SetPlaybackSpeedAsync(3f)).PlaybackSpeed);
        Assert.Equal(1.5f, fixture.Preferences.Get(NativeReadingPresenter.PlaybackSpeedKey, 0f));
    }

    [Fact]
    public async Task AbsentOrChangedOwner_FailsClosedWithoutWrites()
    {
        using var fixture = new NativeSliceFixture();
        await SeedReadingAsync(fixture, NativeSliceFixture.UserA);
        await fixture.SeedAsync(NativeSliceFixture.UserB, wordCount: 1);
        await SignInAsync(fixture, NativeSliceFixture.EmailA);
        var ai = new ScratchTranslator("park");

        using (var refused = Presenter(fixture, new ScratchAudioSource(), new ScratchPlayerFactory(), ai))
        {
            var state = await refused.OpenAsync(ChooseOwnLaunch(fixture, NativeSliceFixture.UserB));
            Assert.Equal(NativePhase.Blocked, state.Phase);
        }

        using var reading = Presenter(fixture, new ScratchAudioSource(), new ScratchPlayerFactory(), ai);
        var open = await reading.OpenAsync(ChooseOwnLaunch(fixture, NativeSliceFixture.UserA));
        var word = open.Sentences.SelectMany(sentence => sentence.Tokens).Single(token => token.Text == "공원에");
        fixture.LoseOwner();

        Assert.Equal(NativePhase.Blocked, (await reading.TapWordAsync(word.Id)).Phase);
        Assert.Equal(NativePhase.Blocked, reading.State.Phase);
        Assert.Equal(0, ai.Calls);
        Assert.Equal(NativeDestination.TodaysPlan, (await reading.ExitAsync()).Destination);

        using var scope = fixture.Scope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        Assert.False(await db.UserActivities.AnyAsync());
        Assert.False(await db.DailyPlanCompletions.AnyAsync(row =>
            row.UserProfileId == NativeSliceFixture.UserB));
    }

    [Fact]
    public void AudioSource_DecodesTheApiDataUriAndCharacters()
    {
        Assert.True(NativeReadingAudioSource.TryDecode(
            "data:audio/mpeg;base64," + Convert.ToBase64String([1, 2, 3]), out var bytes));
        Assert.Equal([1, 2, 3], bytes);
        Assert.False(NativeReadingAudioSource.TryDecode("https://example.invalid/a.mp3", out _));
        var characters = NativeReadingAudioSource.ToCharacters([("오", 0.1, 0.2)]);
        Assert.Equal("오", Assert.Single(characters).Character);
        Assert.Equal(0.2, characters[0].EndTime);
    }

    private static string ReadingWordId(string owner) => $"reading-word-{owner}";

    private static async Task SeedReadingAsync(NativeSliceFixture fixture, string owner)
    {
        await fixture.SeedAsync(owner, wordCount: 0);
        using var scope = fixture.Scope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var resource = await db.LearningResources.SingleAsync(item => item.Id == fixture.ResourceId(owner));
        resource.Title = "Reading passage";
        resource.MediaType = "Text";
        resource.Transcript = Transcript;
        db.VocabularyWords.Add(new VocabularyWord
        {
            Id = ReadingWordId(owner),
            TargetLanguageTerm = "날씨가",
            NativeLanguageTerm = "weather",
            Language = "Korean",
            UserProfileId = owner
        });
        db.ResourceVocabularyMappings.Add(new ResourceVocabularyMapping
        {
            Id = $"reading-mapping-{owner}",
            ResourceId = resource.Id,
            VocabularyWordId = ReadingWordId(owner)
        });
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task SupersededOrExitedLookup_IsNeitherShownNorRecorded()
    {
        using var fixture = new NativeSliceFixture();
        var owner = NativeSliceFixture.UserA;
        await SeedReadingAsync(fixture, owner);
        await SignInAsync(fixture, NativeSliceFixture.EmailA);
        var ai = new ScratchTranslator("park");
        using var reading = Presenter(fixture, new ScratchAudioSource(), new ScratchPlayerFactory(), ai);
        var state = await reading.OpenAsync(ChooseOwnLaunch(fixture, owner));
        await reading.AudioTask;
        var tokens = state.Sentences.SelectMany(sentence => sentence.Tokens).ToList();
        var first = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var second = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        ai.Gates.Enqueue(first);
        ai.Gates.Enqueue(second);

        // A later tap supersedes the first lookup.
        var superseded = reading.TapWordAsync(tokens.Single(token => token.Text == "공원에").Id);
        var current = reading.TapWordAsync(tokens.Single(token => token.Text == "친구를").Id);
        first.SetResult(true);
        await superseded;
        second.SetResult(true);
        state = await current;
        Assert.Equal("친구를", state.WordPanel!.Word);
        Assert.Equal("park", state.WordPanel.Definition);

        // Exit while a lookup is in flight: nothing is recorded for it.
        var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        ai.Gates.Enqueue(gate);
        var exited = reading.TapWordAsync(tokens.Single(token => token.Text == "오늘은").Id);
        Assert.Equal(NativeDestination.TodaysPlan, (await reading.ExitAsync()).Destination);
        gate.SetResult(true);
        await exited;

        using var scope = fixture.Scope();
        var inputs = await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>()
            .UserActivities.AsNoTracking()
            .Where(row => row.UserProfileId == owner && row.Input!.StartsWith("Dictionary lookup"))
            .Select(row => row.Input).ToListAsync();
        Assert.Equal(["Dictionary lookup: 친구를"], inputs);
    }

    private static NativeReadingPresenter Presenter(NativeSliceFixture fixture,
        INativeReadingAudioSource audio, IAudioPlayerFactory players,
        IOwnerBoundNativeAiGateway? ai = null) =>
        ActivatorUtilities.CreateInstance<NativeReadingPresenter>(fixture.Provider,
            audio, players, ai ?? new ScratchTranslator(""));

    private static NativeActivityLaunch ChooseOwnLaunch(NativeSliceFixture fixture, string owner) =>
        new(Reading, owner, new DateOnly(2026, 9, 28), null,
            [fixture.ResourceId(owner)], null, [], false, fixture.ResourceId(owner),
            PlanActivityType.Reading);

    private static async Task SignInAsync(NativeSliceFixture fixture, string email) =>
        Assert.Equal(NativePhase.Ready,
            (await fixture.Auth.SignInAsync(email, "fixture-password")).Phase);

    private sealed class ScratchTranslator(string reply) : IOwnerBoundNativeAiGateway
    {
        public string Reply { get; set; } = reply;
        public int Calls { get; private set; }
        public string LastPrompt { get; private set; } = string.Empty;
        public string? Owner { get; private set; }

        /// <summary>When set, lookups wait for it (one gate per lookup, in order).</summary>
        public Queue<TaskCompletionSource<bool>> Gates { get; } = new();

        public async Task<T?> SendPromptAsync<T>(string owner, string prompt, CancellationToken cancellationToken)
        {
            Calls++;
            Owner = owner;
            LastPrompt = prompt;
            if (Gates.TryDequeue(out var gate))
                await gate.Task;
            return (T?)(object)Reply;
        }
    }

    /// <summary>Three sentences at 0-1.9 s, 2-3.9 s, 4-6 s.</summary>
    private sealed class ScratchAudioSource : INativeReadingAudioSource
    {
        public bool Fail { get; init; }
        public string? Owner { get; private set; }

        public Task<TimestampedAudio?> GetAsync(string owner, LearningResource resource, CancellationToken cancellationToken)
        {
            Owner = owner;
            if (Fail)
                return Task.FromResult<TimestampedAudio?>(null);
            var transcript = resource.Transcript!;
            var boundaries = new[] { transcript.IndexOf("저는", StringComparison.Ordinal),
                transcript.IndexOf("친구를", StringComparison.Ordinal) };
            var items = transcript.Select((character, index) =>
            {
                var sentence = index >= boundaries[1] ? 2 : index >= boundaries[0] ? 1 : 0;
                var start = sentence * 2.0 + 0.001 * index;
                return (character.ToString(), start, start + 0.001);
            });
            return Task.FromResult<TimestampedAudio?>(new TimestampedAudio
            {
                AudioData = [1, 2, 3],
                Characters = NativeReadingAudioSource.ToCharacters(items),
                FullTranscript = transcript,
                Duration = 6
            });
        }
    }

    private sealed class ScratchPlayerFactory : IAudioPlayerFactory
    {
        public ScratchPlayer? Player { get; private set; }
        public bool CanPlayLocally => true;

        public IUiAudioPlayer? CreatePlayer(Stream audioStream) => Player = new ScratchPlayer();
    }

    private sealed class ScratchPlayer : IUiAudioPlayer
    {
        public event EventHandler? PlaybackEnded;
        public bool IsPlaying { get; private set; }
        public double Position { get; private set; }
        public double CurrentPosition => Position;
        public double Duration => 6;
        public void Play() => IsPlaying = true;
        public void Pause() => IsPlaying = false;
        public void Stop() { IsPlaying = false; Position = 0; }
        public void Seek(double seconds) => Position = seconds;
        public void End() => PlaybackEnded?.Invoke(this, EventArgs.Empty);
        public void Dispose() => IsPlaying = false;
    }
}
