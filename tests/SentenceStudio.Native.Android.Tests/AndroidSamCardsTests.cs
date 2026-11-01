using SentenceStudio.Contracts.Coach;
using SentenceStudio.Contracts.LearnerMemory;
using SentenceStudio.Native;
using SentenceStudio.Native.Android;
using SentenceStudio.Services.Api;

namespace SentenceStudio.Native.Android.Tests;

/// <summary>
/// The Compose Sam cards' platform-neutral rules, matching the iOS SamView: repair
/// disclosure, proposed-change card stages and controls (soft Apply / Not now, hard
/// explicit confirm step), Copy / report, memory candidate, dispute, conversation
/// management and the status row. No Android UI types.
/// </summary>
public sealed class AndroidSamCardsTests
{
    private static readonly DateTime Now = new(2026, 10, 31, 12, 0, 0, DateTimeKind.Utc);

    private static NativeSamEntry Sam(string id = "m1", CoachWriteOperationDto? write = null,
        CoachRepairDisclosure? repair = null, bool evidence = false,
        CoachMessageKind kind = CoachMessageKind.Text) =>
        new(id, NativeSamEntryKind.Sam, "answer", Now, 1, [], [], null, [], MessageKind: kind,
            Write: write, Repair: repair, RepairEvidenceOnScreen: evidence);

    private static NativeSamState Open(params NativeSamEntry[] entries) => new(
        IsVisible: true, IsOpen: true, Status: NativeSamStatus.Ready, ConversationId: "c1",
        Entries: [.. entries], SamWriteAvailable: true, IsDurableHistoryAvailable: true);

    private static CoachWriteOperationDto Write(bool hard, CoachWriteStatus status = CoachWriteStatus.Proposed,
        bool reversible = true, CoachWriteReceiptDto? receipt = null) => new()
    {
        OperationId = "op1",
        ConversationId = "c1",
        ChangeKind = CoachWriteChangeKind.VocabularyAdd,
        RiskClass = hard ? CoachWriteRiskClass.WriteHard : CoachWriteRiskClass.WriteSoft,
        Status = status,
        ApprovalMode = hard ? "confirm" : "accept",
        Summary = "Add 사과",
        Lines = ["사과 - apple"],
        ExpiresAtUtc = Now.AddMinutes(10),
        RequiresConfirmation = hard,
        IsReversible = reversible,
        Receipt = receipt,
    };

    [Theory]
    [InlineData(CoachRepairDisclosure.AnswerAltered, false, "Coach_Repair_AnswerAltered")]
    [InlineData(CoachRepairDisclosure.RepairSuppressedForLanguage, true, "Coach_Repair_SuppressedForLanguage")]
    [InlineData(CoachRepairDisclosure.RepairSuppressedForLanguage, false,
        "Coach_Repair_SuppressedForLanguage_NoEvidence")]
    public void RepairDisclosureAlwaysNamesWhatHappened(CoachRepairDisclosure repair, bool evidence, string key) =>
        Assert.Equal(key, AndroidSamCards.RepairKey(Sam(repair: repair, evidence: evidence)));

    [Fact]
    public void NoRepairNoticeForNoneLearnerOrALimitation()
    {
        Assert.Null(AndroidSamCards.RepairKey(Sam(repair: CoachRepairDisclosure.None)));
        Assert.Null(AndroidSamCards.RepairKey(Sam()));
        Assert.Null(AndroidSamCards.RepairKey(Sam(repair: CoachRepairDisclosure.AnswerAltered) with
            { Kind = NativeSamEntryKind.Learner }));
    }

    [Fact]
    public void SoftWriteOffersApplyAndNotNowWithNoConfirmStep()
    {
        var state = Open(Sam(write: Write(hard: false))) with { ActiveWriteId = "op1" };
        var card = AndroidSamCards.Write(state, state.SafeEntries[0], Now)!;

        Assert.Equal(NativeSamWriteStage.Proposed, card.Stage);
        Assert.Equal([AndroidSamWriteAction.Accept, AndroidSamWriteAction.Decline],
            card.Buttons.Select(button => button.Action));
        Assert.Equal(["Coach_WriteAccept", "Coach_WriteDecline"], card.Buttons.Select(button => button.LabelKey));
        Assert.False(card.ShowsConfirmStep);
        Assert.Contains(card.Notes, note => note.Key == "Coach_WriteNothingChangedYet");
        Assert.True(card.ButtonsEnabled);
    }

    [Fact]
    public void HardWriteNeedsReviewThenAnExplicitDangerConfirm()
    {
        var proposed = Open(Sam(write: Write(hard: true, reversible: false))) with { ActiveWriteId = "op1" };
        var review = AndroidSamCards.Write(proposed, proposed.SafeEntries[0], Now)!;
        Assert.Equal([AndroidSamWriteAction.Review, AndroidSamWriteAction.Decline],
            review.Buttons.Select(button => button.Action));
        Assert.Contains(review.Notes, note => note is { Key: "Coach_WriteIrreversibleNotice", Danger: true });
        Assert.Contains(review.Notes, note => note.Key == "Coach_WriteProtectedNotice");

        var confirming = proposed with { ConfirmingWriteId = "op1", ConfirmationExpiresAtUtc = Now.AddMinutes(2) };
        var confirm = AndroidSamCards.Write(confirming, confirming.SafeEntries[0], Now)!;
        Assert.Equal(NativeSamWriteStage.ConfirmationRequired, confirm.Stage);
        Assert.True(confirm.ShowsConfirmStep);
        Assert.True(confirm.ConfirmIrreversible);
        Assert.Equal(Now.AddMinutes(2), confirm.ConfirmExpiresAtUtc);
        Assert.Equal([AndroidSamWriteAction.Confirm, AndroidSamWriteAction.CancelConfirm],
            confirm.Buttons.Select(button => button.Action));
        Assert.Equal(AndroidSamButtonStyle.Danger, confirm.Buttons[0].Style);
    }

    [Fact]
    public void BusyOrForeignBusyWriteDisablesEveryControl()
    {
        var state = Open(Sam(write: Write(hard: false))) with { ActiveWriteId = "op1", WriteBusyId = "op1" };
        var card = AndroidSamCards.Write(state, state.SafeEntries[0], Now)!;
        Assert.False(card.ButtonsEnabled);
        Assert.Equal("Coach_WriteBusy", card.Buttons[0].LabelKey);
        Assert.False(AndroidSamCards.Write(state with { WriteBusyId = "other" }, state.SafeEntries[0], Now)!
            .ButtonsEnabled);
    }

    [Fact]
    public void AppliedShowsUndoOnlyInsideTheReceiptWindowAndErrorsStayOnTheirCard()
    {
        var receipt = new CoachWriteReceiptDto
        {
            OperationId = "op1", ChangeKind = CoachWriteChangeKind.VocabularyAdd,
            RiskClass = CoachWriteRiskClass.WriteSoft, Status = CoachWriteStatus.Executed,
            Summary = "Added 사과", ExecutedAtUtc = Now, CanUndo = true, UndoExpiresAtUtc = Now.AddMinutes(5),
        };
        var state = Open(Sam(write: Write(hard: false, CoachWriteStatus.Executed, receipt: receipt))) with
            { WriteErrorId = "op1", WriteErrorKey = "Coach_WriteUnavailable" };
        var card = AndroidSamCards.Write(state, state.SafeEntries[0], Now)!;
        Assert.Equal(NativeSamWriteStage.Applied, card.Stage);
        Assert.Equal("Added 사과", card.Summary);
        Assert.Equal([AndroidSamWriteAction.Undo], card.Buttons.Select(button => button.Action));
        Assert.Equal(Now.AddMinutes(5), card.UndoClosesAtUtc);
        Assert.Equal("Coach_WriteUnavailable", card.ErrorKey);

        var late = AndroidSamCards.Write(state, state.SafeEntries[0], Now.AddMinutes(6))!;
        Assert.Empty(late.Buttons);
        Assert.Null(late.UndoClosesAtUtc);
    }

    [Fact]
    public void InDoubtOffersOnlyARefreshAndTheSurfaceGateHidesCards()
    {
        var state = Open(Sam(write: Write(hard: false, CoachWriteStatus.Executing)));
        Assert.Equal([AndroidSamWriteAction.Refresh],
            AndroidSamCards.Write(state, state.SafeEntries[0], Now)!.Buttons.Select(button => button.Action));
        Assert.Null(AndroidSamCards.Write(state with { SamWriteAvailable = false }, state.SafeEntries[0], Now));
        Assert.Null(AndroidSamCards.Write(state with { ConversationId = null }, state.SafeEntries[0], Now));
    }

    /// <summary>The one-use confirmation value is not in published state, so no card field can carry it.</summary>
    [Fact]
    public void ConfirmationValueNeverReachesTheCard()
    {
        Assert.DoesNotContain(typeof(NativeSamState).GetProperties(),
            property => property.PropertyType == typeof(CoachWriteConfirmation));
        Assert.DoesNotContain(typeof(AndroidSamWriteCard).GetProperties(),
            property => property.PropertyType == typeof(CoachWriteConfirmation)
                || property.Name.Contains("Token", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void CopyAndReportFollowTheMessageState()
    {
        var settled = Sam();
        Assert.True(AndroidSamCards.ShowsMessageActions(settled));
        Assert.False(AndroidSamCards.ShowsMessageActions(settled with { IsPending = true }));
        Assert.False(AndroidSamCards.ShowsMessageActions(settled with { Kind = NativeSamEntryKind.Learner }));
        Assert.False(AndroidSamCards.ShowsMessageActions(Sam("local:1")));

        var state = Open(settled) with { ReportingAvailable = true };
        Assert.Equal("Coach_Copy", AndroidSamCards.CopyLabelKey(state, "m1"));
        Assert.Equal("Coach_Copied", AndroidSamCards.CopyLabelKey(state with { CopiedMessageId = "m1" }, "m1"));
        Assert.Equal("Coach_CopyFailed",
            AndroidSamCards.CopyLabelKey(state with { CopyFailedMessageId = "m1" }, "m1"));
        Assert.True(AndroidSamCards.IsReportable(state, settled));
        Assert.False(AndroidSamCards.IsReportable(state with { ReportingAvailable = false }, settled));
        Assert.Equal(5, AndroidSamCards.ReportReasons.Length);
    }

    [Fact]
    public void MemoryCandidateAndDisputeRowsTakeTheirSourcePlaces()
    {
        var fact = new CoachMemoryFactDto("f1", CoachMemoryKind.PersistentStudyGoal, CoachMemoryStatus.Candidate,
            CoachMemoryScope.TargetLanguage, "ko", new CoachMemoryValueDto(), "Pass TOPIK II", CoachMemoryProvenance.UserExplicit,
            1, Now, Now, null, null, null, null, 1);
        var state = Open(Sam()) with
        {
            MemoryCandidate = fact,
            Dispute = new CoachDisputeDto
                { Signal = CoachDisputeSignal.WrongClaim, Status = CoachDisputeStatus.Open },
        };
        var kinds = AndroidSamProjection.Rows(state).Select(row => row.Kind).ToArray();
        Assert.Equal(AndroidSamRowKind.Dispute, kinds[0]);
        Assert.Contains(AndroidSamRowKind.MemoryCandidate, kinds);
        Assert.Equal("Coach_Dispute_Open", AndroidSamProjection.DisputeStatusKey(state));
        Assert.Equal("Coach_Dispute_Signal_WrongClaim", AndroidSamCards.DisputeSignalKey(state.Dispute!));
        Assert.True(AndroidSamCards.IsEditable(fact));
        Assert.Equal("Coach_MemoryScopeLanguageNamed:ko",
            AndroidSamCards.MemoryScope(fact, key => key + ":{0}").Replace("{0}", ""));

        var noticeOnly = state with { MemoryCandidate = null, MemoryCandidateNoticeKey = "Coach_MemorySaved",
            Dispute = new CoachDisputeDto() };
        var rows = AndroidSamProjection.Rows(noticeOnly).Select(row => row.Kind).ToArray();
        Assert.Contains(AndroidSamRowKind.MemoryNotice, rows);
        Assert.DoesNotContain(AndroidSamRowKind.Dispute, rows);
    }

    [Fact]
    public void EditsSeedOnlyFromTheirTargetAndRenameIsBounded()
    {
        var state = Open() with
        {
            ConversationDialog = new NativeSamConversationDialog(NativeSamConversationDialogKind.Rename, "c9",
                "Old", "Old"),
        };
        Assert.Equal(("rename:c9", "Old"), AndroidSamCards.RenameSeed(state));
        Assert.Equal((null, string.Empty), AndroidSamCards.RenameSeed(state with
        {
            ConversationDialog = state.ConversationDialog! with { Kind = NativeSamConversationDialogKind.Delete }
        }));
        Assert.True(AndroidSamCards.CanRename("New title", busy: false));
        Assert.False(AndroidSamCards.CanRename("   ", busy: false));
        Assert.False(AndroidSamCards.CanRename(new string('a', 121), busy: false));
        Assert.False(AndroidSamCards.CanRename("New", busy: true));
        Assert.Equal(("memory:f1", "goal"), AndroidSamCards.MemorySeed(state with
            { Memory = new NativeSamMemoryState(EditingId: "f1", EditText: "goal") }));
    }

    [Fact]
    public void FailedStatusUsesTheTypedFailureAndClarificationHasNoStatusRow()
    {
        var failed = Open() with { Status = NativeSamStatus.Failed, FailureKey = "Coach_Failure_{0}" };
        Assert.Equal("Coach_Failure_Sam",
            AndroidSamProjection.Status(failed, "Sam", key => key)!.Message);
        // Source CoachStateNotice has no Clarification row: the question is Sam's message
        // and, with a suggestion, re-frames the suggestion card (as on iOS).
        Assert.Null(AndroidSamProjection.Status(Open() with
            { Status = NativeSamStatus.Clarification, ClarifyingQuestion = "Which day?" }, "Sam", key => key));
        Assert.NotNull(AndroidSamProjection.Status(Open() with
            { Status = NativeSamStatus.ClarificationLimitReached }, "Sam", key => key));
    }
}
