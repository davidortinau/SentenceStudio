using SentenceStudio.Contracts.Coach;

namespace SentenceStudio.Api.Coach.Validation;

/// <summary>
/// Applies the current learner-specific embargo to every string a vocabulary proposal can surface.
/// </summary>
internal sealed class CoachVocabularySetEmbargoValidator(
    ICoachValidationDataSource validationData,
    CoachDueItemLeakValidator leakValidator)
{
    public async Task<CoachValidationResult> ValidateAsync(
        string userProfileId,
        CoachVocabularySetProposal proposal,
        CancellationToken cancellationToken)
    {
        var surfacedText = proposal.Terms
            .SelectMany(static entry => new[]
            {
                entry.TargetTerm,
                entry.NativeTerm
            })
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .ToArray();

        if (surfacedText.Length == 0)
        {
            return CoachValidationResult.Valid;
        }

        var embargoedItems = await validationData.GetEmbargoedItemsAsync(
            userProfileId,
            cancellationToken: cancellationToken);
        return leakValidator.ValidateMany(
            surfacedText,
            embargoedItems);
    }

    public async Task<bool> IsSafeAsync(
        string userProfileId,
        CoachVocabularySetProposal proposal,
        CancellationToken cancellationToken) =>
        (await ValidateAsync(userProfileId, proposal, cancellationToken).ConfigureAwait(false)).IsValid;
}
