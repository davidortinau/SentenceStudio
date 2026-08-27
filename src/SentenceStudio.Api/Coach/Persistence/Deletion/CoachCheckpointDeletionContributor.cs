using Microsoft.EntityFrameworkCore;
using SentenceStudio.Api.Coach.Persistence.History;

namespace SentenceStudio.Api.Coach.Persistence.Deletion;

/// <summary>
/// Deletes the coach checkpoint tables for a learner: the session, its plan revisions, and its
/// usage rows.
/// </summary>
/// <remarks>
/// <para>
/// These are the tables that existed before durable history, and they were the ones account
/// deletion missed: <c>DeleteAccount</c> removed the identity user and the user profile and left
/// every <c>CoachSession</c> behind — including its protected conversation payload — keyed to a
/// <c>UserProfileId</c> that no longer resolves to anyone. The rows became unreachable but not
/// gone, which is the worst of both outcomes: undeletable by the learner and still present in
/// backups.
/// </para>
/// <para>
/// Registered as one contributor among many so the coordinator has no hand-maintained table
/// list. Children are deleted before parents explicitly rather than relying on a cascade,
/// because the test provider and the production provider do not have to agree about cascades.
/// </para>
/// </remarks>
public sealed class CoachCheckpointDeletionContributor : ICoachDataDeletionContributor
{
    private readonly CoachDbContext _db;
    private readonly ILogger<CoachCheckpointDeletionContributor> _logger;

    public CoachCheckpointDeletionContributor(
        CoachDbContext db,
        ILogger<CoachCheckpointDeletionContributor> logger)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public string Name => "CoachCheckpoint";

    /// <inheritdoc />
    public async Task<int> DeleteAllAsync(CoachOwner owner, CancellationToken cancellationToken = default)
    {
        if (owner.IsEmpty)
        {
            // Without an owner the filter would be empty and the delete would take every
            // learner's rows. "No owner" can only ever mean "delete nothing".
            _logger.LogWarning(
                "[Coach] {Contributor} was called with no owner — deleting nothing.",
                Name);
            return 0;
        }

        var userProfileId = owner.UserProfileId;

        // Revisions and usage first: they reference the session's learner and are the audit-side
        // rows, so a partial failure leaves the session (and therefore the learner's own delete
        // retry path) intact rather than orphaning the children.
        var revisions = await _db.CoachPlanRevisions
            .Where(revision => revision.UserProfileId == userProfileId)
            .ExecuteDeleteAsync(cancellationToken);

        var usage = await _db.CoachUsages
            .Where(row => row.UserProfileId == userProfileId)
            .ExecuteDeleteAsync(cancellationToken);

        var sessions = await _db.CoachSessions
            .Where(session => session.UserProfileId == userProfileId)
            .ExecuteDeleteAsync(cancellationToken);

        var total = revisions + usage + sessions;

        // Counts only. No identifier, no learner text, no payload.
        _logger.LogInformation(
            "[Coach] {Contributor} deleted {SessionCount} sessions, {RevisionCount} plan revisions, " +
            "and {UsageCount} usage rows.",
            Name, sessions, revisions, usage);

        return total;
    }
}
