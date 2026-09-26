namespace SentenceStudio.Contracts.Coach;

/// <summary>A generated topical vocabulary set that remains inert until the learner approves it.</summary>
public sealed class CoachVocabularySetProposal
{
    public required string ProposalId { get; init; }
    public required string Topic { get; init; }
    public required string Title { get; init; }
    public required string TargetLanguageTag { get; init; }
    public IReadOnlyList<CoachVocabularyTermDto> Terms { get; init; } = Array.Empty<CoachVocabularyTermDto>();
}

/// <summary>The active durable vocabulary proposal for one owned conversation.</summary>
public sealed class CoachConversationVocabularyStateDto
{
    public CoachVocabularySetProposal? PendingProposal { get; init; }
}

/// <summary>One target/native pair in a proposed vocabulary set.</summary>
public sealed class CoachVocabularyTermDto
{
    public required string TargetTerm { get; init; }
    public required string NativeTerm { get; init; }
}

/// <summary>A decision about an opaque, server-issued vocabulary proposal.</summary>
public sealed class ApproveCoachVocabularySetRequest
{
    public required string ProposalReference { get; init; }
    public required AppOperation.ApplicationOperationDecision Decision { get; init; }
}

/// <summary>The durable decision and, after acceptance, activity launch.</summary>
public sealed class CoachVocabularySetApprovalResponse
{
    public required string ProposalId { get; init; }
    public required AppOperation.ApplicationOperationDecision Decision { get; init; }
    public string? ResourceId { get; init; }
    public string? ActivityPath { get; init; }
}
