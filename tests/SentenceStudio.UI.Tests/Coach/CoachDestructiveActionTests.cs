using SentenceStudio.Contracts.Coach;
using SentenceStudio.Services.Api;
using SentenceStudio.WebUI.Services;

namespace SentenceStudio.UI.Tests.Coach;

/// <summary>
/// Destructive-action safety. Deleting coach history is irreversible, so it must never fire
/// straight from a menu item. Stopping an in-flight model turn is NOT destructive — nothing is
/// written and Today's Plan is untouched — so it stays immediate.
/// </summary>
public class CoachDestructiveActionTests
{
    private static (CoachWorkspaceState State, FakeCoachApiClient Client) Create()
    {
        var client = new FakeCoachApiClient();
        return (new CoachWorkspaceState(client), client);
    }

    [Fact]
    public async Task EndingASessionAsksBeforeItDeletesAnything()
    {
        var (state, client) = Create();
        await state.OpenAsync(CoachPresentation.Overlay);

        state.RequestEndSessionConfirmation();

        state.PendingConfirmation.Should().Be(CoachConfirmation.EndSession);
        client.DeleteCalls.Should().Be(0, "asking must not delete");
        state.SessionId.Should().NotBeNull();
    }

    [Fact]
    public async Task CancellingTheConfirmationDeletesNothing()
    {
        var (state, client) = Create();
        await state.OpenAsync(CoachPresentation.Overlay);

        state.RequestEndSessionConfirmation();
        state.DismissConfirmation();

        state.PendingConfirmation.Should().Be(CoachConfirmation.None);
        client.DeleteCalls.Should().Be(0);
        state.SessionId.Should().NotBeNull();
        state.State.Should().NotBe(CoachUiState.SessionDeleted);
    }

    [Fact]
    public async Task ConfirmingRunsTheDeleteExactlyOnce()
    {
        var (state, client) = Create();
        await state.OpenAsync(CoachPresentation.Overlay);

        state.RequestEndSessionConfirmation();
        await state.ConfirmPendingAsync();

        client.DeleteCalls.Should().Be(1);
        state.State.Should().Be(CoachUiState.SessionDeleted);
        state.PendingConfirmation.Should().Be(CoachConfirmation.None);
    }

    [Fact]
    public async Task ConfirmWithoutAPendingRequestDeletesNothing()
    {
        // Guards a stray call, a double-submit, or a replayed event.
        var (state, client) = Create();
        await state.OpenAsync(CoachPresentation.Overlay);

        await state.ConfirmPendingAsync();

        client.DeleteCalls.Should().Be(0);
        state.SessionId.Should().NotBeNull();
    }

    [Fact]
    public async Task ConfirmingTwiceOnlyDeletesOnce()
    {
        var (state, client) = Create();
        await state.OpenAsync(CoachPresentation.Overlay);

        state.RequestEndSessionConfirmation();
        await state.ConfirmPendingAsync();
        await state.ConfirmPendingAsync();

        client.DeleteCalls.Should().Be(1);
    }

    [Fact]
    public void ConfirmationCannotBeRequestedWithoutASession()
    {
        var (state, _) = Create();

        state.RequestEndSessionConfirmation();

        state.PendingConfirmation.Should().Be(CoachConfirmation.None,
            "there is nothing to delete before a session exists");
    }

    [Fact]
    public async Task ClosingTheWorkspaceDropsAPendingConfirmation()
    {
        // Reopening must not silently resume a half-answered destructive prompt.
        var (state, client) = Create();
        await state.OpenAsync(CoachPresentation.Overlay);
        state.RequestEndSessionConfirmation();

        state.Close();

        state.PendingConfirmation.Should().Be(CoachConfirmation.None);
        client.DeleteCalls.Should().Be(0);
    }

    [Fact]
    public async Task StoppingAModelTurnIsNotGatedBehindAConfirmation()
    {
        // Cancelling a run writes nothing, so gating it would only slow the learner down when
        // they most want out.
        var (state, client) = Create();
        await state.OpenAsync(CoachPresentation.Overlay);

        var gate = new TaskCompletionSource();
        client.OnSubmitTurn = _ =>
        {
            gate.Task.GetAwaiter().GetResult();
            return CoachStateMachineTests.Turn();
        };

        state.Draft = "slow turn";
        var run = Task.Run(() => state.SendDraftAsync());
        await WaitForAsync(() => state.State == CoachUiState.Running);

        await state.CancelRunAsync();

        state.PendingConfirmation.Should().Be(CoachConfirmation.None,
            "stopping a run is not destructive and needs no confirmation");
        state.State.Should().Be(CoachUiState.Ready);
        client.DeleteCalls.Should().Be(0, "stopping a run must never delete the session");
        state.SessionId.Should().NotBeNull();

        gate.SetResult();
        await run;
    }

    [Fact]
    public async Task ResetClearsAPendingConfirmation()
    {
        var (state, _) = Create();
        await state.OpenAsync(CoachPresentation.Overlay);
        state.RequestEndSessionConfirmation();

        state.Reset();

        state.PendingConfirmation.Should().Be(CoachConfirmation.None);
    }

    private static async Task WaitForAsync(Func<bool> condition, int timeoutMs = 2000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
        }

        condition().Should().BeTrue("the expected state should be reached within the timeout");
    }
}
