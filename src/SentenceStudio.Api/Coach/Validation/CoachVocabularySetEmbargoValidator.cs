using SentenceStudio.Contracts.Coach;

namespace SentenceStudio.Api.Coach.Validation;

/// <summary>
/// Formerly refused a vocabulary proposal that named a word due for review. Being due is not a
/// secret (issue 258), so every proposal now passes; the type is kept for its callers and can be
/// removed in a follow-up.
/// </summary>
internal sealed class CoachVocabularySetEmbargoValidator(
    ICoachValidationDataSource validationData,
    CoachDueItemLeakValidator leakValidator)
{
    public Task<CoachValidationResult> ValidateAsync(
        string userProfileId,
        CoachVocabularySetProposal proposal,
        CancellationToken cancellationToken)
    {
        // Nothing is read: no due word is looked up, so none can refuse a proposal.
        _ = validationData;
        _ = leakValidator;
        return Task.FromResult(CoachValidationResult.Valid);
    }

    public async Task<bool> IsSafeAsync(
        string userProfileId,
        CoachVocabularySetProposal proposal,
        CancellationToken cancellationToken) =>
        (await ValidateAsync(userProfileId, proposal, cancellationToken).ConfigureAwait(false)).IsValid;
}
