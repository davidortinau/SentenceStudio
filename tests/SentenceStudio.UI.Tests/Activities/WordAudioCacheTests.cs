using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.JSInterop;
using SentenceStudio.Abstractions;
using SentenceStudio.Data;
using SentenceStudio.Services;
using SentenceStudio.Shared.Models;
using SentenceStudio.WebUI.Pages;
using SentenceStudio.WebUI.Services;
using MinimalPairSession = SentenceStudio.WebUI.Pages.MinimalPairSession;

namespace SentenceStudio.UI.Tests.Activities;

/// <summary>
/// Backport B35 for the Vocabulary Quiz, word editor and Minimal Pairs: they checked a cached
/// clip's stored absolute path, which changes when iOS moves the app container on reinstall
/// (and names another device's path for a synced row), so every cached clip was synthesized
/// again and a second history row added. They now go through the shared UiSpeechClipCache:
/// the clip is found by file name under today's AudioCache, and a missing clip is synthesized
/// once and the same row pointed at it.
/// </summary>
public sealed class WordAudioCacheTests
{
    private const string Term = "책";

    public static TheoryData<string> Pages => new() { "VocabQuiz", "VocabularyWordEdit", "MinimalPairSession" };

    [Theory]
    [MemberData(nameof(Pages))]
    public async Task ClipFromAnEarlierAppContainer_PlaysFromTodaysAudioCache(string pageName)
    {
        await using var harness = await ActivityPageHarness.CreateAsync();
        var fileName = $"word_{Guid.NewGuid():N}.mp3";
        byte[] cached = [0x49, 0x44, 0x33, 0x07];
        await File.WriteAllBytesAsync(Path.Combine(harness.AudioCacheDirectory, fileName), cached);
        await AddRowAsync(harness, VoiceFor(harness, pageName),
            $"/var/mobile/Containers/Data/Application/{Guid.NewGuid()}/Library/AudioCache/{fileName}");

        await PlayAsync(harness, pageName);

        harness.Speech.SynthesisCount.Should().Be(0, "the cached clip is still on this device");
        harness.Audio.Players.Should().ContainSingle().Which.Audio.Should().Equal(cached);
        (await Rows(harness)).Should().ContainSingle();
    }

    [Theory]
    [MemberData(nameof(Pages))]
    public async Task RowWhoseClipIsNotOnThisDevice_IsSynthesizedOnce_AndTheSameRowPointsAtIt(string pageName)
    {
        await using var harness = await ActivityPageHarness.CreateAsync();
        var synced = await AddRowAsync(harness, VoiceFor(harness, pageName),
            "/var/mobile/Containers/Data/Application/PHONE/Library/AudioCache/word_phone.mp3");

        await PlayAsync(harness, pageName);
        await PlayAsync(harness, pageName);

        harness.Speech.SynthesisCount.Should().Be(1, "the second play uses the clip cached by the first");
        var row = (await Rows(harness)).Should().ContainSingle("no second history row is added").Which;
        row.Id.Should().Be(synced.Id);
        Path.GetDirectoryName(row.AudioFilePath).Should().Be(harness.AudioCacheDirectory);
        harness.Audio.Players.Should().HaveCount(2)
            .And.OnlyContain(player => player.Audio.SequenceEqual(harness.Speech.Audio));
    }

    [Fact]
    public async Task VocabQuiz_Offline_AudioModeIsAvailableForAClipOnThisDevice()
    {
        await using var harness = await ActivityPageHarness.CreateAsync();
        var fileName = $"word_{Guid.NewGuid():N}.mp3";
        await File.WriteAllBytesAsync(Path.Combine(harness.AudioCacheDirectory, fileName), [0x49, 0x44, 0x33]);
        await AddRowAsync(harness, VoiceFor(harness, "VocabQuiz"),
            $"/old-container/Library/AudioCache/{fileName}");
        harness.Connectivity.IsInternetAvailable = false;
        var quiz = CreatePage(harness, "VocabQuiz");

        var canPlay = await (Task<bool>)Method(quiz, "CanPlayAudioAsync").Invoke(quiz, [Term, "Korean"])!;

        canPlay.Should().BeTrue("the clip is on this device, found by its file name");
    }

    private static string VoiceFor(ActivityPageHarness harness, string pageName)
    {
        var voices = harness.Services.GetRequiredService<SpeechVoicePreferences>();
        return pageName == "VocabQuiz" ? voices.GetVoiceForLanguage("Korean") : voices.VoiceId;
    }

    private static async Task PlayAsync(ActivityPageHarness harness, string pageName)
    {
        var page = CreatePage(harness, pageName);
        var play = pageName switch
        {
            "VocabQuiz" => (Task)Method(page, "PlayAudioTextAsync").Invoke(page, [Term, "Korean"])!,
            "VocabularyWordEdit" => (Task)Method(page, "PlayTermAudio").Invoke(page, null)!,
            _ => (Task)Method(page, "PlayPromptAudio").Invoke(page, null)!,
        };
        await play;
    }

    /// <summary>The page with only what its play handler reads: real owner-scoped stores and fakes for speech and audio.</summary>
    private static object CreatePage(ActivityPageHarness harness, string pageName)
    {
        var services = harness.Services;
        object page = pageName switch
        {
            "VocabQuiz" => new VocabQuiz(),
            "VocabularyWordEdit" => new VocabularyWordEdit(),
            _ => new MinimalPairSession(),
        };
        Set(page, "StreamHistoryRepo", services.GetRequiredService<StreamHistoryRepository>());
        Set(page, "FileSystemService", services.GetRequiredService<IFileSystemService>());
        Set(page, "Connectivity", services.GetRequiredService<IConnectivityService>());
        Set(page, "SpeechService", services.GetRequiredService<ElevenLabsSpeechService>());
        Set(page, "VoicePrefs", services.GetRequiredService<SpeechVoicePreferences>());
        Set(page, "AudioPlayerFactory", services.GetRequiredService<IAudioPlayerFactory>());
        Set(page, "JS", services.GetRequiredService<IJSRuntime>());
        Set(page, "Localize", new BlazorLocalizationService());
        Set(page, "Toast", services.GetRequiredService<ToastService>());
        switch (page)
        {
            case VocabQuiz:
                Set(page, "Logger", NullLogger<VocabQuiz>.Instance);
                break;
            case VocabularyWordEdit:
                Set(page, "Logger", NullLogger<VocabularyWordEdit>.Instance);
                Set(page, "targetLanguageTerm", Term);
                break;
            default:
                Set(page, "Logger", NullLogger<MinimalPairSession>.Instance);
                Set(page, "promptWord", new VocabularyWord { Id = Guid.NewGuid().ToString(), TargetLanguageTerm = Term });
                break;
        }
        return page;
    }

    private static Task<List<StreamHistory>> Rows(ActivityPageHarness harness) =>
        harness.WithDbAsync(db => db.StreamHistories.AsNoTracking()
            .Where(row => row.UserProfileId == ActivityPageHarness.OwnerA).ToListAsync());

    private static Task<StreamHistory> AddRowAsync(ActivityPageHarness harness, string voice, string path) =>
        harness.WithDbAsync(async db =>
        {
            var row = new StreamHistory
            {
                UserProfileId = ActivityPageHarness.OwnerA,
                Phrase = Term,
                VoiceId = voice,
                AudioFilePath = path,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            };
            db.StreamHistories.Add(row);
            await db.SaveChangesAsync();
            return row;
        });

    private static MethodInfo Method(object page, string name) =>
        page.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(page.GetType().Name, name);

    private static void Set(object page, string name, object? value)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        var type = page.GetType();
        if (type.GetProperty(name, flags) is { } property)
            property.SetValue(page, value);
        else if (type.GetField(name, flags) is { } field)
            field.SetValue(page, value);
        else
            throw new MissingMemberException(type.Name, name);
    }
}
