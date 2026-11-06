using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SentenceStudio.Data;
using SentenceStudio.Shared.Models;
using SentenceStudio.UI.Tests.Activities;
using SentenceStudio.WebUI.Services;

namespace SentenceStudio.UI.Tests.Audio;

/// <summary>
/// The Razor activities' owner-scoped clip cache (docs/native-port-backports.md
/// B31, B35, B38) on real SQLite and a real AudioCache directory.
/// </summary>
public sealed class UiSpeechClipCacheTests
{
    private static readonly byte[] Clip = [0x49, 0x44, 0x33, 0x04, 0x01];

    [Fact]
    public async Task SamePhraseAndVoice_IsSynthesizedOnce_AndStoredOnce()
    {
        await using var harness = await ActivityPageHarness.CreateAsync();
        var synth = new CountingSynthesizer(Clip);

        var first = await Cache(harness).GetOrCreateAsync("안녕하세요", "voice-1", true, "phrase", synth.Run, default);
        var second = await Cache(harness).GetOrCreateAsync("안녕하세요", "voice-1", true, "phrase", synth.Run, default);

        first.Should().Match<UiSpeechClip>(c => c.Status == UiSpeechClipStatus.Ready && c.Synthesized);
        second.Status.Should().Be(UiSpeechClipStatus.Ready);
        second.Synthesized.Should().BeFalse("the cached clip plays without a new synthesis");
        second.Audio.Should().Equal(Clip);
        second.Entry!.Id.Should().Be(first.Entry!.Id);
        synth.Calls.Should().Equal("voice-1");
        var rows = await Rows(harness);
        rows.Should().ContainSingle().Which.UserProfileId.Should().Be(ActivityPageHarness.OwnerA);
        Path.GetDirectoryName(rows[0].AudioFilePath).Should().Be(harness.AudioCacheDirectory);
    }

    [Fact]
    public async Task StoredPathFromAnEarlierAppContainer_IsFoundByFileName_WithoutSynthesis()
    {
        await using var harness = await ActivityPageHarness.CreateAsync();
        var fileName = $"number_{Guid.NewGuid():N}.mp3";
        await File.WriteAllBytesAsync(Path.Combine(harness.AudioCacheDirectory, fileName), Clip);
        var oldContainerPath =
            $"/var/mobile/Containers/Data/Application/{Guid.NewGuid()}/Library/AudioCache/{fileName}";
        var row = await AddRowAsync(harness, ActivityPageHarness.OwnerA, "세 시", "voice-1", oldContainerPath);
        var synth = new CountingSynthesizer(Clip);

        var clip = await Cache(harness).GetOrCreateAsync("세 시", "voice-1", true, "number", synth.Run, default);

        clip.Status.Should().Be(UiSpeechClipStatus.Ready);
        clip.Synthesized.Should().BeFalse();
        clip.Audio.Should().Equal(Clip);
        synth.Calls.Should().BeEmpty();
        (await Rows(harness)).Should().ContainSingle().Which.AudioFilePath.Should().Be(oldContainerPath,
            "a found clip leaves the row as it is");
        clip.Entry!.Id.Should().Be(row.Id);
    }

    [Fact]
    public async Task EntryWhoseClipIsOnAnotherDevice_IsSynthesizedInItsOwnVoice_AndTheSharedEntryIsUnchanged()
    {
        await using var harness = await ActivityPageHarness.CreateAsync();
        var createdAt = DateTime.UtcNow.AddDays(-2);
        const string phonePath = "/var/mobile/Containers/Data/Application/PHONE/Library/AudioCache/shadow_phone.mp3";
        var synced = await AddRowAsync(harness, ActivityPageHarness.OwnerA, "감사합니다", "phone-voice",
            phonePath, createdAt);
        var synth = new CountingSynthesizer(Clip);

        var clip = await Cache(harness).GetForEntryAsync(synced, "selected-voice", true, "phrase", synth.Run, default);

        clip.Status.Should().Be(UiSpeechClipStatus.Ready);
        clip.Synthesized.Should().BeTrue();
        synth.Calls.Should().Equal("phone-voice");
        // The synced row is not rewritten to this device's file name: another device would then
        // miss its own copy and synthesize again, and so on (code review 2026-11-03).
        var row = (await Rows(harness)).Should().ContainSingle("no second entry is added").Which;
        row.Id.Should().Be(synced.Id);
        row.AudioFilePath.Should().Be(phonePath);
        row.CreatedAt.Should().BeCloseTo(createdAt, TimeSpan.FromSeconds(1));
        var copy = Path.Combine(harness.AudioCacheDirectory, StreamHistory.RegeneratedClipFileName(synced.Id));
        (await File.ReadAllBytesAsync(copy)).Should().Equal(Clip);

        var replay = await Cache(harness).GetForEntryAsync(row, "selected-voice", true, "phrase", synth.Run, default);
        replay.Synthesized.Should().BeFalse("this device's copy of the entry plays");
        synth.Calls.Should().HaveCount(1);
        // Offline checks (the quiz's play button, How Do You Say's restore) find the same copy.
        var appData = Path.GetDirectoryName(harness.AudioCacheDirectory);
        UiSpeechClipCache.Resolve(appData, row.AudioFilePath).Should().BeNull("the phone's file is not here");
        UiSpeechClipCache.ResolveEntry(appData, row).Should().Be(copy);
    }

    [Fact]
    public async Task LookupByPhrase_ReusesTheOwnersEntryWhenItsClipIsMissing()
    {
        await using var harness = await ActivityPageHarness.CreateAsync();
        var synced = await AddRowAsync(harness, ActivityPageHarness.OwnerA, "다섯 시", "voice-1",
            "/elsewhere/AudioCache/missing.mp3");
        var synth = new CountingSynthesizer(Clip);

        var clip = await Cache(harness).GetOrCreateAsync("다섯 시", "voice-1", true, "number", synth.Run, default);

        clip.Synthesized.Should().BeTrue();
        clip.Entry!.Id.Should().Be(synced.Id);
        (await Rows(harness)).Should().ContainSingle().Which.Id.Should().Be(synced.Id);
    }

    [Fact]
    public async Task StoredPathOutsideTheAudioCache_IsNeverRead()
    {
        await using var harness = await ActivityPageHarness.CreateAsync();
        var outside = Path.Combine(harness.AppData.FullName, "private.mp3");
        await File.WriteAllBytesAsync(outside, [0x01, 0x02, 0x03]);
        await AddRowAsync(harness, ActivityPageHarness.OwnerA, "비밀", "voice-1", outside);
        var synth = new CountingSynthesizer(Clip);

        var clip = await Cache(harness).GetOrCreateAsync("비밀", "voice-1", true, "phrase", synth.Run, default);

        clip.Audio.Should().Equal(Clip, "the file outside AudioCache is not served");
        synth.Calls.Should().HaveCount(1);
        UiSpeechClipCache.Resolve(harness.AppData.FullName, "../private.mp3").Should().BeNull();
        UiSpeechClipCache.Resolve(harness.AppData.FullName, outside).Should().BeNull();
    }

    [Fact]
    public async Task EmptySynthesis_IsAFailure_AndStoresNothing()
    {
        await using var harness = await ActivityPageHarness.CreateAsync();
        var synth = new CountingSynthesizer([]);

        var clip = await Cache(harness).GetOrCreateAsync("안녕", "voice-1", true, "phrase", synth.Run, default);

        clip.Status.Should().Be(UiSpeechClipStatus.Failed);
        clip.Audio.Should().BeNull();
        (await Rows(harness)).Should().BeEmpty();
        Directory.EnumerateFiles(harness.AudioCacheDirectory).Should().BeEmpty();
    }

    [Fact]
    public async Task OfflineWithoutACachedClip_ReportsOffline_WithoutSynthesis()
    {
        await using var harness = await ActivityPageHarness.CreateAsync();
        var synth = new CountingSynthesizer(Clip);

        var clip = await Cache(harness).GetOrCreateAsync("안녕", "voice-1", false, "phrase", synth.Run, default);

        clip.Status.Should().Be(UiSpeechClipStatus.Offline);
        synth.Calls.Should().BeEmpty();
        (await Rows(harness)).Should().BeEmpty();
    }

    [Fact]
    public async Task AnotherOwnersClip_IsNotUsed_AndTheirEntryIsNotTouched()
    {
        await using var harness = await ActivityPageHarness.CreateAsync();
        var fileName = $"phrase_{Guid.NewGuid():N}.mp3";
        await File.WriteAllBytesAsync(Path.Combine(harness.AudioCacheDirectory, fileName), [0x07, 0x07]);
        var foreign = await AddRowAsync(harness, ActivityPageHarness.OwnerB, "안녕", "voice-1",
            Path.Combine(harness.AudioCacheDirectory, fileName));
        var synth = new CountingSynthesizer(Clip);

        var clip = await Cache(harness).GetOrCreateAsync("안녕", "voice-1", true, "phrase", synth.Run, default);

        clip.Audio.Should().Equal(Clip);
        clip.Entry!.UserProfileId.Should().Be(ActivityPageHarness.OwnerA);
        clip.Entry.Id.Should().NotBe(foreign.Id);
        var rows = await Rows(harness);
        rows.Single(r => r.Id == foreign.Id).AudioFilePath.Should().Be(foreign.AudioFilePath);
        rows.Should().HaveCount(2);
    }

    [Fact]
    public async Task NoActiveOwner_WritesNoEntry()
    {
        await using var harness = await ActivityPageHarness.CreateAsync();
        harness.Preferences.Remove(UserProfileRepository.ActiveProfileIdKey);
        var synth = new CountingSynthesizer(Clip);

        var clip = await Cache(harness).GetOrCreateAsync("안녕", "voice-1", true, "phrase", synth.Run, default);

        clip.Entry.Should().BeNull();
        (await Rows(harness)).Should().BeEmpty();
        Directory.EnumerateFiles(harness.AudioCacheDirectory).Should().BeEmpty(
            "an unrecorded clip leaves no file behind");
    }

    private static UiSpeechClipCache Cache(ActivityPageHarness harness) => new(
        harness.Services.GetRequiredService<StreamHistoryRepository>(),
        harness.Services.GetRequiredService<SentenceStudio.Abstractions.IFileSystemService>(),
        NullLogger.Instance);

    private static Task<List<StreamHistory>> Rows(ActivityPageHarness harness) =>
        harness.WithDbAsync(db => db.StreamHistories.AsNoTracking().ToListAsync());

    private static Task<StreamHistory> AddRowAsync(ActivityPageHarness harness, string owner, string phrase,
        string voice, string path, DateTime? createdAt = null) =>
        harness.WithDbAsync(async db =>
        {
            var row = new StreamHistory
            {
                UserProfileId = owner,
                Phrase = phrase,
                VoiceId = voice,
                AudioFilePath = path,
                CreatedAt = createdAt ?? DateTime.UtcNow,
                UpdatedAt = createdAt ?? DateTime.UtcNow,
            };
            db.StreamHistories.Add(row);
            await db.SaveChangesAsync();
            return row;
        });

    [Fact]
    public async Task A_clip_in_the_apis_default_voice_plays_but_is_never_kept_under_the_requested_voice()
    {
        await using var harness = await ActivityPageHarness.CreateAsync();
        var calls = 0;
        Task<Stream> Substituted(string voice, CancellationToken token)
        {
            calls++;
            return Task.FromResult<Stream>(new SentenceStudio.Services.Api.SpeechClipStream(
                Clip, "21m00Tcm4TlvDq8ikWAM", voiceSubstituted: true));
        }

        var first = await Cache(harness).GetOrCreateAsync("안녕하세요", "voice-1", true, "phrase", Substituted, default);
        var second = await Cache(harness).GetOrCreateAsync("안녕하세요", "voice-1", true, "phrase", Substituted, default);

        first.Status.Should().Be(UiSpeechClipStatus.Ready);
        first.Audio.Should().Equal(Clip, "the clip still plays");
        first.Entry.Should().BeNull();
        (await Rows(harness)).Should().BeEmpty("no row may name voice-1 for a clip in another voice");
        Directory.GetFiles(harness.AudioCacheDirectory).Should().BeEmpty();
        second.Status.Should().Be(UiSpeechClipStatus.Ready);
        calls.Should().Be(2, "nothing was cached, so the next play asks again");
    }

    [Fact]
    public async Task A_clip_from_an_older_server_that_names_no_voice_plays_but_is_not_kept()
    {
        await using var harness = await ActivityPageHarness.CreateAsync();

        var clip = await Cache(harness).GetOrCreateAsync("안녕하세요", "voice-1", true, "phrase",
            (voice, token) => Task.FromResult<Stream>(new SentenceStudio.Services.Api.SpeechClipStream(
                Clip, voiceId: null, voiceSubstituted: null)), default);

        clip.Status.Should().Be(UiSpeechClipStatus.Ready);
        clip.Audio.Should().Equal(Clip);
        (await Rows(harness)).Should().BeEmpty("an unconfirmed voice is never recorded as voice-1");
        Directory.GetFiles(harness.AudioCacheDirectory).Should().BeEmpty();
    }

    [Fact]
    public async Task A_synced_entry_regenerated_in_the_apis_default_voice_gets_no_local_copy()
    {
        await using var harness = await ActivityPageHarness.CreateAsync();
        var synced = await AddRowAsync(harness, ActivityPageHarness.OwnerA, "감사합니다", "phone-voice",
            "/var/mobile/Containers/Data/Application/PHONE/Library/AudioCache/phrase_phone.mp3");

        var clip = await Cache(harness).GetForEntryAsync(synced, "voice-1", true, "phrase",
            (voice, token) => Task.FromResult<Stream>(new SentenceStudio.Services.Api.SpeechClipStream(
                Clip, "21m00Tcm4TlvDq8ikWAM", voiceSubstituted: true)), default);

        clip.Status.Should().Be(UiSpeechClipStatus.Ready);
        clip.Audio.Should().Equal(Clip);
        File.Exists(Path.Combine(harness.AudioCacheDirectory, StreamHistory.RegeneratedClipFileName(synced.Id)))
            .Should().BeFalse("the entry's own voice was not used");
    }

    // ---- A clip being made is shared (review of b4c0da41: synthesis outlives the quiz's playback)

    [Fact]
    public async Task ARequestWhileTheSameClipIsBeingMade_WaitsForIt_AndNothingIsStoredTwice()
    {
        await using var harness = await ActivityPageHarness.CreateAsync();
        var cache = Cache(harness);
        var synth = new GatedSynthesizer(Clip);

        var first = cache.GetOrCreateAsync("안녕하세요", "voice-1", true, "word", synth.Run, default);
        await synth.Started.WaitAsync(TimeSpan.FromSeconds(10));
        var second = cache.GetOrCreateAsync("안녕하세요", "voice-1", true, "word", synth.Run, default);
        synth.Release();

        var made = await first;
        made.Should().Match<UiSpeechClip>(c => c.Status == UiSpeechClipStatus.Ready && c.Synthesized);
        (await second).Should().BeSameAs(made);
        synth.Calls.Should().ContainSingle("one paid synthesis");
        (await Rows(harness)).Should().ContainSingle();
        Directory.GetFiles(harness.AudioCacheDirectory).Should().ContainSingle();
    }

    [Fact]
    public async Task ACancelledRequest_IsNotHandedToTheRequestThatJoinedIt()
    {
        await using var harness = await ActivityPageHarness.CreateAsync();
        var cache = Cache(harness);
        var synth = new GatedSynthesizer(Clip);
        using var firstCaller = new CancellationTokenSource();

        var first = cache.GetOrCreateAsync("안녕하세요", "voice-1", true, "word", synth.Run, firstCaller.Token);
        await synth.Started.WaitAsync(TimeSpan.FromSeconds(10));
        var second = cache.GetOrCreateAsync("안녕하세요", "voice-1", true, "word", synth.Run, default);
        firstCaller.Cancel();
        await FluentActions.Awaiting(() => first).Should().ThrowAsync<OperationCanceledException>();
        synth.Release();

        var clip = await second;
        clip.Should().Match<UiSpeechClip>(c => c.Status == UiSpeechClipStatus.Ready && c.Synthesized,
            "the joined request makes the clip itself");
        synth.Calls.Should().HaveCount(2);
        (await Rows(harness)).Should().ContainSingle();
        (await cache.GetOrCreateAsync("안녕하세요", "voice-1", true, "word", synth.Run, default))
            .Synthesized.Should().BeFalse("a later request plays the stored clip");
    }

    [Fact]
    public async Task AFailedRequest_IsNotHandedToALaterOne()
    {
        await using var harness = await ActivityPageHarness.CreateAsync();
        var cache = Cache(harness);
        var synth = new GatedSynthesizer(Clip);

        var first = cache.GetOrCreateAsync("안녕하세요", "voice-1", true, "word", synth.Run, default);
        await synth.Started.WaitAsync(TimeSpan.FromSeconds(10));
        // The store's owner check fails once the clip is made.
        await harness.WithDbAsync(db => db.Database.ExecuteSqlRawAsync(
            "ALTER TABLE \"UserProfile\" RENAME TO \"UserProfile_unavailable\""));
        try
        {
            synth.Release();
            await FluentActions.Awaiting(() => first).Should().ThrowAsync<System.Data.Common.DbException>();
        }
        finally
        {
            await harness.WithDbAsync(db => db.Database.ExecuteSqlRawAsync(
                "ALTER TABLE \"UserProfile_unavailable\" RENAME TO \"UserProfile\""));
        }

        var later = await cache.GetOrCreateAsync("안녕하세요", "voice-1", true, "word", synth.Run, default);

        later.Should().Match<UiSpeechClip>(c => c.Status == UiSpeechClipStatus.Ready && c.Synthesized);
        synth.Calls.Should().HaveCount(2);
        (await Rows(harness)).Should().ContainSingle();
    }

    [Fact]
    public async Task WithNoActiveLearner_RequestsAreNotShared()
    {
        await using var harness = await ActivityPageHarness.CreateAsync();
        harness.Preferences.Remove(UserProfileRepository.ActiveProfileIdKey);
        var cache = Cache(harness);
        var synth = new GatedSynthesizer(Clip);

        var first = cache.GetOrCreateAsync("안녕하세요", "voice-1", true, "word", synth.Run, default);
        await synth.Started.WaitAsync(TimeSpan.FromSeconds(10));
        var second = cache.GetOrCreateAsync("안녕하세요", "voice-1", true, "word", synth.Run, default);
        synth.Release();

        (await first).Status.Should().Be(UiSpeechClipStatus.Ready);
        (await second).Status.Should().Be(UiSpeechClipStatus.Ready);
        synth.Calls.Should().HaveCount(2, "with no learner nothing is shared");
        (await Rows(harness)).Should().BeEmpty();
    }

    /// <summary>Synthesizes once released; a cancelled caller stops waiting.</summary>
    private sealed class GatedSynthesizer(byte[] audio)
    {
        private readonly TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public List<string> Calls { get; } = new();
        public Task Started => _started.Task;

        public void Release() => _gate.TrySetResult();

        public async Task<Stream> Run(string voice, CancellationToken cancellationToken)
        {
            lock (Calls)
                Calls.Add(voice);
            _started.TrySetResult();
            await _gate.Task.WaitAsync(cancellationToken);
            return new MemoryStream(audio);
        }
    }

    private sealed class CountingSynthesizer(byte[] audio)
    {
        public List<string> Calls { get; } = new();

        public Task<Stream> Run(string voice, CancellationToken cancellationToken)
        {
            Calls.Add(voice);
            return Task.FromResult<Stream>(new MemoryStream(audio));
        }
    }
}
