using Microsoft.Extensions.Logging;
using SentenceStudio.Application.Skills;
using SentenceStudio.Services.Plans;

namespace SentenceStudio.Api.Coach.Tools.SamTools;

/// <summary>Lists the learner's skill profiles.</summary>
public sealed class SkillListTool : CoachToolBase
{
    /// <summary>The largest page this read will return, whatever the caller asks for.</summary>
    /// <remarks>Public so the read-capability metadata cites it rather than restating it.</remarks>
    public const int MaxResults = 50;

    private readonly ISkillProfileQueries _skills;
    private readonly IPlanDateContext _dates;
    private readonly ILogger<SkillListTool> _logger;

    public SkillListTool(
        IUserScopeProvider userScope,
        ISkillProfileQueries skills,
        IPlanDateContext dates,
        ILogger<SkillListTool> logger)
        : base(userScope)
    {
        _skills = skills;
        _dates = dates;
        _logger = logger;
    }

    public override string ToolName => CoachToolNames.GetSkillList;

    public async Task<SkillListResult> GetAsync(int maxResults = 20, CancellationToken ct = default)
    {
        var userId = RequireUserProfileId();
        maxResults = Math.Clamp(maxResults, 1, MaxResults);

        try
        {
            var total = await _skills.CountActiveSkillsAsync(userId, ct);
            var summaries = await _skills.GetRecentActiveSkillsAsync(userId, maxResults, ct);

            var rows = summaries
                .Select(s => new SkillListEntry(
                    s.SkillId,
                    SanitizeMetadata(s.Title, 120),
                    s.Description == null ? null : SanitizeMetadata(s.Description, 200),
                    SanitizeMetadata(s.Language, 40)))
                .ToList();

            return new SkillListResult(
                total,
                rows.Count,
                rows,
                new CoachResultScope
                {
                    Coverage = total > rows.Count
                        ? CoachScopeCoverage.PageOfOwnedSet
                        : CoachScopeCoverage.CompleteOwnedSet,
                    Order = CoachScopeOrder.UpdatedDescending,
                    OrderHonored = true,
                    Filters = CoachScopeFilters.OwnerScoped | CoachScopeFilters.ExcludeArchived,
                    AsOfUtc = _dates.UtcNow,
                    RequestedCount = maxResults,
                    ReturnedCount = rows.Count,
                    MatchedCount = total,
                    Truncated = total > rows.Count,
                    DefinitionCode = CoachScopeDefinition.ActiveSkillList,
                    EligiblePopulationCount = total,
                    MinimumEvidence = CoachScopeMinimumEvidence.None,
                    // Two skills updated in the same tick have no defined relative order, and
                    // saying so is cheaper than pretending the list is stable.
                    TieBreak = CoachScopeTieBreak.None,
                    ClockBasis = CoachScopeClockBasis.ServerUtcInstant,
                    ReferenceMode = CoachScopeReferenceMode.AsOfInstant
                });
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { throw DataAccessFailure(ex); }
    }
}

/// <summary>Reads detail for one skill profile the learner owns.</summary>
public sealed class SkillDetailTool : CoachToolBase
{
    private readonly ISkillProfileQueries _skills;
    private readonly IPlanDateContext _dates;
    private readonly ILogger<SkillDetailTool> _logger;

    public SkillDetailTool(
        IUserScopeProvider userScope,
        ISkillProfileQueries skills,
        IPlanDateContext dates,
        ILogger<SkillDetailTool> logger)
        : base(userScope)
    {
        _skills = skills;
        _dates = dates;
        _logger = logger;
    }

    public override string ToolName => CoachToolNames.GetSkillDetail;

    public async Task<SkillDetailResult> GetAsync(string skillId, CancellationToken ct = default)
    {
        var userId = RequireUserProfileId();

        if (string.IsNullOrWhiteSpace(skillId))
            throw InvalidArgument("The skill identifier is required.");

        try
        {
            var row = await _skills.GetActiveSkillDetailAsync(userId, skillId, ct);

            if (row is null)
                throw InvalidArgument("The skill does not exist or does not belong to this learner.");

            var daysSince = row.CreatedAt == default
                ? 0
                : Math.Max(0, (int)(_dates.UtcNow.Date - row.CreatedAt.ToUniversalTime().Date).TotalDays);

            return new SkillDetailResult(
                SkillId: row.SkillId,
                Title: SanitizeMetadata(row.Title, 120),
                SkillDescription: row.Description is null ? null : SanitizeMetadata(row.Description, 500),
                Language: SanitizeMetadata(row.Language, 40),
                DaysSinceCreated: daysSince,
                Scope: new CoachResultScope
                {
                    Coverage = CoachScopeCoverage.SingleItem,
                    Order = CoachScopeOrder.NotApplicable,
                    OrderHonored = true,
                    Filters = CoachScopeFilters.OwnerScoped
                        | CoachScopeFilters.ExcludeArchived
                        | CoachScopeFilters.SingleIdentifier,
                    AsOfUtc = _dates.UtcNow,
                    ReturnedCount = 1,
                    DefinitionCode = CoachScopeDefinition.ActiveSkillDetail,
                    MinimumEvidence = CoachScopeMinimumEvidence.None,
                    TieBreak = CoachScopeTieBreak.NotApplicable,
                    ClockBasis = CoachScopeClockBasis.ServerUtcInstant,
                    ReferenceMode = CoachScopeReferenceMode.AsOfInstant
                });
        }
        catch (CoachToolException) { throw; }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { throw DataAccessFailure(ex); }
    }
}
