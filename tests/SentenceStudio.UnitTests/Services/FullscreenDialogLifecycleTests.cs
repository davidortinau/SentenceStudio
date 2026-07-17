using FluentAssertions;
using SentenceStudio.Services;

namespace SentenceStudio.UnitTests.Services;

public sealed class FullscreenDialogLifecycleTests
{
    [Fact]
    public async Task AutoAdvanceAndDisposeShareOneIdempotentDetachLifecycle()
    {
        var lifecycle = new FullscreenDialogLifecycle();
        lifecycle.Open().Should().BeTrue();
        var detachStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var allowDetach = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var detachCount = 0;

        async Task DetachAsync()
        {
            Interlocked.Increment(ref detachCount);
            detachStarted.TrySetResult();
            await allowDetach.Task;
        }

        var autoAdvanceClose = lifecycle.CloseAsync(DetachAsync);
        await detachStarted.Task;
        var disposeClose = lifecycle.CloseAsync(DetachAsync);
        allowDetach.TrySetResult();
        await Task.WhenAll(autoAdvanceClose, disposeClose);

        detachCount.Should().Be(1);
        lifecycle.IsOpen.Should().BeFalse();
    }

    [Fact]
    public async Task ReopenAfterClose_StartsFreshLifecycle()
    {
        var lifecycle = new FullscreenDialogLifecycle();
        var detachCount = 0;

        lifecycle.Open().Should().BeTrue();
        await lifecycle.CloseAsync(() =>
        {
            detachCount++;
            return Task.CompletedTask;
        });
        lifecycle.Open().Should().BeTrue();
        await lifecycle.CloseAsync(() =>
        {
            detachCount++;
            return Task.CompletedTask;
        });

        detachCount.Should().Be(2);
    }

    [Theory]
    [InlineData(true, true, FullscreenFocusTarget.Thumbnail)]
    [InlineData(true, false, FullscreenFocusTarget.ActivityContent)]
    [InlineData(false, true, FullscreenFocusTarget.None)]
    public void FocusRestorePolicy_AlwaysProvidesLogicalTargetWhenRequested(
        bool restoreRequested,
        bool thumbnailAvailable,
        FullscreenFocusTarget expected)
    {
        FullscreenFocusTargetPolicy.Resolve(restoreRequested, thumbnailAvailable)
            .Should().Be(expected);
    }
}
