using System.Collections.Immutable;
using System.Globalization;
using SentenceStudio.Native;
using Xunit;

namespace SentenceStudio.Native.Tests;

/// <summary>
/// Captain's 2026-11-02 decision: errors and warnings are banners that stay
/// until the next action; success and info are transient toasts (~3 s).
/// </summary>
public sealed class NativeMessageTests
{
    [Theory]
    [InlineData(NativeMessageKind.Success, NativeMessagePresentation.Toast)]
    [InlineData(NativeMessageKind.Info, NativeMessagePresentation.Toast)]
    [InlineData(NativeMessageKind.Warning, NativeMessagePresentation.Banner)]
    [InlineData(NativeMessageKind.Error, NativeMessagePresentation.Banner)]
    public void Kind_SelectsThePresentation(NativeMessageKind kind, NativeMessagePresentation expected)
    {
        var message = new NativeMessage(kind, "Key");
        Assert.Equal(expected, message.Presentation);
        Assert.Equal(expected == NativeMessagePresentation.Toast, message.IsToast);
        Assert.Equal(expected == NativeMessagePresentation.Banner, message.IsBanner);
    }

    [Fact]
    public void Format_UsesTheResourceTheArgumentOrLiteralText()
    {
        string Text(string key) => key switch
        {
            "Graded" => "Graded! Accuracy: {0}%",
            "Saved" => "Saved.",
            _ => throw new InvalidOperationException(key)
        };
        Assert.Equal("Saved.", NativeMessage.Success("Saved").Format(Text));
        Assert.Equal(string.Format(CultureInfo.CurrentCulture, "Graded! Accuracy: {0}%", "85"),
            NativeMessage.Success("Graded", "85").Format(Text));
        Assert.Equal("translated text", new NativeMessage(NativeMessageKind.Info, null, "translated text").Format(Text));
    }

    [Fact]
    public void Equality_IsContent_WhileEachShowingHasItsOwnId()
    {
        var first = NativeMessage.Error("Failed");
        var second = NativeMessage.Error("Failed");
        Assert.Equal(first, second);
        Assert.Equal(first.GetHashCode(), second.GetHashCode());
        Assert.NotEqual(first.Id, second.Id);
        var copy = first with { };
        Assert.Equal(first.Id, copy.Id);
        Assert.NotEqual(first, NativeMessage.Warning("Failed"));
        Assert.NotEqual(first, NativeMessage.Error("Failed", "x"));
    }

    [Fact]
    public void Split_KeepsBannersInOrderAndPicksTheNewestToast()
    {
        var error = NativeMessage.Error("SaveFailed");
        var success = NativeMessage.Success("Graded");
        var warning = NativeMessage.Warning("Offline");
        var info = NativeMessage.Info("Tricky");
        ImmutableArray<NativeMessage> messages = [error, success, warning, info];
        Assert.Equal(new[] { error, warning }, NativeMessages.Banners(messages).ToArray());
        Assert.Same(info, NativeMessages.LatestToast(messages));
        Assert.Empty(NativeMessages.Banners(default(ImmutableArray<NativeMessage>)));
        Assert.Null(NativeMessages.LatestToast(default(ImmutableArray<NativeMessage>)));
        Assert.Null(NativeMessages.LatestToast(error));
        Assert.Empty(NativeMessages.Banners(success));
        Assert.Equal(new[] { error }, NativeMessages.Banners(error).ToArray());
    }

    [Fact]
    public void Toast_DisappearsAfterThreeSecondsAndIsNotShownAgain()
    {
        var clock = new ManualClock();
        using var toasts = new NativeToastScheduler(clock);
        var changes = 0;
        toasts.Changed += () => changes++;
        var graded = NativeMessage.Success("Graded", "85");

        Assert.True(toasts.Offer(graded));
        Assert.Same(graded, toasts.Current);
        clock.Advance(TimeSpan.FromMilliseconds(2999));
        Assert.Same(graded, toasts.Current);
        Assert.Equal(0, changes);
        clock.Advance(TimeSpan.FromMilliseconds(1));
        Assert.Null(toasts.Current);
        Assert.Equal(1, changes);

        // The same message still in the presenter state (or a state copy) does not come back.
        Assert.False(toasts.Offer(graded));
        Assert.False(toasts.Offer(graded with { }));
        Assert.Null(toasts.Current);
        // Banners and nothing are never toasts.
        Assert.False(toasts.Offer(NativeMessage.Error("Failed")));
        Assert.False(toasts.Offer(null));
        Assert.Null(toasts.Current);
    }

    [Fact]
    public void NewerToast_ReplacesTheCurrentOneAndRestartsTheTimer()
    {
        var clock = new ManualClock();
        using var toasts = new NativeToastScheduler(clock);
        var first = NativeMessage.Success("Created");
        Assert.True(toasts.Offer(first));
        clock.Advance(TimeSpan.FromSeconds(2));
        var second = NativeMessage.Info("Tricky");
        Assert.True(toasts.Offer(second));
        clock.Advance(TimeSpan.FromSeconds(2));
        // The first toast's timer is gone; the second has 1 s left.
        Assert.Same(second, toasts.Current);
        // An older message offered late does not replace the newer one.
        Assert.False(toasts.Offer(first));
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Null(toasts.Current);
    }

    [Fact]
    public void Dismiss_ClosesTheToastAndCancelsItsTimer()
    {
        var clock = new ManualClock();
        using var toasts = new NativeToastScheduler(clock);
        var changes = 0;
        toasts.Changed += () => changes++;
        Assert.False(toasts.Dismiss());
        toasts.Offer(NativeMessage.Success("Deleted"));
        Assert.True(toasts.Dismiss());
        Assert.Null(toasts.Current);
        clock.Advance(TimeSpan.FromSeconds(5));
        Assert.Equal(0, changes);
        Assert.Equal(0, clock.ActiveTimers);
    }

    [Fact]
    public void Reset_AndDispose_ClearWithoutRaisingChanged()
    {
        var clock = new ManualClock();
        var toasts = new NativeToastScheduler(clock);
        var changes = 0;
        toasts.Changed += () => changes++;
        toasts.Offer(NativeMessage.Success("Saved"));
        toasts.Reset();
        Assert.Null(toasts.Current);
        toasts.Offer(NativeMessage.Success("Saved again"));
        toasts.Dispose();
        Assert.Null(toasts.Current);
        Assert.False(toasts.Offer(NativeMessage.Success("After dispose")));
        clock.Advance(TimeSpan.FromSeconds(5));
        Assert.Equal(0, changes);
        Assert.Throws<ArgumentOutOfRangeException>(() => new NativeToastScheduler(clock, TimeSpan.Zero));
        Assert.Equal(TimeSpan.FromSeconds(3), NativeToastScheduler.DefaultDuration);
    }

    /// <summary>A TimeProvider whose one-shot timers fire only when the test advances time.</summary>
    private sealed class ManualClock : TimeProvider
    {
        private readonly List<ManualTimer> _timers = [];
        private DateTimeOffset _now = new(2026, 11, 2, 9, 0, 0, TimeSpan.Zero);

        public int ActiveTimers => _timers.Count(timer => !timer.Disposed && timer.Due is not null);

        public override DateTimeOffset GetUtcNow() => _now;

        public override ITimer CreateTimer(TimerCallback callback, object? state,
            TimeSpan dueTime, TimeSpan period)
        {
            var timer = new ManualTimer(callback, state, _now + dueTime);
            _timers.Add(timer);
            return timer;
        }

        public void Advance(TimeSpan by)
        {
            _now += by;
            foreach (var timer in _timers.ToArray())
                if (!timer.Disposed && timer.Due is { } due && due <= _now)
                {
                    timer.Due = null;
                    timer.Callback(timer.State);
                }
        }

        private sealed class ManualTimer(TimerCallback callback, object? state, DateTimeOffset due) : ITimer
        {
            public TimerCallback Callback { get; } = callback;
            public object? State { get; } = state;
            public DateTimeOffset? Due { get; set; } = due;
            public bool Disposed { get; private set; }

            public bool Change(TimeSpan dueTime, TimeSpan period) => throw new NotSupportedException();

            public void Dispose() => Disposed = true;

            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }
        }
    }
}
