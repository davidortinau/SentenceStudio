using Microsoft.Extensions.Logging;

namespace SentenceStudio.Services;

/// <summary>
/// "Resolve with AI" (docs/duplicate-merge-ai-plan.md), the same on every client: the rules merge
/// what loses nothing, the AI judges the groups left to review, its confident calls apply with a
/// record for Undo, and the rest come back for the learner with the AI's call. Each client passes
/// its own transport: the web and MAUI through <see cref="IAiService"/>, the native heads through
/// their owner-bound gateway.
/// </summary>
public sealed class VocabularyDuplicateAiCleanup(
    LearningResourceRepository repository, ILogger<VocabularyDuplicateAiCleanup> logger)
{
    /// <param name="leaveOut">
    /// Copies the learner left out or skipped on the page: the rules never merge them and the AI
    /// never sees a group that holds one.
    /// </param>
    public async Task<VocabularyDuplicateAiCleanupResult> RunAsync(string owner, string nativeLanguage,
        string targetLanguage, VocabularyDuplicateAiAsk ask, IReadOnlyCollection<string>? leaveOut,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(owner))
        {
            logger.LogWarning("Duplicate cleanup with AI called without a learner; nothing done.");
            return VocabularyDuplicateAiCleanupResult.None;
        }

        var rules = await repository.MergeSafeDuplicateGroupsAsync(cancellationToken, owner, leaveOut: leaveOut);
        cancellationToken.ThrowIfCancellationRequested();
        var left = leaveOut?.ToHashSet(StringComparer.Ordinal) ?? [];
        var groups = (await repository.GetDuplicateGroupsForAiAsync(cancellationToken, owner))
            .Where(group => !group.Copies.Any(copy => left.Contains(copy.Id)))
            .ToList();
        if (groups.Count == 0)
            return new VocabularyDuplicateAiCleanupResult(rules, VocabularyDuplicateApplyResult.None);

        var results = await VocabularyDuplicateResolver.ResolveAsync(groups, nativeLanguage, targetLanguage, ask,
            logger, cancellationToken);
        var applied = await repository.ApplyDuplicateResolutionAsync(groups, results, cancellationToken, owner);
        return new VocabularyDuplicateAiCleanupResult(rules, applied);
    }
}

/// <summary>What the rules merged and what the AI pass did.</summary>
public sealed record VocabularyDuplicateAiCleanupResult(
    VocabularyMergeAllResult Rules, VocabularyDuplicateApplyResult Ai)
{
    public static VocabularyDuplicateAiCleanupResult None { get; } =
        new(VocabularyMergeAllResult.None, VocabularyDuplicateApplyResult.None);
}
