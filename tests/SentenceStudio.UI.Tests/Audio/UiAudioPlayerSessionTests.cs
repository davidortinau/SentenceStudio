using SentenceStudio.Abstractions;
using SentenceStudio.WebApp.Platform;
using SentenceStudio.WebUI.Services;

namespace SentenceStudio.UI.Tests.Audio;

public class UiAudioPlayerSessionTests
{
    [Fact]
    public void NativePlayer_KeepsItsStreamOpenUntilStopped()
    {
        var player = new FakePlayer();
        var factory = new FakeFactory { Create = _ => player };
        var stream = new TrackingStream();
        using var session = new UiAudioPlayerSession();

        var created = session.CreatePlayer(factory, stream);
        created.Should().BeSameAs(player);
        session.Player.Should().BeSameAs(player);
        stream.Disposed.Should().BeFalse();
        player.PlayCalls.Should().Be(0, "creating a player must not autoplay an activity's audio");

        created.Play();
        session.Stop();

        player.PlayCalls.Should().Be(1);
        player.StopCalls.Should().Be(1);
        player.DisposeCalls.Should().Be(1);
        stream.Disposed.Should().BeTrue();
        session.Player.Should().BeNull();
    }

    [Fact]
    public void ReplacingPlayer_StopsAndDisposesThePreviousPlayerAndStream()
    {
        var first = new FakePlayer();
        var second = new FakePlayer();
        var factory = new FakeFactory { Create = stream => stream.Position == 1 ? first : second };
        var firstStream = new TrackingStream();
        firstStream.Position = 1;
        var secondStream = new TrackingStream();
        using var session = new UiAudioPlayerSession();

        session.CreatePlayer(factory, firstStream);
        session.CreatePlayer(factory, secondStream);

        first.StopCalls.Should().Be(1);
        first.DisposeCalls.Should().Be(1);
        firstStream.Disposed.Should().BeTrue();
        secondStream.Disposed.Should().BeFalse();
        session.Player.Should().BeSameAs(second);

        session.Stop();
        secondStream.Disposed.Should().BeTrue();
    }

    [Fact]
    public void UnavailableNativePlayer_FailsInsteadOfPretendingToPlay()
    {
        var factory = new FakeFactory { Create = _ => null };
        var stream = new TrackingStream();
        using var session = new UiAudioPlayerSession();

        Action create = () => session.CreatePlayer(factory, stream);

        create.Should().Throw<InvalidOperationException>().WithMessage("*unavailable*");
        factory.CreateCalls.Should().Be(1);
        stream.Disposed.Should().BeTrue();
        session.Player.Should().BeNull();
    }

    [Fact]
    public void FailedReplacement_ClosesBothStreamsEvenWhenStoppingTheOldPlayerThrows()
    {
        var oldPlayer = new FakePlayer { ThrowOnStop = true };
        var factory = new FakeFactory { Create = stream => stream.Position == 1 ? oldPlayer : new FakePlayer() };
        var oldStream = new TrackingStream();
        oldStream.Position = 1;
        var replacement = new TrackingStream();
        using var session = new UiAudioPlayerSession();
        session.CreatePlayer(factory, oldStream);

        Action createReplacement = () => session.CreatePlayer(factory, replacement);

        createReplacement.Should().Throw<InvalidOperationException>();
        oldPlayer.DisposeCalls.Should().Be(1);
        oldStream.Disposed.Should().BeTrue();
        replacement.Disposed.Should().BeTrue();
        session.Player.Should().BeNull();
    }

    [Fact]
    public void BrowserFactory_IsNeverAskedToCreateAServerSidePlayer()
    {
        var factory = new FakeFactory { CanPlayLocally = false };
        var stream = new TrackingStream();
        using var session = new UiAudioPlayerSession();

        Action create = () => session.CreatePlayer(factory, stream);

        create.Should().Throw<PlatformNotSupportedException>();
        factory.CreateCalls.Should().Be(0);
        stream.Disposed.Should().BeTrue();
    }

    [Fact]
    public void WebAdapter_ExplicitlyRefusesServerSideAudio()
    {
        IAudioPlayerFactory factory = new WebAudioPlayerFactory();
        using var stream = new MemoryStream(new byte[] { 1, 2, 3 });

        factory.CanPlayLocally.Should().BeFalse();
        Action create = () => factory.CreatePlayer(stream);
        create.Should().Throw<PlatformNotSupportedException>();
    }

    private sealed class FakeFactory : IAudioPlayerFactory
    {
        public bool CanPlayLocally { get; init; } = true;
        public Func<Stream, IUiAudioPlayer?> Create { get; init; } = _ => new FakePlayer();
        public int CreateCalls { get; private set; }

        public IUiAudioPlayer? CreatePlayer(Stream audioStream)
        {
            CreateCalls++;
            return Create(audioStream);
        }
    }

    private sealed class FakePlayer : IUiAudioPlayer
    {
        public event EventHandler? PlaybackEnded;
        public bool IsPlaying { get; private set; }
        public double CurrentPosition { get; private set; }
        public double Duration => 5;
        public int PlayCalls { get; private set; }
        public int StopCalls { get; private set; }
        public int DisposeCalls { get; private set; }
        public bool ThrowOnStop { get; init; }

        public void Play() { PlayCalls++; IsPlaying = true; }
        public void Pause() => IsPlaying = false;
        public void Stop()
        {
            StopCalls++;
            if (ThrowOnStop) throw new InvalidOperationException("The old player failed to stop.");
            IsPlaying = false;
        }
        public void Seek(double seconds) => CurrentPosition = seconds;
        public void Dispose() => DisposeCalls++;
        public void End() => PlaybackEnded?.Invoke(this, EventArgs.Empty);
    }

    private sealed class TrackingStream : MemoryStream
    {
        public bool Disposed { get; private set; }

        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            base.Dispose(disposing);
        }
    }
}
