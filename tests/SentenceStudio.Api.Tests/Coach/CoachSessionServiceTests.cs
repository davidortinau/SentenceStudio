using SentenceStudio.Api.Coach.Agents;
using SentenceStudio.Api.Coach.Application;
using SentenceStudio.Api.Coach.Runtime;
using SentenceStudio.Contracts.Coach;
using SentenceStudio.Contracts.Coach.Intent;

namespace SentenceStudio.Api.Tests.Coach;

/// <summary>
/// The application state machine. These tests are the boundary guarantees: every path that
/// can write Today's Plan, and every path that must not.
/// </summary>
public class CoachSessionServiceTests
{
    // ------------------------------------------------------------------ availability

    [Fact]
    public async Task Availability_FeatureOff_IsUnavailableAndReadsNothing()
    {
        using var harness = new CoachApplicationHarness(new CoachOptions { Enabled = false });

        var result = await harness.Service.GetAvailabilityAsync();

        result.Status.Should().Be(CoachOperationStatus.Unavailable);
        harness.Coach.RunCount.Should().Be(0);
    }

    [Fact]
    public async Task Availability_LearnerOutsideCohort_IsUnavailable()
    {
        using var harness = new CoachApplicationHarness(new CoachOptions
        {
            Enabled = true,
            AllowedUserProfileIds = { "someone-else" }
        });

        var result = await harness.Service.GetAvailabilityAsync();

        result.Status.Should().Be(CoachOperationStatus.Unavailable);
    }

    [Fact]
    public async Task Availability_NoPlanForToday_StillOpensForLanguageQuestions()
    {
        using var harness = new CoachApplicationHarness();
        harness.PlanService.SetItems(Array.Empty<SentenceStudio.Services.Plans.PlanSnapshotItem>());

        var result = await harness.Service.GetAvailabilityAsync();

        // The coach answers language questions with or without a plan. Only the plan-editing
        // half is unavailable, and opening the coach never creates a plan.
        result.IsOk.Should().BeTrue();
        result.Value!.IsAvailable.Should().BeTrue();
        result.Value.CanEditPlan.Should().BeFalse();
    }

    [Fact]
    public async Task Availability_MergesCohortBudgetPlanAndResumableSession()
    {
        using var harness = new CoachApplicationHarness();

        var first = await harness.Service.GetAvailabilityAsync();
        first.Value!.State.Should().Be(CoachAvailabilityState.Available);
        first.Value.RunsRemainingToday.Should().Be(10);
        first.Value.ActiveSessionId.Should().BeNull();

        var sessionId = await harness.StartSessionAsync();

        var second = await harness.Service.GetAvailabilityAsync();
        second.Value!.State.Should().Be(CoachAvailabilityState.ResumeAvailable);
        second.Value.ActiveSessionId.Should().Be(sessionId);
        second.Value.EntryPointLabel.Should().Be("Resume coach");
    }

    [Fact]
    public async Task Availability_BudgetExhausted_ReportsLimitReached()
    {
        using var harness = new CoachApplicationHarness(new CoachOptions
        {
            Enabled = true,
            AllowedUserProfileIds = { CoachApplicationHarness.OwnerUserId },
            MaxRunsPerDay = 1,
            MaxRunsPerWeek = 1
        });

        var lease = await harness.Budget.TryStartRunAsync(
            CoachApplicationHarness.OwnerUserId, harness.DateContext.UserLocalDate);
        await lease.Lease!.DisposeAsync();

        var result = await harness.Service.GetAvailabilityAsync();

        result.Value!.State.Should().Be(CoachAvailabilityState.LimitReached);
        result.Value.IsAvailable.Should().BeFalse();
    }

    // ------------------------------------------------------------------ sessions

    [Fact]
    public async Task StartSession_ResumesTheActiveSessionInsteadOfCreatingASecond()
    {
        using var harness = new CoachApplicationHarness();

        var first = await harness.Service.StartSessionAsync(new StartCoachSessionRequest { Resume = true });
        var second = await harness.Service.StartSessionAsync(new StartCoachSessionRequest { Resume = true });

        second.Value!.SessionId.Should().Be(first.Value!.SessionId);
    }

    [Fact]
    public async Task GetSession_OtherLearnersSession_Returns404NotForbidden()
    {
        using var harness = new CoachApplicationHarness();
        var sessionId = await harness.StartSessionAsync();

        harness.UserScope.Current = CoachApplicationHarness.OtherUserId;
        var result = await harness.Service.GetSessionAsync(sessionId);

        // Indistinguishable from "never existed": the coach never confirms another
        // learner's session is there.
        result.Status.Should().Be(CoachOperationStatus.SessionNotFound);
    }

    [Fact]
    public async Task GetSession_Expired_ReportsExpiredNotFound()
    {
        using var harness = new CoachApplicationHarness();
        var sessionId = await harness.StartSessionAsync();

        harness.Db.CoachSessions.Single().ExpiresAt = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        await harness.Db.SaveChangesAsync();

        var result = await harness.Service.GetSessionAsync(sessionId);

        result.Status.Should().Be(CoachOperationStatus.SessionExpired);
    }

    [Fact]
    public async Task DeleteSession_RemovesConversationButKeepsRevisionAudit()
    {
        using var harness = new CoachApplicationHarness();
        var sessionId = await harness.StartSessionAsync();
        await ApplyDirectChangeAsync(harness, sessionId);

        var deleted = await harness.Service.DeleteSessionAsync(sessionId);

        deleted.IsOk.Should().BeTrue();
        harness.Db.CoachSessions.Should().BeEmpty();
        harness.Db.CoachPlanRevisions.Should().NotBeEmpty("deleting coach history never undoes Today's Plan");
    }

    [Fact]
    public async Task DeleteSession_OtherLearnersSession_Returns404()
    {
        using var harness = new CoachApplicationHarness();
        var sessionId = await harness.StartSessionAsync();

        harness.UserScope.Current = CoachApplicationHarness.OtherUserId;
        var result = await harness.Service.DeleteSessionAsync(sessionId);

        result.Status.Should().Be(CoachOperationStatus.SessionNotFound);
        harness.Db.CoachSessions.Should().HaveCount(1);
    }

    // ------------------------------------------------------------------ direct change

    [Fact]
    public async Task DirectConstraintChange_AppliesImmediatelyAndRecordsARevision()
    {
        using var harness = new CoachApplicationHarness();
        var sessionId = await harness.StartSessionAsync();

        var result = await ApplyDirectChangeAsync(harness, sessionId);

        result.IsOk.Should().BeTrue();
        result.Value!.ChangeReceipt.Should().NotBeNull();
        result.Value.ChangeReceipt!.CanUndo.Should().BeTrue();
        harness.PlanService.ApplyCallCount.Should().Be(1);

        var revision = harness.Db.CoachPlanRevisions.Single();
        revision.Source.Should().Be(CoachRevisionSource.DirectRequest);
        revision.BeforePlanVersion.Should().NotBe(revision.AfterPlanVersion);
        revision.BeforePlanHash.Should().NotBeNullOrEmpty();
        revision.AfterPlanHash.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task DirectConstraintChange_PreservesCompletedAndStartedWork()
    {
        using var harness = new CoachApplicationHarness();
        var sessionId = await harness.StartSessionAsync();

        var result = await ApplyDirectChangeAsync(harness, sessionId);

        var receipt = result.Value!.ChangeReceipt!;
        receipt.PreservedCompletedItemCount.Should().Be(1);
        receipt.PreservedInProgressItemCount.Should().Be(1);
        receipt.PreservedMinutesSpent.Should().Be(8, "5 completed + 3 in progress minutes must survive");

        harness.PlanService.Current.Items.Should().Contain(i => i.PlanItemId == "done-1" && i.IsCompleted);
        harness.PlanService.Current.Items.Should().Contain(i => i.PlanItemId == "started-1" && i.MinutesSpent == 3);
    }

    [Fact]
    public async Task DirectConstraintChange_OutOfRangeMinutes_IsRejectedWithNoWrite()
    {
        using var harness = new CoachApplicationHarness();
        var sessionId = await harness.StartSessionAsync();

        harness.Coach.NextResult = Completed(new CoachTurnIntent
        {
            Kind = CoachIntentKind.DirectConstraintChange,
            ConstraintDelta = new CoachConstraintDeltaIntent { AvailableMinutes = 900 },
            CoachMessage = "Set to 900 minutes."
        });

        var result = await SubmitTextAsync(harness, sessionId, "make it 900 minutes");

        result.Status.Should().Be(CoachOperationStatus.InvalidConstraint);
        harness.PlanService.ApplyCallCount.Should().Be(0);
    }

    [Fact]
    public async Task StructuredConstraintAction_AppliesWithoutCallingTheModel()
    {
        using var harness = new CoachApplicationHarness();
        var sessionId = await harness.StartSessionAsync();

        var result = await harness.Service.SubmitTurnAsync(sessionId, new CoachTurnRequest
        {
            InputKind = CoachTurnInputKind.ConstraintAction,
            ConstraintAction = new CoachConstraintDeltaDto { AvailableMinutes = 10 }
        });

        result.IsOk.Should().BeTrue();
        harness.Coach.RunCount.Should().Be(0, "a tapped UI value needs no model");
        harness.PlanService.ApplyCallCount.Should().Be(1);
    }

    [Fact]
    public async Task StalePlanVersion_IsRejectedAsAConflictWithNoWrite()
    {
        using var harness = new CoachApplicationHarness();
        var sessionId = await harness.StartSessionAsync();

        harness.Coach.NextResult = Completed(DirectChangeIntent());

        var result = await harness.Service.SubmitTurnAsync(sessionId, new CoachTurnRequest
        {
            InputKind = CoachTurnInputKind.Text,
            Text = "make it 10 minutes",
            ExpectedPlanVersion = "v1:not-the-current-plan"
        });

        result.Status.Should().Be(CoachOperationStatus.PlanChangedElsewhere);
        harness.Db.CoachPlanRevisions.Should().BeEmpty();
    }

    // ------------------------------------------------------------------ suggestions

    [Fact]
    public async Task Suggestion_PreviewsOnlyAndWritesNothing()
    {
        using var harness = new CoachApplicationHarness();
        var sessionId = await harness.StartSessionAsync();

        var result = await SuggestAsync(harness, sessionId);

        result.IsOk.Should().BeTrue();
        result.Value!.PendingSuggestion.Should().NotBeNull();
        result.Value.PendingSuggestion!.Preview.IsPreview.Should().BeTrue();
        result.Value.ChangeReceipt.Should().BeNull();
        harness.PlanService.ApplyCallCount.Should().Be(0);
        harness.Db.CoachPlanRevisions.Should().BeEmpty();

        harness.Db.CoachSessions.Single().PendingSuggestionId.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task Suggestion_SecondProposalWhileOneIsOpenIsIgnored()
    {
        using var harness = new CoachApplicationHarness();
        var sessionId = await harness.StartSessionAsync();

        var first = await SuggestAsync(harness, sessionId);
        var second = await SuggestAsync(harness, sessionId);

        second.Value!.PendingSuggestion!.SuggestionId
            .Should().Be(first.Value!.PendingSuggestion!.SuggestionId);
    }

    [Fact]
    public async Task TappedAccept_AppliesTheExactStoredDelta()
    {
        using var harness = new CoachApplicationHarness();
        var sessionId = await harness.StartSessionAsync();
        var suggestion = (await SuggestAsync(harness, sessionId)).Value!.PendingSuggestion!;

        var result = await harness.Service.AcceptSuggestionAsync(
            sessionId, suggestion.SuggestionId, new CoachSuggestionDecisionRequest());

        result.IsOk.Should().BeTrue();
        result.Value!.ChangeReceipt.Should().NotBeNull();
        harness.PlanService.LastAppliedConstraints!.AvailableMinutes.Should().Be(12);
        harness.Db.CoachPlanRevisions.Single().Source.Should().Be(CoachRevisionSource.AcceptedSuggestion);
        harness.Db.CoachSessions.Single().PendingSuggestionId.Should().BeNull();
    }

    [Fact]
    public async Task TappedAccept_UnknownSuggestion_Returns404WithNoWrite()
    {
        using var harness = new CoachApplicationHarness();
        var sessionId = await harness.StartSessionAsync();

        var result = await harness.Service.AcceptSuggestionAsync(
            sessionId, "not-a-suggestion", new CoachSuggestionDecisionRequest());

        result.Status.Should().Be(CoachOperationStatus.SuggestionNotFound);
        harness.PlanService.ApplyCallCount.Should().Be(0);
    }

    [Fact]
    public async Task TappedReject_ClearsTheSuggestionAndWritesNothing()
    {
        using var harness = new CoachApplicationHarness();
        var sessionId = await harness.StartSessionAsync();
        var suggestion = (await SuggestAsync(harness, sessionId)).Value!.PendingSuggestion!;

        var result = await harness.Service.RejectSuggestionAsync(
            sessionId, suggestion.SuggestionId, new CoachSuggestionDecisionRequest());

        result.IsOk.Should().BeTrue();
        harness.PlanService.ApplyCallCount.Should().Be(0);
        harness.Db.CoachSessions.Single().PendingSuggestionId.Should().BeNull();
    }

    // ------------------------------------------------------------------ typed acceptance

    [Theory]
    [InlineData("yes")]
    [InlineData("Yes, add that")]
    [InlineData("네")]
    [InlineData("좋아요")]
    public async Task TypedAcceptance_ClearAffirmative_Applies(string text)
    {
        using var harness = new CoachApplicationHarness();
        var sessionId = await harness.StartSessionAsync();
        var suggestion = (await SuggestAsync(harness, sessionId)).Value!.PendingSuggestion!;

        harness.Coach.NextResult = Completed(new CoachTurnIntent
        {
            Kind = CoachIntentKind.AcceptPendingSuggestion,
            PendingSuggestionId = suggestion.SuggestionId,
            AcceptanceState = CoachAcceptanceState.Accepted,
            CoachMessage = "Updated Today's Plan."
        });

        var result = await SubmitTextAsync(harness, sessionId, text);

        result.IsOk.Should().BeTrue();
        result.Value!.ChangeReceipt.Should().NotBeNull();
        harness.PlanService.ApplyCallCount.Should().Be(1);
    }

    [Theory]
    [InlineData("maybe")]
    [InlineData("I guess so")]
    [InlineData("yes, but not the speaking one")]
    [InlineData("글쎄요")]
    [InlineData("hmm")]
    public async Task TypedAcceptance_AmbiguousText_AsksForClarificationAndWritesNothing(string text)
    {
        using var harness = new CoachApplicationHarness();
        var sessionId = await harness.StartSessionAsync();
        var suggestion = (await SuggestAsync(harness, sessionId)).Value!.PendingSuggestion!;

        // The model classifies it as an acceptance. That is a hint, not authorisation.
        harness.Coach.NextResult = Completed(new CoachTurnIntent
        {
            Kind = CoachIntentKind.AcceptPendingSuggestion,
            PendingSuggestionId = suggestion.SuggestionId,
            AcceptanceState = CoachAcceptanceState.Accepted,
            CoachMessage = "Updated Today's Plan."
        });

        var result = await SubmitTextAsync(harness, sessionId, text);

        result.IsOk.Should().BeTrue();
        result.Value!.Status.Should().Be(CoachTurnStatus.Incomplete);
        result.Value.StopReason.Should().Be(CoachStopReason.ClarificationRequested);
        result.Value.ClarifyingQuestion.Should().NotBeNullOrEmpty();
        result.Value.PendingSuggestion.Should().NotBeNull("an unclear answer keeps the suggestion open");
        harness.PlanService.ApplyCallCount.Should().Be(0);
        harness.Db.CoachPlanRevisions.Should().BeEmpty();
    }

    [Fact]
    public async Task TypedAcceptance_NamingAStaleSuggestion_DoesNotWrite()
    {
        using var harness = new CoachApplicationHarness();
        var sessionId = await harness.StartSessionAsync();
        await SuggestAsync(harness, sessionId);

        harness.Coach.NextResult = Completed(new CoachTurnIntent
        {
            Kind = CoachIntentKind.AcceptPendingSuggestion,
            PendingSuggestionId = "a-suggestion-that-is-not-open",
            AcceptanceState = CoachAcceptanceState.Accepted,
            CoachMessage = "Updated Today's Plan."
        });

        var result = await SubmitTextAsync(harness, sessionId, "yes");

        result.Value!.StopReason.Should().Be(CoachStopReason.ClarificationRequested);
        harness.PlanService.ApplyCallCount.Should().Be(0);
    }

    [Fact]
    public async Task TypedAcceptance_WithNoOpenSuggestion_DoesNotWrite()
    {
        using var harness = new CoachApplicationHarness();
        var sessionId = await harness.StartSessionAsync();

        harness.Coach.NextResult = Completed(new CoachTurnIntent
        {
            Kind = CoachIntentKind.AcceptPendingSuggestion,
            PendingSuggestionId = "invented",
            AcceptanceState = CoachAcceptanceState.Accepted,
            CoachMessage = "Updated Today's Plan."
        });

        var result = await SubmitTextAsync(harness, sessionId, "yes");

        harness.PlanService.ApplyCallCount.Should().Be(0);
        result.Value!.StopReason.Should().Be(CoachStopReason.ClarificationRequested);
    }

    [Theory]
    [InlineData("no")]
    [InlineData("not now")]
    [InlineData("아니요")]
    public async Task TypedRejection_ClearNegative_ClearsSuggestionWithNoWrite(string text)
    {
        using var harness = new CoachApplicationHarness();
        var sessionId = await harness.StartSessionAsync();
        var suggestion = (await SuggestAsync(harness, sessionId)).Value!.PendingSuggestion!;

        harness.Coach.NextResult = Completed(new CoachTurnIntent
        {
            Kind = CoachIntentKind.RejectPendingSuggestion,
            PendingSuggestionId = suggestion.SuggestionId,
            AcceptanceState = CoachAcceptanceState.Rejected,
            CoachMessage = "Today's Plan is unchanged."
        });

        var result = await SubmitTextAsync(harness, sessionId, text);

        result.IsOk.Should().BeTrue();
        harness.PlanService.ApplyCallCount.Should().Be(0);
        harness.Db.CoachSessions.Single().PendingSuggestionId.Should().BeNull();
    }

    // ------------------------------------------------------------------ clarification cap

    [Fact]
    public async Task Clarification_StopsAfterTheConfiguredCap()
    {
        using var harness = new CoachApplicationHarness();
        var sessionId = await harness.StartSessionAsync();

        harness.Coach.NextResult = Completed(new CoachTurnIntent
        {
            Kind = CoachIntentKind.AskClarification,
            ClarifyingQuestion = "How many minutes do you have?",
            CoachMessage = "I need one detail."
        });

        var first = await SubmitTextAsync(harness, sessionId, "not much time");
        var second = await SubmitTextAsync(harness, sessionId, "some time");
        var third = await SubmitTextAsync(harness, sessionId, "a bit");

        first.Value!.ClarifyingQuestion.Should().NotBeNullOrEmpty();
        second.Value!.ClarifyingQuestion.Should().NotBeNullOrEmpty();
        third.Value!.ClarifyingQuestion.Should().BeNull("the cap is two clarifications per session");
        harness.PlanService.ApplyCallCount.Should().Be(0);
    }

    // ------------------------------------------------------------------ limits and failures

    [Fact]
    public async Task TurnText_OverTheLimit_IsRejectedBeforeTheModelRuns()
    {
        using var harness = new CoachApplicationHarness();
        var sessionId = await harness.StartSessionAsync();

        var result = await SubmitTextAsync(harness, sessionId, new string('a', 501));

        result.Status.Should().Be(CoachOperationStatus.InvalidInput);
        harness.Coach.RunCount.Should().Be(0);
    }

    [Fact]
    public async Task Turn_WithNoChatClient_Returns503AndNeverWrites()
    {
        using var harness = new CoachApplicationHarness();
        var sessionId = await harness.StartSessionAsync();
        harness.AgentFactory.IsModelAvailable = false;

        var result = await SubmitTextAsync(harness, sessionId, "make it 10 minutes");

        result.Status.Should().Be(CoachOperationStatus.ModelUnavailable);
        harness.Coach.RunCount.Should().Be(0);
        harness.PlanService.ApplyCallCount.Should().Be(0);
    }

    [Fact]
    public async Task Turn_OverTheRunBudget_IsRateLimited()
    {
        using var harness = new CoachApplicationHarness(new CoachOptions
        {
            Enabled = true,
            AllowedUserProfileIds = { CoachApplicationHarness.OwnerUserId },
            MaxRunsPerDay = 1,
            MaxRunsPerWeek = 1
        });

        var sessionId = await harness.StartSessionAsync();
        await SubmitTextAsync(harness, sessionId, "hello");

        var result = await SubmitTextAsync(harness, sessionId, "hello again");

        result.Status.Should().Be(CoachOperationStatus.RateLimited);
    }

    [Fact]
    public async Task Turn_ThatTimesOut_ReportsIncompleteWithNoWrite()
    {
        using var harness = new CoachApplicationHarness();
        var sessionId = await harness.StartSessionAsync();

        harness.Coach.NextResult = CoachAgentTurnResult.Failure(CoachAgentOutcome.Timeout, "timed out");

        var result = await SubmitTextAsync(harness, sessionId, "make it 10 minutes");

        result.IsOk.Should().BeTrue();
        result.Value!.Status.Should().Be(CoachTurnStatus.Incomplete);
        result.Value.StopReason.Should().Be(CoachStopReason.Timeout);
        harness.PlanService.ApplyCallCount.Should().Be(0);
    }

    [Fact]
    public async Task Turn_WithAnIntentThatFailsValidation_IsRejectedWithNoWrite()
    {
        using var harness = new CoachApplicationHarness();
        var sessionId = await harness.StartSessionAsync();

        // A direct change with no delta cannot be acted on.
        harness.Coach.NextResult = Completed(new CoachTurnIntent
        {
            Kind = CoachIntentKind.DirectConstraintChange,
            CoachMessage = "Changing the plan."
        });

        var result = await SubmitTextAsync(harness, sessionId, "change something");

        result.Value!.Status.Should().Be(CoachTurnStatus.Rejected);
        result.Value.StopReason.Should().Be(CoachStopReason.ValidationFailed);
        harness.PlanService.ApplyCallCount.Should().Be(0);
    }

    [Fact]
    public async Task OffTopicTurn_WritesNothing()
    {
        using var harness = new CoachApplicationHarness();
        var sessionId = await harness.StartSessionAsync();

        harness.Coach.NextResult = Completed(new CoachTurnIntent
        {
            Kind = CoachIntentKind.OffTopic,
            CoachMessage = "I can only adjust today's study settings."
        });

        var result = await SubmitTextAsync(harness, sessionId, "how do I say hello?");

        result.IsOk.Should().BeTrue();
        harness.PlanService.ApplyCallCount.Should().Be(0);
        harness.Db.CoachPlanRevisions.Should().BeEmpty();
    }

    // ------------------------------------------------------------------ idempotency and cancel

    [Fact]
    public async Task RepeatingAClientTurnId_ReplaysTheAnswerWithoutASecondWrite()
    {
        using var harness = new CoachApplicationHarness();
        var sessionId = await harness.StartSessionAsync();

        harness.Coach.NextResult = Completed(DirectChangeIntent());

        var request = new CoachTurnRequest
        {
            InputKind = CoachTurnInputKind.Text,
            Text = "make it 10 minutes",
            ClientTurnId = "turn-1"
        };

        var first = await harness.Service.SubmitTurnAsync(sessionId, request);
        var second = await harness.Service.SubmitTurnAsync(sessionId, request);

        second.Value!.TurnId.Should().Be(first.Value!.TurnId);
        harness.PlanService.ApplyCallCount.Should().Be(1);
        harness.Db.CoachPlanRevisions.Should().HaveCount(1);
    }

    [Fact]
    public async Task Cancel_StopsTheInFlightRun()
    {
        using var harness = new CoachApplicationHarness();
        var sessionId = await harness.StartSessionAsync();

        using var registration = harness.Runs.Register(
            CoachApplicationHarness.OwnerUserId, sessionId, CancellationToken.None);

        var result = await harness.Service.CancelAsync(sessionId);

        result.IsOk.Should().BeTrue();
        result.Value.Should().BeTrue();
        registration.IsCancelled.Should().BeTrue();
    }

    [Fact]
    public async Task Cancel_CannotReachAnotherLearnersRun()
    {
        using var harness = new CoachApplicationHarness();
        var sessionId = await harness.StartSessionAsync();

        using var registration = harness.Runs.Register(
            CoachApplicationHarness.OwnerUserId, sessionId, CancellationToken.None);

        harness.UserScope.Current = CoachApplicationHarness.OtherUserId;
        var result = await harness.Service.CancelAsync(sessionId);

        result.Value.Should().BeFalse();
        registration.IsCancelled.Should().BeFalse();
    }

    // ------------------------------------------------------------------ undo

    [Fact]
    public async Task Undo_RestoresTheRemainderAndMarksTheRevisionUndone()
    {
        using var harness = new CoachApplicationHarness();
        var sessionId = await harness.StartSessionAsync();
        await ApplyDirectChangeAsync(harness, sessionId);

        harness.PlanService.Current.Items.Should().Contain(i => i.PlanItemId == "fresh-2");

        var result = await harness.Service.UndoAsync(sessionId, new CoachUndoRequest());

        result.IsOk.Should().BeTrue();
        harness.PlanService.UndoCallCount.Should().Be(1);
        harness.PlanService.Current.Items.Should().Contain(i => i.PlanItemId == "fresh-1");
        harness.PlanService.Current.Items.Should().Contain(i => i.PlanItemId == "done-1" && i.IsCompleted);
        harness.PlanService.Current.Items.Should().Contain(i => i.PlanItemId == "started-1" && i.MinutesSpent == 3);

        var revisions = harness.Db.CoachPlanRevisions.OrderBy(r => r.RevisionNumber).ToList();
        revisions.Should().HaveCount(2);
        revisions[0].IsUndone.Should().BeTrue();
        revisions[0].UndoneByRevisionId.Should().Be(revisions[1].Id);
        revisions[1].Source.Should().Be(CoachRevisionSource.Undo);
    }

    [Fact]
    public async Task Undo_WithNothingToUndo_Returns422()
    {
        using var harness = new CoachApplicationHarness();
        var sessionId = await harness.StartSessionAsync();

        var result = await harness.Service.UndoAsync(sessionId, new CoachUndoRequest());

        result.Status.Should().Be(CoachOperationStatus.NothingToUndo);
    }

    [Fact]
    public async Task Undo_HasNoRedo()
    {
        using var harness = new CoachApplicationHarness();
        var sessionId = await harness.StartSessionAsync();
        await ApplyDirectChangeAsync(harness, sessionId);

        (await harness.Service.UndoAsync(sessionId, new CoachUndoRequest())).IsOk.Should().BeTrue();
        var second = await harness.Service.UndoAsync(sessionId, new CoachUndoRequest());

        second.Status.Should().Be(CoachOperationStatus.NothingToUndo);
    }

    [Fact]
    public async Task Undo_OtherLearnersSession_Returns404()
    {
        using var harness = new CoachApplicationHarness();
        var sessionId = await harness.StartSessionAsync();
        await ApplyDirectChangeAsync(harness, sessionId);

        harness.UserScope.Current = CoachApplicationHarness.OtherUserId;
        var result = await harness.Service.UndoAsync(sessionId, new CoachUndoRequest());

        result.Status.Should().Be(CoachOperationStatus.SessionNotFound);
        harness.PlanService.UndoCallCount.Should().Be(0);
    }

    // ------------------------------------------------------------------ privacy

    [Fact]
    public async Task ConversationState_RoundTripsThroughTheEncryptedStore()
    {
        using var harness = new CoachApplicationHarness();
        var sessionId = await harness.StartSessionAsync();

        harness.Coach.NextResult = new CoachAgentTurnResult
        {
            Outcome = CoachAgentOutcome.Completed,
            Intent = new CoachTurnIntent { Kind = CoachIntentKind.NoChange, CoachMessage = "ok" },
            AgentSessionJson = """{"turn":1}"""
        };
        await SubmitTextAsync(harness, sessionId, "hello");

        harness.Coach.NextResult = new CoachAgentTurnResult
        {
            Outcome = CoachAgentOutcome.Completed,
            Intent = new CoachTurnIntent { Kind = CoachIntentKind.NoChange, CoachMessage = "ok" },
            AgentSessionJson = """{"turn":2}"""
        };
        await SubmitTextAsync(harness, sessionId, "hello again");

        // The second turn resumed the conversation the first turn produced.
        harness.Coach.LastRequest!.AgentSessionJson.Should().Be("""{"turn":1}""");

        // And what is on disk is ciphertext, not that JSON.
        harness.Db.CoachSessions.Single().ProtectedAgentSession.Should().NotContain("turn");
    }

    [Fact]
    public async Task RevisionAudit_NeverStoresLearnerText()
    {
        const string sentinel = "SENTINEL_TEXT_c41f";

        using var harness = new CoachApplicationHarness();
        var sessionId = await harness.StartSessionAsync();

        harness.Coach.NextResult = new CoachAgentTurnResult
        {
            Outcome = CoachAgentOutcome.Completed,
            Intent = DirectChangeIntent(),
            AgentSessionJson = $"{{\"learner\":\"{sentinel}\"}}"
        };

        await harness.Service.SubmitTurnAsync(sessionId, new CoachTurnRequest
        {
            InputKind = CoachTurnInputKind.Text,
            // A real plan command, so the turn still applies: a bare sentinel names no
            // constraint and is now offered rather than applied.
            Text = $"make it 10 minutes {sentinel}"
        });

        var revision = harness.Db.CoachPlanRevisions.Single();
        var audit = string.Join(
            '|',
            revision.AcceptedConstraintDeltaJson,
            revision.BeforePlanSnapshotJson,
            revision.AfterPlanSnapshotJson);

        audit.Should().NotContain(sentinel);

        var session = harness.Db.CoachSessions.Single();
        session.ProtectedAgentSession.Should().NotBeNullOrEmpty();
        session.ProtectedAgentSession.Should().NotContain(sentinel, "the session blob is encrypted at rest");
    }

    // ------------------------------------------------------------------ helpers

    private static CoachAgentTurnResult Completed(CoachTurnIntent intent) =>
        new() { Outcome = CoachAgentOutcome.Completed, Intent = intent };

    private static CoachTurnIntent DirectChangeIntent() => new()
    {
        Kind = CoachIntentKind.DirectConstraintChange,
        ConstraintDelta = new CoachConstraintDeltaIntent { AvailableMinutes = 10, AudioAllowed = false },
        CoachMessage = "Today's Plan now fits 10 minutes and uses no audio."
    };

    private static Task<CoachOperationResult<CoachTurnResponse>> SubmitTextAsync(
        CoachApplicationHarness harness, string sessionId, string text) =>
        harness.Service.SubmitTurnAsync(sessionId, new CoachTurnRequest
        {
            InputKind = CoachTurnInputKind.Text,
            Text = text
        });

    private static Task<CoachOperationResult<CoachTurnResponse>> ApplyDirectChangeAsync(
        CoachApplicationHarness harness, string sessionId)
    {
        harness.Coach.NextResult = Completed(DirectChangeIntent());
        return SubmitTextAsync(harness, sessionId, "make it 10 minutes and no audio");
    }

    private static async Task<CoachOperationResult<CoachTurnResponse>> SuggestAsync(
        CoachApplicationHarness harness, string sessionId)
    {
        // The claim below is now checked against what the turn read, so the read has to happen.
        harness.SeedPracticeBalanceRead();

        harness.Coach.NextResult = Completed(new CoachTurnIntent
        {
            Kind = CoachIntentKind.SuggestConstraintChange,
            ConstraintDelta = new CoachConstraintDeltaIntent { AvailableMinutes = 12 },
            CoachMessage = "Would you like a shorter session today?",
            EvidenceReferences =
            [
                new CoachEvidenceReferenceIntent { Kind = CoachEvidenceKind.PracticeBalance, WindowDays = 14 }
            ]
        });

        return await SubmitTextAsync(harness, sessionId, "what should I do today?");
    }
}
