using SentenceStudio.Abstractions;
using SentenceStudio.Data;
using SentenceStudio.Services.Plans;
using SentenceStudio.Shared.Models;
using SentenceStudio.Shared.Services;

namespace SentenceStudio.Native.Adapters;

public sealed class NativeQuizEligibilityAdapter(
    IUserScopeProvider scope,
    IPreferencesService preferences,
    LearningResourceRepository resources) : INativeQuizEligibility
{
    public async Task<NativeQuizEligibilityResult> SelectAsync(
        string userId, bool dueOnly, IReadOnlyList<VocabularyQuizItem> candidates,
        CancellationToken cancellationToken)
    {
        NativeQuizOwnerGuard.RequireActiveOwner(userId, scope, preferences, cancellationToken);
        ArgumentNullException.ThrowIfNull(candidates);
        if (candidates.Any(item => item is null || item.Progress is null))
            throw new NotSupportedException(
                "Native quiz candidates require owner-scoped progress or an owner-bound new-word baseline.");
        await NativeQuizOwnerGuard.RequireOwnerReachableWordsAsync(
            userId, candidates.Select(item => item?.Word).ToArray(),
            scope, preferences, resources, cancellationToken);

        var selected = VocabQuizSourcePolicy.SelectNonFocusBatch(
            userId, dueOnly, candidates, DateTime.Now);
        NativeQuizOwnerGuard.RequireActiveOwner(userId, scope, preferences, cancellationToken);
        return new NativeQuizEligibilityResult(selected.Batch, selected.AllMastered);
    }
}
