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
    /// <param name="keepers">Copies the learner chose with Keep This: a merge that holds one keeps it.</param>
    /// <returns>
    /// What the rules merged and what the AI did; <see cref="VocabularyDuplicateAiCleanupResult.AiUnavailable"/>
    /// when the AI answered nothing (the rules' merges are still made), for the client to say so;
    /// <see cref="VocabularyDuplicateAiCleanupResult.Cancelled"/> when it stopped early (what it
    /// made is still reported, so the client records it for sync).
    /// </returns>
    public async Task<VocabularyDuplicateAiCleanupResult> RunAsync(string owner, string nativeLanguage,
        string targetLanguage, VocabularyDuplicateAiAsk ask, IReadOnlyCollection<string>? leaveOut,
        IReadOnlyCollection<string>? keepers, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(owner))
        {
            logger.LogWarning("Duplicate cleanup with AI called without a learner; nothing done.");
            return VocabularyDuplicateAiCleanupResult.None;
        }

        var rules = await repository.MergeSafeDuplicateGroupsAsync(cancellationToken, owner, leaveOut: leaveOut,
            keepers: keepers);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var left = leaveOut?.ToHashSet(StringComparer.Ordinal) ?? [];
            var groups = (await repository.GetDuplicateGroupsForAiAsync(cancellationToken, owner))
                .Where(group => !group.Copies.Any(copy => left.Contains(copy.Id)))
                .ToList();
            if (groups.Count == 0)
                return new VocabularyDuplicateAiCleanupResult(rules, VocabularyDuplicateApplyResult.None);

            IReadOnlyList<VocabularyDuplicateAiResult> results;
            try
            {
                results = await VocabularyDuplicateResolver.ResolveAsync(groups, nativeLanguage, targetLanguage, ask,
                    logger, cancellationToken);
            }
            catch (VocabularyDuplicateAiUnavailableException ex)
            {
                logger.LogError(ex, "Duplicate cleanup with AI: the AI answered none of {Count} groups.", groups.Count);
                return new VocabularyDuplicateAiCleanupResult(rules, VocabularyDuplicateApplyResult.None,
                    AiUnavailable: true);
            }
            var applied = await repository.ApplyDuplicateResolutionAsync(groups, results, cancellationToken, owner, keepers);
            return new VocabularyDuplicateAiCleanupResult(rules, applied,
                Cancelled: cancellationToken.IsCancellationRequested);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Stopped (the learner left) before the AI's calls applied: the rules' merges are
            // still reported.
            logger.LogInformation("Duplicate cleanup with AI cancelled after the rules' merges.");
            return new VocabularyDuplicateAiCleanupResult(rules, VocabularyDuplicateApplyResult.None, Cancelled: true);
        }
    }
}

/// <summary>
/// What the rules merged and what the AI pass did. <paramref name="AiUnavailable"/>: the AI
/// answered nothing, so it decided nothing; the client tells the learner (an error, not "0 merged").
/// <paramref name="Cancelled"/>: the pass stopped early; what it made is still here.
/// </summary>
public sealed record VocabularyDuplicateAiCleanupResult(
    VocabularyMergeAllResult Rules, VocabularyDuplicateApplyResult Ai, bool AiUnavailable = false,
    bool Cancelled = false)
{
    public static VocabularyDuplicateAiCleanupResult None { get; } =
        new(VocabularyMergeAllResult.None, VocabularyDuplicateApplyResult.None);
}
