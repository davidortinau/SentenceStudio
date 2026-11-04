using Microsoft.Extensions.DependencyInjection;
using SentenceStudio.Services;
using SentenceStudio.Shared.Models;
using SentenceStudio.WebUI.Pages;
using SentenceStudio.WebUI.Services;
using SentenceStudio.WebUI.Shared;

namespace SentenceStudio.UI.Tests.Activities;

/// <summary>
/// Shadowing fixes ported from the native presenter (docs/native-port-backports.md
/// B26-B28) on the real page, for both the device player (MAUI) and browser audio (web).
/// </summary>
public sealed class ShadowingPageTests
{
    [Fact]
    public async Task Slow_OnTheDevicePlayer_PlaysTheNormalClipAtSixTenths_AndNormalReusesIt()
    {
        await using var harness = await OpenAsync(browser: false);

        await harness.DispatchAsync("SetPlaybackSpeed", 0.6f);

        harness.Speech.SynthesisCount.Should().Be(1);
        harness.Speech.Bodies.Single(body => body.Contains("voice_settings"))
            .Should().MatchRegex("\"speed\":\\s*1(\\.0)?[,}]", "the clip is synthesized at normal speed");
        var slow = harness.Audio.Players.Last();
        slow.Rate.Should().Be(0.6);
        slow.Events.Should().ContainInOrder("rate:0.6", "play@0.6");

        await harness.DispatchAsync("SetPlaybackSpeed", 1.0f);

        harness.Speech.SynthesisCount.Should().Be(1, "changing speed replays the same clip");
        harness.Audio.Players.Last().Events.Should().ContainInOrder("rate:1", "play@1");
    }

    [Fact]
    public async Task Slow_InTheBrowser_AppliesThePlaybackRateBeforePlaybackStarts()
    {
        await using var harness = await OpenAsync(browser: true);
        harness.Js.Calls.Clear();

        await harness.DispatchAsync("SetPlaybackSpeed", 0.6f);

        var calls = harness.Js.Calls.Select(call => call.Identifier).ToList();
        calls.Should().ContainInOrder(
            "audioInterop.loadFromBase64", "audioInterop.setPlaybackRate", "audioInterop.startPlayback");
        harness.Js.Calls.Single(call => call.Identifier == "audioInterop.setPlaybackRate")
            .Args.Should().Equal(0.6d);
        harness.Get<bool>("isAudioPlaying").Should().BeTrue();
    }

    [Fact]
    public async Task Normal_InTheBrowser_DoesNotDependOnThePlaybackRateCall()
    {
        await using var harness = await OpenAsync(browser: true);
        harness.Js.Calls.Clear();

        await harness.DispatchAsync("TogglePlayback");

        harness.Js.Calls.Select(call => call.Identifier).Should().NotContain("audioInterop.setPlaybackRate");
        harness.Get<bool>("isAudioPlaying").Should().BeTrue();
    }

    [Fact]
    public async Task WaveformTap_SeeksTheDevicePlayerToTheTappedPosition()
    {
        await using var harness = await OpenAsync(browser: false);
        await harness.DispatchAsync("GenerateAudioForCurrent");
        var html = harness.Html();
        html.Should().Contain("class=\"waveform-seek\"");
        html.Should().Contain($"aria-label=\"{Text(harness, "NativeShadowing_PlaybackPosition")}\"");

        var fraction = WaveformDisplay.ToFraction("250");
        fraction.Should().Be(0.25);
        await harness.DispatchAsync("HandleSeek", fraction!.Value);

        harness.Audio.Players.Last().Events.Should().Contain("seek:0.5", "a quarter of a two-second clip");
        harness.Get<double>("playbackPosition").Should().Be(0.25);
    }

    [Fact]
    public async Task WaveformTap_InTheBrowser_SeeksWhilePlaying()
    {
        await using var harness = await OpenAsync(browser: true);
        await harness.DispatchAsync("TogglePlayback");
        harness.Js.Calls.Clear();

        await harness.DispatchAsync("HandleSeek", 0.5d);

        harness.Js.Calls.Should().ContainSingle(call => call.Identifier == "audioInterop.seekTo")
            .Which.Args.Should().Equal(0.5d * ActivityPageHarness.RecordingJsRuntime.Duration);
        // The playhead estimate keeps advancing from the seek point while playing.
        harness.Get<double>("playbackPosition").Should().BeInRange(0.5, 0.55);
    }

    [Theory]
    [InlineData(true, "NativeShadowing_AudioFailed")]
    [InlineData(false, "OfflineAudioUnavailable")]
    public async Task AudioThatCannotBePrepared_IsReportedOnThePage_AndPlayTriesAgain(bool online, string key)
    {
        await using var harness = await OpenAsync(browser: false);
        harness.Speech.Audio = [];
        harness.Connectivity.IsInternetAvailable = online;

        await harness.DispatchAsync("GenerateAudioForCurrent");

        var html = harness.Html();
        html.Should().Contain("id=\"shadowing-audio-status\"");
        html.Should().Contain(Text(harness, key));
        html.Should().Contain("role=\"alert\"");
        harness.Audio.Players.Should().BeEmpty();

        harness.Speech.Audio = [0x49, 0x44, 0x33];
        harness.Connectivity.IsInternetAvailable = true;
        await harness.DispatchAsync("TogglePlayback");

        harness.Html().Should().NotContain("id=\"shadowing-audio-status\"");
        harness.Audio.Players.Should().ContainSingle().Which.IsPlaying.Should().BeTrue();
    }

    private static async Task<ActivityPageHarness> OpenAsync(bool browser)
    {
        var harness = await ActivityPageHarness.CreateAsync(services =>
        {
            services.AddSingleton(ActivityPageHarness.Unreached<ShadowingService>());
            services.AddSingleton(ActivityPageHarness.Unreached<SentenceStudio.Services.Progress.IProgressService>());
        });
        harness.Audio.CanPlayLocally = !browser;
        await harness.RenderAsync<Shadowing>();
        var sentence = new ShadowingSentence { TargetLanguageText = "안녕하세요", NativeLanguageText = "Hello" };
        harness.Set("sentences", new List<ShadowingSentence> { sentence });
        harness.Set("currentSentence", sentence);
        harness.Set("currentIndex", 0);
        harness.Set("voiceId", "voice-1");
        harness.Toasts.Clear();
        return harness;
    }

    private static string Text(ActivityPageHarness harness, string key) =>
        harness.Get<BlazorLocalizationService>("Localize")[key];
}
