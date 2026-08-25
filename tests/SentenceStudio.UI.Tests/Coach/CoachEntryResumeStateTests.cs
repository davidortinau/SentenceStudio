using SentenceStudio.Contracts.Coach;
using SentenceStudio.WebUI.Services;
using SentenceStudio.WebUI.Shared.Sam;

namespace SentenceStudio.UI.Tests.Coach;

/// <summary>
/// Regression cover for the E2E defect found on 2026-08-15: after opening a coach session and
/// closing the overlay, the FAB kept reading "Ask the coach" until a full browser
/// reload, and reopening started a second session instead of resuming the live one.
/// </summary>
/// <remarks>
/// The entry card cached one availability snapshot on first interactive render and never
/// subscribed to state changes, so it neither re-rendered on close nor had a value that could
/// have changed — the snapshot was taken before the session existed. These tests pin the local
/// resume signal the FAB now reads.
/// </remarks>
public class CoachEntryResumeStateTests
{
    private static (CoachWorkspaceState State, FakeCoachApiClient Client) Create()
    {
        var client = new FakeCoachApiClient();
        return (new CoachWorkspaceState(client), client);
    }

    // ---------------------------------------------------------------- resume signal

    [Fact]
    public void NoSessionMeansNothingToResume()
    {
        var (state, _) = Create();

        state.HasResumableSession.Should().BeFalse("the FAB must read 'Ask the coach' first");
    }

    [Fact]
    public async Task AnOpenSessionIsResumable()
    {
        var (state, _) = Create();

        await state.OpenAsync(CoachPresentation.Overlay);

        state.HasResumableSession.Should().BeTrue();
    }

    [Fact]
    public async Task ClosingTheOverlayKeepsTheSessionResumable()
    {
        // The defect: this is the exact moment the entry stayed stuck on "Ask the coach".
        var (state, _) = Create();
        await state.OpenAsync(CoachPresentation.Overlay);
        var sessionId = state.SessionId;

        state.Close();

        state.IsOpen.Should().BeFalse("the overlay is gone");
        state.HasResumableSession.Should().BeTrue("but the session survives, so the entry says Resume");
        state.SessionId.Should().Be(sessionId, "and reopening must land on the SAME session");
    }

    [Fact]
    public async Task ClosingRaisesChangedSoTheDashboardEntryRerenders()
    {
        // Without this notification the FAB never re-renders and the label cannot update,
        // which is why only a full page reload fixed it.
        var (state, _) = Create();
        await state.OpenAsync(CoachPresentation.Overlay);

        var notifications = 0;
        state.Changed += () => notifications++;

        state.Close();

        notifications.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task StartingASessionRaisesChangedSoTheEntryCanUpdateImmediately()
    {
        var (state, _) = Create();

        var notifications = 0;
        state.Changed += () => notifications++;

        await state.OpenAsync(CoachPresentation.Overlay);

        notifications.Should().BeGreaterThan(0);
        state.HasResumableSession.Should().BeTrue();
    }

    // ---------------------------------------------------------------- terminal states reset it

    [Fact]
    public async Task DeletingTheSessionReturnsTheEntryToAskTheCoach()
    {
        var (state, _) = Create();
        await state.OpenAsync(CoachPresentation.Overlay);

        await state.DeleteSessionAsync();

        state.HasResumableSession.Should().BeFalse();
        state.State.Should().Be(CoachUiState.SessionDeleted);
    }

    [Fact]
    public async Task AnExpiredSessionIsNotOfferedForResume()
    {
        var (state, client) = Create();
        await state.OpenAsync(CoachPresentation.Overlay);

        client.OnSubmitTurn = _ => throw new SentenceStudio.Services.Api.CoachApiException(
            System.Net.HttpStatusCode.NotFound, CoachProblemTypes.SessionExpired, "expired", null);

        state.Draft = "10 minutes";
        await state.SendDraftAsync();

        state.State.Should().Be(CoachUiState.Expired);
        state.HasResumableSession.Should().BeFalse(
            "resuming an expired session would fail the moment the learner clicked");
    }

    [Fact]
    public void ResetReturnsTheEntryToAskTheCoach()
    {
        var (state, _) = Create();

        state.Reset();

        state.HasResumableSession.Should().BeFalse();
    }

    // ---------------------------------------------------------------- focus restore

    [Fact]
    public async Task ClosingHandsFocusBackToTheInvokingDashboardButton()
    {
        var (state, _) = Create();
        await state.OpenAsync(CoachPresentation.Overlay, invokerElementId: SamElementIds.Fab);

        state.Close();

        state.ConsumePendingFocus().Should().Be(SamElementIds.Fab,
            "focus must return to the control that opened the workspace, not fall to <body>");
    }

    [Fact]
    public async Task TheCloseFocusTargetIsConsumedExactlyOnce()
    {
        var (state, _) = Create();
        await state.OpenAsync(CoachPresentation.Overlay, invokerElementId: SamElementIds.Fab);

        state.Close();

        state.ConsumePendingFocus().Should().Be(SamElementIds.Fab);
        state.ConsumePendingFocus().Should().BeNull("a stale target would steal focus later");
    }

    [Fact]
    public async Task ClosingWithoutAKnownInvokerRequestsNoFocusMove()
    {
        // Deep link or refresh: there is no invoking control to return to, and guessing one
        // would move focus somewhere the learner never was.
        var (state, _) = Create();
        await state.OpenAsync(CoachPresentation.Overlay);

        state.Close();

        state.ConsumePendingFocus().Should().BeNull();
    }

    [Fact]
    public async Task TheInvokerSurvivesAReopenSoASecondCloseStillRestoresFocus()
    {
        var (state, _) = Create();
        await state.OpenAsync(CoachPresentation.Overlay, invokerElementId: SamElementIds.Fab);
        state.Close();
        state.ConsumePendingFocus();

        // Reopen from the entry again, this time without re-supplying the invoker.
        await state.OpenAsync(CoachPresentation.Overlay, state.SessionId);
        state.Close();

        state.ConsumePendingFocus().Should().Be(SamElementIds.Fab);
    }

    // ---------------------------------------------------------------- reopen resumes

    [Fact]
    public async Task ReopeningAfterCloseResumesRatherThanStartingASecondSession()
    {
        var (state, client) = Create();
        client.OnGetSession = id => FakeCoachApiClient.Session(id);

        await state.OpenAsync(CoachPresentation.Overlay);
        var sessionId = state.SessionId;
        client.StartSessionCalls.Should().Be(1);

        state.Close();
        await state.OpenAsync(CoachPresentation.Overlay, sessionId);

        client.StartSessionCalls.Should().Be(1, "the live session must be resumed, not replaced");
        state.SessionId.Should().Be(sessionId);
        state.IsOpen.Should().BeTrue();
    }
}
