using SentenceStudio.Contracts.Coach;

namespace SentenceStudio.UnitTests.Coach;

/// <summary>
/// Fully populated coach contract samples for the round-trip tests.
/// </summary>
internal static class CoachContractSamples
{
    private static readonly DateTime Now = new(2026, 8, 14, 20, 0, 0, DateTimeKind.Utc);
    private static readonly DateOnly Today = new(2026, 8, 14);

    public static CoachConstraintSetDto Constraints() => new()
    {
        AvailableMinutes = 10,
        AudioAllowed = false,
        SpeechAllowed = true,
        TypingAllowed = true,
        SkillEmphasis = CoachSkillEmphasis.Speaking,
        GoalTag = "travel",
        GoalHorizonDays = 30,
        EnergyLevel = CoachEnergyLevel.Low
    };

    public static CoachConstraintDeltaDto Delta() => new()
    {
        AvailableMinutes = 10,
        AudioAllowed = false,
        SkillEmphasis = CoachSkillEmphasis.Speaking,
        GoalTag = "travel",
        GoalHorizonDays = 30,
        EnergyLevel = CoachEnergyLevel.Low,
        ChangedFields = new[]
        {
            CoachConstraintField.AvailableMinutes,
            CoachConstraintField.AudioAllowed,
            CoachConstraintField.SkillEmphasis
        }
    };

    public static CoachPlanItemDto PlanItem(
        string id,
        CoachPlanActivityType activityType,
        CoachPlanItemChangeKind changeKind) => new()
    {
        Id = id,
        ActivityType = activityType,
        Title = "Review your due words",
        Description = "A short review of the words that are due today.",
        Priority = 1,
        EstimatedMinutes = 5,
        MinutesSpent = 2,
        IsCompleted = changeKind == CoachPlanItemChangeKind.PreservedCompleted,
        ChangeKind = changeKind,
        ResourceTitle = "Travel phrases"
    };

    public static CoachPlanDiffDto Diff() => new()
    {
        BeforePlanVersion = "plan-v1",
        AfterPlanVersion = "plan-v2",
        IsPreview = false,
        Items = new[]
        {
            PlanItem("item-1", CoachPlanActivityType.VocabularyReview, CoachPlanItemChangeKind.PreservedCompleted),
            PlanItem("item-2", CoachPlanActivityType.Shadowing, CoachPlanItemChangeKind.Added)
        },
        AddedItemCount = 1,
        RemovedItemCount = 1,
        AdjustedItemCount = 0,
        PreservedCompletedItemCount = 1,
        PreservedInProgressItemCount = 1,
        EstimatedMinutesBefore = 20,
        EstimatedMinutesAfter = 10
    };

    public static CoachEvidenceDto Evidence() => new()
    {
        Kind = CoachEvidenceKind.PracticeBalance,
        Label = "Practice balance",
        Summary = "Your last 14 days were mostly input.",
        WindowStartDate = Today.AddDays(-13),
        WindowEndDate = Today,
        Values = new[]
        {
            new CoachEvidenceValueDto { Label = "Input minutes", Value = 120, Unit = CoachEvidenceUnit.Minutes },
            new CoachEvidenceValueDto { Label = "Output minutes", Value = 18, Unit = CoachEvidenceUnit.Minutes }
        }
    };

    public static CoachRevisionDto Revision() => new()
    {
        RevisionId = "revision-1",
        RevisionNumber = 1,
        Source = CoachRevisionSource.DirectRequest,
        ChangedFields = new[] { CoachConstraintField.AvailableMinutes, CoachConstraintField.AudioAllowed },
        Summary = "Today's Plan now fits 10 minutes and uses no audio.",
        BeforePlanVersion = "plan-v1",
        AfterPlanVersion = "plan-v2",
        CreatedAtUtc = Now,
        IsUndone = false,
        CanUndo = true
    };

    public static CoachPlanStateDto PlanState() => new()
    {
        PlanDate = Today,
        PlanVersion = "plan-v2",
        Items = new[]
        {
            PlanItem("item-1", CoachPlanActivityType.VocabularyReview, CoachPlanItemChangeKind.Unchanged),
            PlanItem("item-2", CoachPlanActivityType.Shadowing, CoachPlanItemChangeKind.Unchanged)
        },
        AppliedConstraints = Constraints(),
        EstimatedTotalMinutes = 10,
        CompletedCount = 1,
        TotalCount = 2,
        CompletionPercentage = 50,
        LastRevision = Revision(),
        CanUndo = true
    };

    public static PendingCoachSuggestionDto Suggestion() => new()
    {
        SuggestionId = "suggestion-1",
        Delta = Delta(),
        Rationale = "A short speaking activity balances your week.",
        Preview = new CoachPlanDiffDto
        {
            BeforePlanVersion = "plan-v2",
            AfterPlanVersion = "preview-abc",
            IsPreview = true,
            Items = new[] { PlanItem("item-3", CoachPlanActivityType.Conversation, CoachPlanItemChangeKind.Added) },
            AddedItemCount = 1,
            RemovedItemCount = 0,
            AdjustedItemCount = 0,
            PreservedCompletedItemCount = 1,
            PreservedInProgressItemCount = 0,
            EstimatedMinutesBefore = 10,
            EstimatedMinutesAfter = 14
        },
        Evidence = new[] { Evidence() },
        AcceptLabel = "Include speaking",
        RejectLabel = "Not now",
        CreatedAtUtc = Now,
        ExpiresAtUtc = Now.AddHours(24)
    };

    public static CoachChangeReceiptDto Receipt() => new()
    {
        ReceiptId = "receipt-1",
        Revision = Revision(),
        Summary = "Updated 3 remaining items. Preserved 2 completed items.",
        AppliedDelta = Delta(),
        Diff = Diff(),
        ReplacedItemCount = 3,
        PreservedCompletedItemCount = 2,
        PreservedInProgressItemCount = 1,
        PreservedMinutesSpent = 12,
        CanUndo = true,
        UndoLabel = "Undo"
    };

    public static CoachMessageDto Message() => new()
    {
        MessageId = "message-1",
        Role = CoachMessageRole.Coach,
        Kind = CoachMessageKind.Receipt,
        Text = "Today's Plan now fits 10 minutes and uses no audio.",
        CreatedAtUtc = Now,
        RelatedSuggestionId = "suggestion-1",
        RelatedReceiptId = "receipt-1"
    };

    public static CoachTurnResponse TurnResponse() => new()
    {
        SessionId = "session-1",
        TurnId = "turn-1",
        Status = CoachTurnStatus.Completed,
        StopReason = CoachStopReason.Completed,
        SessionStatus = CoachSessionStatus.Active,
        Messages = new[] { Message() },
        ActiveConstraints = Constraints(),
        PlanState = PlanState(),
        PendingSuggestion = Suggestion(),
        ChangeReceipt = Receipt(),
        Evidence = new[] { Evidence() },
        ClarifyingQuestion = "Should I add the speaking activity to Today's Plan now?",
        ClarificationsRemaining = 1,
        RunsRemainingToday = 4,
        ExpiresAtUtc = Now.AddHours(24)
    };

    public static CoachSessionResponse SessionResponse() => new()
    {
        SessionId = "session-1",
        Status = CoachSessionStatus.SuggestionPending,
        Messages = new[] { Message() },
        ActiveConstraints = Constraints(),
        PlanState = PlanState(),
        PendingSuggestion = Suggestion(),
        Evidence = new[] { Evidence() },
        Revisions = new[] { Revision() },
        ClarificationsRemaining = 2,
        RunsRemainingToday = 4,
        CreatedAtUtc = Now,
        ExpiresAtUtc = Now.AddHours(24)
    };
}
