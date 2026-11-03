using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SentenceStudio.Services.Speech;
using SentenceStudio.Shared.Models;
using SentenceStudio.WebUI.Pages;
using SentenceStudio.WebUI.Services;

namespace SentenceStudio.UI.Tests.Activities;

/// <summary>
/// How Do You Say fixes ported from the native presenter (docs/native-port-backports.md
/// B31, B32, B35, B38) on the real page with the owner-scoped history store.
/// </summary>
public sealed class HowDoYouSayPageTests
{
    private const string Voice = "voice-selected";

    [Fact]
    public async Task SpeakingTheSamePhraseAgain_PlaysTheCachedClip_WithoutASecondEntry()
    {
        await using var harness = await OpenAsync();

        await SpeakAsync(harness, "안녕하세요");
        await SpeakAsync(harness, "안녕하세요");

        harness.Speech.SynthesisCount.Should().Be(1, "the second request reuses the cached clip");
        (await Rows(harness)).Should().ContainSingle().Which.Phrase.Should().Be("안녕하세요");
        harness.Get<List<StreamHistory>>("history").Should().ContainSingle();
        harness.Audio.Players.Should().HaveCount(2);
        harness.Audio.Players.Should().OnlyContain(player => player.Audio.SequenceEqual(harness.Speech.Audio));
        harness.Toasts.Select(t => t.Text).Should().Equal(
            Text(harness, "NativeHowDoYouSay_Generated"), Text(harness, "NativeHowDoYouSay_Generated"));
    }

    [Fact]
    public async Task SyncedEntryMadeOnAnotherDevice_IsSynthesizedInItsVoice_AndTheSameEntryPlays()
    {
        await using var harness = await OpenAsync();
        var synced = await AddRowAsync(harness, "감사합니다", "phone-voice",
            "/var/mobile/Containers/Data/Application/PHONE/Library/AudioCache/phrase_phone.mp3");
        await harness.DispatchAsync("LoadHistory");

        await harness.DispatchAsync("PlayHistoryItem", synced);

        harness.Speech.Requests.Should().ContainSingle(path => path.EndsWith("/text-to-speech/phone-voice",
            StringComparison.Ordinal), "the entry's own voice is used, not the selected one");
        var row = (await Rows(harness)).Should().ContainSingle("no second entry is added").Which;
        row.Id.Should().Be(synced.Id);
        Path.GetDirectoryName(row.AudioFilePath).Should().Be(harness.AudioCacheDirectory);
        harness.Get<StreamHistory?>("currentPlayingItem")!.Id.Should().Be(synced.Id);
        harness.Get<bool>("isAudioPlaying").Should().BeTrue();
        harness.Audio.Players.Should().ContainSingle();
        harness.Toasts.Should().BeEmpty();
    }

    [Fact]
    public async Task EntryFromAnEarlierAppContainer_PlaysFromTodaysAudioCache()
    {
        await using var harness = await OpenAsync();
        var fileName = $"phrase_{Guid.NewGuid():N}.mp3";
        byte[] cached = [0x49, 0x44, 0x33, 0x09];
        await File.WriteAllBytesAsync(Path.Combine(harness.AudioCacheDirectory, fileName), cached);
        var entry = await AddRowAsync(harness, "안녕", Voice,
            $"/var/mobile/Containers/Data/Application/{Guid.NewGuid()}/Library/AudioCache/{fileName}");
        await harness.DispatchAsync("LoadHistory");

        await harness.DispatchAsync("PlayHistoryItem", entry);

        harness.Speech.SynthesisCount.Should().Be(0);
        harness.Audio.Players.Should().ContainSingle().Which.Audio.Should().Equal(cached);
        harness.Toasts.Should().BeEmpty();
    }

    [Fact]
    public async Task SyncedEntryWhileOffline_SaysAudioIsUnavailableOffline()
    {
        await using var harness = await OpenAsync();
        var synced = await AddRowAsync(harness, "감사합니다", "phone-voice", "/phone/AudioCache/x.mp3");
        harness.Connectivity.IsInternetAvailable = false;

        await harness.DispatchAsync("PlayHistoryItem", synced);

        harness.Speech.SynthesisCount.Should().Be(0);
        harness.Toasts.Should().ContainSingle().Which.Should().Be(
            new ToastMessage(Text(harness, "OfflineAudioUnavailable"), ToastType.Warning, 4000));
        harness.Audio.Players.Should().BeEmpty();
    }

    [Fact]
    public async Task FailedSynthesis_ShowsALocalizedMessage_AndNoExceptionText()
    {
        await using var harness = await OpenAsync();
        harness.Speech.Audio = [];

        await SpeakAsync(harness, "안녕하세요");

        harness.Toasts.Should().ContainSingle().Which.Should().Be(
            new ToastMessage(Text(harness, "NativeHowDoYouSay_GenerateFailed"), ToastType.Error, 5000));
        (await Rows(harness)).Should().BeEmpty();
        harness.Audio.Players.Should().BeEmpty();
    }

    [Fact]
    public async Task HistoryTimes_AreShownInTheLearnersTimeZone()
    {
        await using var harness = await OpenAsync();
        harness.Clock.TimeZone = SentenceStudio.Services.Plans.TimeZoneResolver.TryResolve("Asia/Seoul", out var seoul)
            ? seoul : throw new InvalidOperationException("Asia/Seoul is unavailable on this machine.");
        var createdUtc = new DateTime(2026, 11, 2, 23, 30, 0, DateTimeKind.Utc);
        await AddRowAsync(harness, "안녕", Voice, "/phone/AudioCache/y.mp3", createdUtc);

        await harness.DispatchAsync("LoadHistory");

        var culture = harness.Get<BlazorLocalizationService>("Localize").Culture;
        var html = harness.Html();
        html.Should().Contain(new DateTime(2026, 11, 3, 8, 30, 0).ToString("g", culture));
        html.Should().NotContain(createdUtc.ToString("g", culture));
    }

    private static async Task<ActivityPageHarness> OpenAsync()
    {
        var harness = await ActivityPageHarness.CreateAsync(services =>
            services.AddSingleton<IVoiceDiscoveryService>(new NoVoices()));
        await harness.RenderAsync<HowDoYouSay>();
        harness.Set("selectedVoiceId", Voice);
        return harness;
    }

    private static async Task SpeakAsync(ActivityPageHarness harness, string phrase)
    {
        harness.Set("phrase", phrase);
        await harness.DispatchAsync("SubmitPhrase");
    }

    private static string Text(ActivityPageHarness harness, string key) =>
        harness.Get<BlazorLocalizationService>("Localize")[key];

    private static Task<List<StreamHistory>> Rows(ActivityPageHarness harness) =>
        harness.WithDbAsync(db => db.StreamHistories.AsNoTracking()
            .Where(row => row.UserProfileId == ActivityPageHarness.OwnerA).ToListAsync());

    private static Task<StreamHistory> AddRowAsync(ActivityPageHarness harness, string phrase, string voice,
        string path, DateTime? createdAt = null) =>
        harness.WithDbAsync(async db =>
        {
            var row = new StreamHistory
            {
                UserProfileId = ActivityPageHarness.OwnerA,
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

    private sealed class NoVoices : IVoiceDiscoveryService
    {
        public Task<List<VoiceInfo>> GetVoicesForLanguageAsync(string language, bool forceRefresh = false) =>
            Task.FromResult(new List<VoiceInfo>());
        public string? GetLanguageCode(string language) => "ko";
        public IReadOnlyList<string> SupportedLanguages => ["Korean"];
        public void ClearCache() { }
    }
}
