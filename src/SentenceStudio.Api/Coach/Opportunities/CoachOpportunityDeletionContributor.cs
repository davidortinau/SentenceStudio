using Microsoft.EntityFrameworkCore;
using SentenceStudio.Api.Coach.Persistence;
using SentenceStudio.Api.Coach.Persistence.History;

namespace SentenceStudio.Api.Coach.Opportunities;

/// <summary>
/// Removes every opportunity row a learner owns when their account is erased.
/// </summary>
/// <remarks>
/// <para>
/// Registered with <c>TryAddEnumerable</c> so <c>CoachDataDeletionService</c> <em>discovers</em>
/// it rather than being told about it. That is what makes this table covered from the moment it
/// exists: the coordinator holds no hand-maintained list, so a new store cannot be added in one
/// lane and forgotten in the deletion lane — which is exactly the shape of a data-subject-request
/// miss, and exactly what happened to <c>CoachSession</c> before the contributor pattern existed.
/// </para>
/// <para>
/// By joining the discovery it also inherits the coordinator's single transaction, its
/// idempotency requirement, and its second verification pass — so a forgotten filter here is
/// caught before the learner is told their data is gone, rather than after.
/// </para>
/// <para>
/// The rows are content-free, but they are still the learner's rows: they record that
/// <em>this learner</em> hit a gap, and erasure means erasure.
/// </para>
/// </remarks>
public sealed class CoachOpportunityDeletionContributor : ICoachDataDeletionContributor
{
    private readonly CoachDbContext _db;
    private readonly ILogger<CoachOpportunityDeletionContributor> _logger;

    public CoachOpportunityDeletionContributor(
        CoachDbContext db,
        ILogger<CoachOpportunityDeletionContributor> logger)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public string Name => "CoachOpportunity";

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

        var deleted = await _db.CoachOpportunities
            .Where(row => row.UserProfileId == userProfileId)
            .ExecuteDeleteAsync(cancellationToken)
            .ConfigureAwait(false);

        // A count only. No identifier, no capability code.
        _logger.LogInformation(
            "[Coach] {Contributor} deleted {RowCount} opportunity rows.",
            Name,
            deleted);

        return deleted;
    }
}
