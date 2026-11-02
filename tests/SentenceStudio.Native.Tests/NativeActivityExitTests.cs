namespace SentenceStudio.Native.Tests;

/// <summary>
/// Forces the Back/Exit ordering behind the recurring "activity re-renders after
/// Back" bug: Exit cancels in-flight work before it can take the presenter gate,
/// so the cancelled continuation can resume and finish first.
/// </summary>
internal static class ExitRace
{
    /// <summary>
    /// Holds the next Exit after its cancels and before its gate wait until
    /// <paramref name="continuation"/> finished (or <paramref name="bound"/>
    /// elapsed). Without the fence the cancelled continuation finishes here,
    /// before Exit publishes; with it the bound elapses because the continuation
    /// waits for Exit to settle.
    /// </summary>
    public static void HoldExitUntil(NativeActivityExit fence, Func<Task> continuation,
        TimeSpan? bound = null)
    {
        fence.AfterCancel = async () =>
        {
            fence.AfterCancel = null;
            await Task.WhenAny(continuation(), Task.Delay(bound ?? TimeSpan.FromSeconds(1)));
        };
    }
}

public sealed class NativeActivityExitTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task EnterAsync_WaitsForAnExitInProgress_ThenReportsItExited()
    {
        var fence = new NativeActivityExit();
        var gate = new SemaphoreSlim(1, 1);
        fence.Begin();
        var enter = fence.EnterAsync(gate);
        await Task.Delay(50);
        Assert.False(enter.IsCompleted);
        Assert.Equal(1, gate.CurrentCount);

        fence.End(exited: true);
        Assert.False(await enter.WaitAsync(Bound));
        Assert.Equal(0, gate.CurrentCount);
        gate.Release();
        Assert.True(fence.HasBegun);
        Assert.True(fence.IsDone);
    }

    [Fact]
    public async Task AnExitThatEndsUnexited_ReleasesWaitersToProceed()
    {
        var fence = new NativeActivityExit();
        var gate = new SemaphoreSlim(1, 1);
        fence.Begin();
        var enter = fence.EnterAsync(gate);
        fence.End(exited: false);
        Assert.True(await enter.WaitAsync(Bound));
        gate.Release();
        Assert.False(fence.HasBegun);
        Assert.True(await fence.EnterAsync(gate).WaitAsync(Bound));
        gate.Release();
    }

    [Fact]
    public async Task Complete_ReleasesEveryWaiter()
    {
        var fence = new NativeActivityExit();
        var gate = new SemaphoreSlim(1, 1);
        fence.Begin();
        var state = fence.StateAsync(gate, () => "exit");
        fence.Complete();
        Assert.Equal("exit", await state.WaitAsync(Bound));
        Assert.Equal(1, gate.CurrentCount);
    }

    [Fact]
    public async Task StateAsync_WithoutAnExit_ReadsUnderTheGate()
    {
        var fence = new NativeActivityExit();
        var gate = new SemaphoreSlim(1, 1);
        await gate.WaitAsync();
        var state = fence.StateAsync(gate, () => gate.CurrentCount);
        await Task.Delay(50);
        Assert.False(state.IsCompleted);
        gate.Release();
        Assert.Equal(0, await state.WaitAsync(Bound));
        Assert.Equal(1, gate.CurrentCount);
    }
}
