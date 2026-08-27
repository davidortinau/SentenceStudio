using Microsoft.Extensions.Logging;
using SentenceStudio.Api.Coach.Persistence.History;

namespace SentenceStudio.Api.Coach.Memory;

/// <summary>
/// Removes every saved preference when a learner's coach data is deleted.
/// </summary>
/// <remarks>
/// <para>
/// Registered through the same enumerable the history contributors use, so the deletion
/// coordinator finds it without a hand-maintained table list.
/// </para>
/// <para>
/// Deliberately not gated on the feature flag. A learner who asks to be forgotten is not asking
/// conditionally, and a flag that was on last month can have left rows behind.
/// </para>
/// <para>
/// Idempotent by construction: the second pass finds nothing and reports zero, which is what the
/// coordinator's double-run verification requires.
/// </para>
/// </remarks>
public sealed class CoachMemoryDeletionContributor : ICoachDataDeletionContributor
{
    private readonly ICoachMemoryStore _store;
    private readonly ICoachMemoryChangedNotifier _notifier;
    private readonly ILogger<CoachMemoryDeletionContributor> _logger;

    /// <summary>Creates the contributor.</summary>
    public CoachMemoryDeletionContributor(
        ICoachMemoryStore store,
        ICoachMemoryChangedNotifier notifier,
        ILogger<CoachMemoryDeletionContributor> logger)
    {
        _store = store;
        _notifier = notifier;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => "CoachMemoryFact";

    /// <inheritdoc />
    public async Task<int> DeleteAllAsync(CoachOwner owner, CancellationToken cancellationToken = default)
    {
        if (owner.IsEmpty)
        {
            _logger.LogWarning("[Coach] Memory deletion called with no owner — refusing.");
            return 0;
        }

        var deleted = await _store.DeleteAllForOwnerAsync(owner, cancellationToken).ConfigureAwait(false);

        if (deleted > 0)
        {
            // Tell the checkpoint owner even though the account is going away: a serialized
            // session that still holds a preference is exactly what "forget me" has to reach.
            await _notifier.MemoryChangedAsync(
                owner,
                CoachMemoryChangeKind.ForgottenAll,
                deleted,
                cancellationToken).ConfigureAwait(false);
        }

        return deleted;
    }
}

/// <summary>
/// Removes the preferences a single conversation produced when that conversation is deleted.
/// </summary>
/// <remarks>
/// <para>
/// A v1 fact has exactly one source, so "delete facts whose only provenance is this conversation"
/// is simply "delete facts sourced from this conversation". Both candidates and active facts go:
/// a candidate that outlived the conversation it came from could be approved later with nothing
/// left to explain where it came from.
/// </para>
/// <para>
/// This runs as an explicit call rather than a database cascade so the checkpoint notifier stays in
/// the loop. A cascade would delete the row and leave the same preference alive inside an already
/// serialized agent session.
/// </para>
/// </remarks>
public sealed class CoachMemorySourceDeletionHandler
{
    private readonly ICoachMemoryStore _store;
    private readonly ICoachMemoryChangedNotifier _notifier;
    private readonly ILogger<CoachMemorySourceDeletionHandler> _logger;

    /// <summary>Creates the handler.</summary>
    public CoachMemorySourceDeletionHandler(
        ICoachMemoryStore store,
        ICoachMemoryChangedNotifier notifier,
        ILogger<CoachMemorySourceDeletionHandler> logger)
    {
        _store = store;
        _notifier = notifier;
        _logger = logger;
    }

    /// <summary>Deletes every fact sourced from one conversation. Returns how many rows went.</summary>
    public async Task<int> OnConversationDeletedAsync(
        CoachOwner owner,
        string conversationId,
        CancellationToken cancellationToken = default)
    {
        if (owner.IsEmpty)
        {
            _logger.LogWarning("[Coach] Memory source deletion called with no owner — refusing.");
            return 0;
        }

        if (string.IsNullOrWhiteSpace(conversationId))
        {
            return 0;
        }

        var deleted = await _store.DeleteForSourceConversationAsync(owner, conversationId, cancellationToken).ConfigureAwait(false);

        if (deleted > 0)
        {
            await _notifier.MemoryChangedAsync(
                owner,
                CoachMemoryChangeKind.SourceDeleted,
                deleted,
                cancellationToken).ConfigureAwait(false);
        }

        return deleted;
    }
}
