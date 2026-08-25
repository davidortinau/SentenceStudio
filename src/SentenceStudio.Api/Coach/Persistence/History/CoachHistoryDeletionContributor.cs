using Microsoft.EntityFrameworkCore;

namespace SentenceStudio.Api.Coach.Persistence.History;

/// <summary>
/// Deletes durable conversation history for a learner.
/// </summary>
/// <remarks>
/// <para>
/// Children are deleted before parents inside one transaction. Relying on the database cascade
/// would make the behaviour depend on the provider, and the test provider is not the production
/// provider; doing it explicitly means the deletion path proven in tests is the deletion path
/// that runs against PostgreSQL.
/// </para>
/// <para>
/// This contributor owns the three history tables only. Plan revisions and usage rows belong to
/// other contributors, so a deletion request cannot silently take audit rows with it.
/// </para>
/// </remarks>
public sealed class CoachHistoryDeletionContributor : ICoachDataDeletionContributor
{
    private readonly CoachDbContext _db;
    private readonly ILogger<CoachHistoryDeletionContributor> _logger;

    public CoachHistoryDeletionContributor(CoachDbContext db, ILogger<CoachHistoryDeletionContributor> logger)
    {
        _db = db;
        _logger = logger;
    }

    public string Name => "CoachConversationHistory";

    public async Task<int> DeleteAllAsync(CoachOwner owner, CancellationToken cancellationToken = default)
    {
        if (owner.IsEmpty)
        {
            // An unfiltered delete here would erase every learner's history. Refusing is the only
            // safe reading of "no owner".
            _logger.LogWarning(
                "[Coach] {Contributor} called with no active user id — deleting nothing.",
                Name);
            return 0;
        }

        var ownsTransaction = _db.Database.CurrentTransaction is null;
        var transaction = ownsTransaction
            ? await _db.Database.BeginTransactionAsync(cancellationToken)
            : null;

        try
        {
            var messages = await _db.CoachMessages
                .Where(m => m.UserProfileId == owner.UserProfileId)
                .ExecuteDeleteAsync(cancellationToken);

            var operations = await _db.CoachTurnOperations
                .Where(o => o.UserProfileId == owner.UserProfileId)
                .ExecuteDeleteAsync(cancellationToken);

            var conversations = await _db.CoachConversations
                .Where(c => c.UserProfileId == owner.UserProfileId)
                .ExecuteDeleteAsync(cancellationToken);

            if (transaction is not null)
            {
                await transaction.CommitAsync(cancellationToken);
            }

            // ExecuteDelete bypasses the change tracker, so any tracked copy would be written back
            // on the next save and resurrect a row this call just deleted.
            _db.ChangeTracker.Clear();

            var total = messages + operations + conversations;

            _logger.LogInformation(
                "[Coach] {Contributor} deleted {Total} rows ({Conversations} conversations, {Messages} messages, {Operations} operations).",
                Name,
                total,
                conversations,
                messages,
                operations);

            return total;
        }
        catch
        {
            if (transaction is not null)
            {
                await transaction.RollbackAsync(cancellationToken);
            }

            throw;
        }
        finally
        {
            if (transaction is not null)
            {
                await transaction.DisposeAsync();
            }
        }
    }
}
