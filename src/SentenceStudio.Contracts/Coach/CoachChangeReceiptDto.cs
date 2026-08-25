namespace SentenceStudio.Contracts.Coach;

/// <summary>
/// A receipt for one applied plan change.
/// The server sends a receipt only after it writes the change.
/// </summary>
public sealed class CoachChangeReceiptDto
{
    /// <summary>The receipt identifier.</summary>
    public required string ReceiptId { get; init; }

    /// <summary>The revision this receipt describes.</summary>
    public required CoachRevisionDto Revision { get; init; }

    /// <summary>The localized summary of the change, for example "Updated 3 remaining items".</summary>
    public required string Summary { get; init; }

    /// <summary>The constraint change the server applied.</summary>
    public required CoachConstraintDeltaDto AppliedDelta { get; init; }

    /// <summary>
    /// What this change did to the vocabulary focus, and the focus in force after it.
    /// </summary>
    /// <remarks>
    /// Always describes the operation this receipt is for, so a superseded or undone revision is
    /// never relabelled by a later one. A client should read this rather than diffing the active
    /// constraints, which cannot distinguish "no focus" from "the focus was just cleared".
    /// </remarks>
    public CoachVocabularyFocusChangeDto VocabularyFocus { get; init; } =
        CoachVocabularyFocusChangeDto.Unchanged(null);

    /// <summary>The difference between the plan before and the plan after.</summary>
    public required CoachPlanDiffDto Diff { get; init; }

    /// <summary>The number of unfinished items the server replaced.</summary>
    public required int ReplacedItemCount { get; init; }

    /// <summary>The number of completed items the server kept.</summary>
    public required int PreservedCompletedItemCount { get; init; }

    /// <summary>The number of started items the server kept.</summary>
    public required int PreservedInProgressItemCount { get; init; }

    /// <summary>The logged minutes the server kept. This number never goes down.</summary>
    public required int PreservedMinutesSpent { get; init; }

    /// <summary>True if the learner can undo this change now.</summary>
    public required bool CanUndo { get; init; }

    /// <summary>The localized label of the undo action.</summary>
    public required string UndoLabel { get; init; }
}
