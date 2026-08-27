using SentenceStudio.Contracts.Coach;
using SentenceStudio.WebUI.Services;

namespace SentenceStudio.UI.Tests.Coach;

/// <summary>
/// The announce-or-focus contract as the components consume it.
/// </summary>
/// <remarks>
/// These pin the state side of the shared post-render focus path: a pending focus target is
/// produced only when the outcome policy calls for it, is consumed exactly once, and a polite
/// announcement is cleared after it has been read so a repeat of the same message is still a
/// real DOM mutation (screen readers announce on mutation, not on assignment).
/// </remarks>
public class CoachFocusAndAnnouncementTests
{
    private static (CoachWorkspaceState State, FakeCoachApiClient Client) Create()
    {
        var client = new FakeCoachApiClient();
        return (new CoachWorkspaceState(client), client);
    }

    // ---------------------------------------------------------------- focus is consumed once

    [Fact]
    public async Task ATappedAcceptanceProducesAFocusTargetExactlyOnce()
    {
        var (state, client) = Create();
        client.OnGetSession = id => FakeCoachApiClient.Session(id,
            CoachSessionStatus.SuggestionPending, CoachStateMachineTests.Suggestion());
        await state.OpenAsync(CoachPresentation.Overlay, "session-1");

        await state.AcceptSuggestionAsync();

        state.ConsumePendingFocus().Should().Be(CoachElementIds.Receipt("receipt-1"));
        // A second render must not re-steal focus from wherever the learner moved next.
        state.ConsumePendingFocus().Should().BeNull();
    }

    [Fact]
    public async Task ATappedUndoFocusesTheUndoneReceipt()
    {
        var (state, client) = Create();
        await state.OpenAsync(CoachPresentation.Overlay);

        client.OnSubmitTurn = _ => CoachStateMachineTests.Turn(
            receipt: CoachStateMachineTests.Receipt(CoachRevisionSource.DirectRequest));
        state.Draft = "10 minutes";
        await state.SendDraftAsync();
        state.ConsumePendingFocus();

        await state.UndoAsync();

        state.ConsumePendingFocus().Should().Be(CoachElementIds.Receipt("receipt-2"));
        state.PoliteAnnouncementKey.Should().BeNull("focusing the receipt already reads it");
    }

    [Fact]
    public async Task ATypedRequestNeverMovesFocusOutOfTheComposer()
    {
        var (state, client) = Create();
        await state.OpenAsync(CoachPresentation.Overlay);

        client.OnSubmitTurn = _ => CoachStateMachineTests.Turn(
            receipt: CoachStateMachineTests.Receipt(CoachRevisionSource.DirectRequest));
        state.Draft = "Make it 10 minutes.";
        await state.SendDraftAsync();

        state.ConsumePendingFocus().Should().BeNull();
        state.PoliteAnnouncementKey.Should().Be("Coach_StatusUpdated");
    }

    [Fact]
    public async Task ATypedFailureDoesNotYankFocusToTheAlert()
    {
        var (state, client) = Create();
        await state.OpenAsync(CoachPresentation.Overlay);

        client.OnSubmitTurn = _ => throw new SentenceStudio.Services.Api.CoachApiException(
            System.Net.HttpStatusCode.UnprocessableEntity,
            CoachProblemTypes.PlanValidationFailed, "invalid", "no");

        state.Draft = "10 minutes";
        await state.SendDraftAsync();

        // The composer survives the failure render, so focus stays where the learner is typing.
        state.ConsumePendingFocus().Should().BeNull();
        state.AlertKey.Should().Be("Coach_Failed");
        state.PoliteAnnouncementKey.Should().BeNull("a failure uses role=alert only");
    }

    [Fact]
    public async Task ATappedFailureFocusesTheAlertBecauseTheButtonIsGone()
    {
        var (state, client) = Create();
        client.OnGetSession = id => FakeCoachApiClient.Session(id,
            CoachSessionStatus.SuggestionPending, CoachStateMachineTests.Suggestion());
        await state.OpenAsync(CoachPresentation.Overlay, "session-1");

        client.OnAccept = () => throw new SentenceStudio.Services.Api.CoachApiException(
            System.Net.HttpStatusCode.UnprocessableEntity,
            CoachProblemTypes.PlanValidationFailed, "invalid", "no");

        await state.AcceptSuggestionAsync();

        state.ConsumePendingFocus().Should().Be(CoachElementIds.Alert);
        state.PoliteAnnouncementKey.Should().BeNull();
    }

    // ---------------------------------------------------------------- announcements

    [Fact]
    public async Task ClearingAnAnnouncementEmptiesTheLiveRegion()
    {
        var (state, client) = Create();
        await state.OpenAsync(CoachPresentation.Overlay);

        client.OnSubmitTurn = _ => CoachStateMachineTests.Turn(
            receipt: CoachStateMachineTests.Receipt(CoachRevisionSource.DirectRequest));
        state.Draft = "10 minutes";
        await state.SendDraftAsync();
        state.PoliteAnnouncementKey.Should().NotBeNull();

        state.ClearAnnouncement();

        state.PoliteAnnouncementKey.Should().BeNull();
    }

    [Fact]
    public async Task TheSameAnnouncementTwiceInARowIsStillDelivered()
    {
        // Screen readers announce on DOM mutation. If the region kept its previous text the
        // second identical message would change nothing and be silently dropped, so the
        // component clears between messages and the state must support that round trip.
        var (state, client) = Create();
        await state.OpenAsync(CoachPresentation.Overlay);

        client.OnSubmitTurn = _ => CoachStateMachineTests.Turn(
            receipt: CoachStateMachineTests.Receipt(CoachRevisionSource.DirectRequest));

        state.Draft = "10 minutes";
        await state.SendDraftAsync();
        state.PoliteAnnouncementKey.Should().Be("Coach_StatusUpdated");

        state.ClearAnnouncement();
        state.PoliteAnnouncementKey.Should().BeNull();

        client.OnSubmitTurn = _ => CoachStateMachineTests.Turn(
            receipt: CoachStateMachineTests.Receipt(CoachRevisionSource.DirectRequest, "receipt-3", "rev-3"));
        state.Draft = "15 minutes";
        await state.SendDraftAsync();

        state.PoliteAnnouncementKey.Should().Be("Coach_StatusUpdated",
            "an identical repeat must still be announced");
    }

    [Fact]
    public async Task StartingANewRunClearsTheStaleAnnouncement()
    {
        var (state, client) = Create();
        await state.OpenAsync(CoachPresentation.Overlay);

        client.OnSubmitTurn = _ => CoachStateMachineTests.Turn(
            receipt: CoachStateMachineTests.Receipt(CoachRevisionSource.DirectRequest));
        state.Draft = "10 minutes";
        await state.SendDraftAsync();

        var gate = new TaskCompletionSource();
        client.OnSubmitTurn = _ =>
        {
            gate.Task.GetAwaiter().GetResult();
            return CoachStateMachineTests.Turn();
        };

        state.Draft = "again";
        var run = Task.Run(() => state.SendDraftAsync());
        await WaitForAsync(() => state.State == CoachUiState.Running);

        state.PoliteAnnouncementKey.Should().BeNull("the previous outcome must not linger over a new run");
        state.AlertKey.Should().BeNull();

        gate.SetResult();
        await run;
    }

    [Fact]
    public async Task ClosingClearsAnyPendingFocusTarget()
    {
        var (state, client) = Create();
        client.OnGetSession = id => FakeCoachApiClient.Session(id,
            CoachSessionStatus.SuggestionPending, CoachStateMachineTests.Suggestion());
        await state.OpenAsync(CoachPresentation.Overlay, "session-1");
        await state.AcceptSuggestionAsync();

        state.Close();

        state.ConsumePendingFocus().Should().BeNull(
            "a stale target would steal focus when the workspace is reopened");
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
