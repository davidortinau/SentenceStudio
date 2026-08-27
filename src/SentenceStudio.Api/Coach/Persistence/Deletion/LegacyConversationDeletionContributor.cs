using SentenceStudio.Api.Coach.Persistence.History;
using SentenceStudio.Data;

namespace SentenceStudio.Api.Coach.Persistence.Deletion;

/// <summary>
/// Deletes the learner's owned rows in the legacy Conversation activity as part of account
/// erasure.
/// </summary>
/// <remarks>
/// <para>
/// The legacy conversation tables predate coach persistence and belong to another lane. This
/// contributor owns none of that logic: it calls
/// <see cref="IConversationOwnerDataService.DeleteOwnedAsync"/> and reports the count, so the
/// rules about what "owned" means stay in one place and this file does not have to change when
/// they do.
/// </para>
/// <para>
/// <b>Ownerless rows are never touched.</b> Legacy rows written before owner scoping carry a null
/// <c>UserProfileId</c>. Deleting them during someone's account erasure would mean guessing that
/// they belong to whoever happens to be leaving, and the cost of guessing wrong is another
/// person's data. They are left alone, they are not counted toward this deletion, and they are
/// never mentioned to the user — an "and 412 other records we could not attribute" line in a
/// deletion confirmation tells a learner about data that is not theirs.
/// </para>
/// <para>
/// <b>It writes through another context.</b> The conversation service resolves its own
/// <c>ApplicationDbContext</c>, so this contributor is declared as an external store: the
/// coordinator either enlists that context in its transaction (the production shape, where coach
/// and legacy tables share one database) or runs this contributor after the coach commit and
/// reports partial completion. What it must never do is commit legacy deletes in the middle of a
/// transaction that can still roll back.
/// </para>
/// </remarks>
public sealed class LegacyConversationDeletionContributor : ICoachExternalStoreDeletionContributor
{
    private readonly IConversationOwnerDataService _conversations;
    private readonly ILogger<LegacyConversationDeletionContributor> _logger;

    public LegacyConversationDeletionContributor(
        IConversationOwnerDataService conversations,
        ILogger<LegacyConversationDeletionContributor> logger)
    {
        _conversations = conversations ?? throw new ArgumentNullException(nameof(conversations));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public string Name => "LegacyConversation";

    /// <inheritdoc />
    public async Task<int> DeleteAllAsync(CoachOwner owner, CancellationToken cancellationToken = default)
    {
        if (owner.IsEmpty)
        {
            _logger.LogWarning(
                "[Coach] {Contributor} was called with no owner — deleting nothing.",
                Name);
            return 0;
        }

        var result = await _conversations.DeleteOwnedAsync(owner.UserProfileId, cancellationToken);

        await GuardAgainstSurvivingRowsAsync(owner, cancellationToken);

        // Counts only, and only of rows that were actually attributed to this learner.
        _logger.LogInformation(
            "[Coach] {Contributor} deleted {ConversationCount} conversations and {ChunkCount} chunks.",
            Name, result.ConversationsDeleted, result.ChunksDeleted);

        return result.ConversationsDeleted + result.ChunksDeleted;
    }

    /// <summary>
    /// Confirms the learner has no owned conversations left, and throws if any survive.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This exists because <see cref="IConversationOwnerDataService.DeleteOwnedAsync"/> reports a
    /// database failure as a zero-row result rather than an exception. Without a check, a delete
    /// that failed on a broken connection is indistinguishable from a learner who simply had no
    /// conversations, and the account deletion would report success over rows that are still
    /// there. The coordinator's own second pass cannot catch it either: a repeat call fails the
    /// same way and returns the same reassuring zero.
    /// </para>
    /// <para>
    /// The check reads back through the export half of the same contract, which does surface its
    /// errors. So a genuinely broken database throws here, and a delete that quietly did nothing
    /// is caught by the rows it left behind. Either way the coordinator rolls back and the
    /// account deletion fails, which is what keeps "your data is gone" honest.
    /// </para>
    /// </remarks>
    private async Task GuardAgainstSurvivingRowsAsync(CoachOwner owner, CancellationToken cancellationToken)
    {
        var remaining = await _conversations.ExportOwnedAsync(owner.UserProfileId, cancellationToken);
        var survivingConversations = remaining.Conversations.Count;

        if (survivingConversations == 0)
        {
            return;
        }

        _logger.LogError(
            "[Coach] {Contributor} found {SurvivingCount} owned conversations still present after " +
            "deletion. Failing the erasure rather than reporting success over surviving data.",
            Name, survivingConversations);

        throw new InvalidOperationException(
            $"Legacy conversation deletion left {survivingConversations} owned conversation(s) in place.");
    }
}
