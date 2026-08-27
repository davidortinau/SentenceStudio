using Microsoft.EntityFrameworkCore;
using SentenceStudio.Api.Coach.Persistence;
using SentenceStudio.Api.Coach.Persistence.History;

namespace SentenceStudio.Api.Coach.Reports;

/// <summary>
/// Removes every response report a learner filed when their account is erased.
/// </summary>
/// <remarks>
/// <para>
/// Registered with <c>TryAddEnumerable</c> so <c>CoachDataDeletionService</c> <em>discovers</em>
/// it. That is what makes this table covered from the moment it exists rather than from the
/// moment somebody remembers it: the coordinator holds no hand-maintained table list, so a store
/// cannot be added in one lane and forgotten in the deletion lane.
/// </para>
/// <para>
/// The rows are content-free, but they are still the learner's rows — they record that
/// <em>this learner</em> was unhappy with something — and erasure means erasure.
/// </para>
/// </remarks>
public sealed class CoachResponseReportDeletionContributor : ICoachDataDeletionContributor
{
    private readonly CoachDbContext _db;
    private readonly ILogger<CoachResponseReportDeletionContributor> _logger;

    public CoachResponseReportDeletionContributor(
        CoachDbContext db,
        ILogger<CoachResponseReportDeletionContributor> logger)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public string Name => "CoachResponseReport";

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

        var deleted = await _db.CoachResponseReports
            .Where(row => row.UserProfileId == userProfileId)
            .ExecuteDeleteAsync(cancellationToken)
            .ConfigureAwait(false);

        // A count only. No identifier, no reason.
        _logger.LogInformation(
            "[Coach] {Contributor} deleted {RowCount} response report rows.",
            Name,
            deleted);

        return deleted;
    }
}
