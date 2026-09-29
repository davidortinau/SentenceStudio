using Microsoft.Extensions.Logging.Abstractions;
using SentenceStudio.Abstractions;
using SentenceStudio.Models;
using SentenceStudio.Services;
using SentenceStudio.Shared.Models;

namespace SentenceStudio.AppLib.Tests.Services;

public sealed class PlatformAudioBoundaryTests
{
    [Fact]
    public async Task Browser_capability_skips_native_player_creation()
    {
        var factory = new FakeFactory { CanPlayLocally = false };
        using var manager = CreateManager(factory);

        await manager.LoadAudioAsync(EmptyAudio());

        Assert.False(manager.HasNativePlayer);
        Assert.Equal(0, factory.CreationCount);
    }

    [Fact]
    public async Task Local_player_supports_playback_and_is_disposed()
    {
        var factory = new FakeFactory { CanPlayLocally = true };
        var manager = CreateManager(factory);

        await manager.LoadAudioAsync(EmptyAudio());
        manager.Play();

        Assert.True(manager.HasNativePlayer);
        Assert.True(manager.IsPlaying);
        Assert.Equal(1, factory.CreationCount);

        manager.Stop();
        manager.Dispose();

        Assert.False(factory.Player.IsPlaying);
        Assert.True(factory.Player.IsDisposed);
    }

    [Fact]
    public void Shared_app_state_exposes_profile_without_MAUI_types()
    {
        var properties = typeof(IAppState).GetProperties();

        Assert.Contains(properties, property =>
            property.Name == nameof(IAppState.CurrentUserProfile)
            && property.PropertyType == typeof(UserProfile));
        Assert.DoesNotContain(properties, property =>
            property.PropertyType.Assembly.GetName().Name?.StartsWith("Microsoft.Maui", StringComparison.Ordinal) == true);
    }

    private static TimestampedAudioManager CreateManager(FakeFactory factory) =>
        new(
            new SentenceTimingCalculator(NullLogger<SentenceTimingCalculator>.Instance),
            factory,
            NullLogger<TimestampedAudioManager>.Instance);

    private static TimestampedAudio EmptyAudio() =>
        new() { AudioData = [1, 2, 3], FullTranscript = string.Empty, Characters = [] };

    private sealed class FakeFactory : IAudioPlayerFactory
    {
        public bool CanPlayLocally { get; init; }
        public int CreationCount { get; private set; }
        public FakePlayer Player { get; } = new();

        public IUiAudioPlayer? CreatePlayer(Stream audioStream)
        {
            CreationCount++;
            return Player;
        }
    }

    private sealed class FakePlayer : IUiAudioPlayer
    {
        public event EventHandler? PlaybackEnded;
        public bool IsPlaying { get; private set; }
        public bool IsDisposed { get; private set; }
        public double CurrentPosition => 0;
        public double Duration => 1;

        public void Play() => IsPlaying = true;
        public void Pause() => IsPlaying = false;
        public void Stop() => IsPlaying = false;
        public void Seek(double seconds) { }
        public void Dispose() => IsDisposed = true;
    }
}
