using System.Security.Cryptography;
using System.Text;
using SentenceStudio.Contracts.Coach;
using SentenceStudio.Services.PlanGeneration;
using SentenceStudio.Services.Plans;

namespace SentenceStudio.Api.Coach.Tools;

/// <summary>
/// Builds a read-only plan preview for validated constraints.
/// The tool uses the pure preview path, so it performs no database write.
/// The answer holds counts and metadata only. It never holds a word or a translation.
/// </summary>
public sealed class PreviewPracticePlanTool : CoachToolBase
{
    private const int MaxTitleLength = 120;
    private const int MaxGoalTagLength = CoachConstraintLimits.MaxGoalTagLength;

    private readonly IDeterministicPlanGenerator _planner;
    private readonly ICoachPlanPreviewFailureAdapter _failureAdapter;
    private readonly IPlanDateContext _dates;

    public PreviewPracticePlanTool(
        IUserScopeProvider userScope,
        IDeterministicPlanGenerator planner,
        ICoachPlanPreviewFailureAdapter failureAdapter,
        IPlanDateContext dates)
        : base(userScope)
    {
        _planner = planner;
        _failureAdapter = failureAdapter;
        _dates = dates;
    }

    public override string ToolName => CoachToolNames.PreviewPracticePlan;

    /// <summary>Returns a plan preview. The preview changes nothing.</summary>
    public async Task<PlanPreviewSummary> PreviewAsync(
        CoachPlanPreviewArguments arguments,
        CancellationToken ct = default)
    {
        var userProfileId = RequireUserProfileId();

        ArgumentNullException.ThrowIfNull(arguments);
        var constraints = ToPlanConstraints(arguments);

        if (!constraints.TryValidate(out var errors))
        {
            throw InvalidArgument(string.Join(" ", errors));
        }

        PlanSkeleton? skeleton;
        try
        {
            skeleton = await _planner.GenerateAsync(
                PlanBuildRequest.Preview(userProfileId, constraints), ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new CoachToolException(
                CoachToolFailureKind.Unauthorized, ToolName, "The request has no user scope.", ex);
        }
        catch (Exception ex)
        {
            throw DataAccessFailure(ex);
        }

        if (skeleton is null || skeleton.Activities.Count == 0)
        {
            throw _failureAdapter.Describe(constraints, skeleton);
        }

        return ToSummary(skeleton);
    }

    /// <summary>Maps the tool arguments onto the planner's own constraint type.</summary>
    internal PlanConstraints ToPlanConstraints(CoachPlanPreviewArguments arguments)
    {
        var goalTag = SanitizeMetadata(arguments.GoalTag, MaxGoalTagLength);

        if (arguments.SkillEmphasis is { } emphasis && !Enum.IsDefined(emphasis))
        {
            throw InvalidArgument("The skill emphasis is not one of the allowed skills.");
        }

        if (arguments.EnergyLevel is { } level && !Enum.IsDefined(level))
        {
            throw InvalidArgument("The energy level is not one of the allowed levels.");
        }

        // Every optional flag is nullable on the wire, because a model answers a closed
        // schema with explicit nulls. Null and missing both mean "the learner did not say",
        // which resolves to the permissive default here so no null reaches PlanConstraints.
        return new PlanConstraints
        {
            AvailableMinutes = arguments.AvailableMinutes,
            AudioAllowed = arguments.AudioAllowed ?? CoachPlanPreviewArguments.DefaultModalityAllowed,
            SpeechAllowed = arguments.SpeechAllowed ?? CoachPlanPreviewArguments.DefaultModalityAllowed,
            TypingAllowed = arguments.TypingAllowed ?? CoachPlanPreviewArguments.DefaultModalityAllowed,
            SkillEmphasis = arguments.SkillEmphasis switch
            {
                CoachSkillEmphasis.Listening => PlanSkillEmphasis.Listening,
                CoachSkillEmphasis.Speaking => PlanSkillEmphasis.Speaking,
                CoachSkillEmphasis.Reading => PlanSkillEmphasis.Reading,
                CoachSkillEmphasis.Writing => PlanSkillEmphasis.Writing,
                CoachSkillEmphasis.Vocabulary => PlanSkillEmphasis.Vocabulary,
                _ => null
            },
            GoalTag = goalTag.Length == 0 ? null : goalTag,
            GoalHorizonDays = arguments.GoalHorizonDays,
            EnergyLevel = (arguments.EnergyLevel ?? CoachPlanPreviewArguments.DefaultEnergyLevel) == CoachEnergyLevel.Low
                ? PlanEnergyLevel.Low
                : PlanEnergyLevel.Normal
        };
    }

    private PlanPreviewSummary ToSummary(PlanSkeleton skeleton)
    {
        var items = skeleton.Activities
            .OrderBy(a => a.Priority)
            .Select(a => new PlanPreviewItem(
                ActivityType: SanitizeMetadata(a.ActivityType, 40),
                EstimatedMinutes: a.EstimatedMinutes,
                Priority: a.Priority,
                ResourceId: NullIfEmpty(a.ResourceId),
                ResourceTitle: string.Equals(a.ResourceId, skeleton.PrimaryResource?.Id, StringComparison.Ordinal)
                    ? NullIfEmpty(SanitizeMetadata(skeleton.PrimaryResource?.Title, MaxTitleLength))
                    : null,
                SkillId: NullIfEmpty(a.SkillId),
                FocusWordCount: a.FocusVocabularyIds.Count))
            .ToList();

        return new PlanPreviewSummary(
            PreviewId: ComputePreviewId(items),
            TotalMinutes: skeleton.TotalMinutes,
            Items: items,
            VocabularyReviewWordCount: skeleton.VocabularyReview?.WordCount ?? 0,
            TotalDueCount: skeleton.VocabularyReview?.TotalDue ?? 0,
            PrimaryResourceTitle: NullIfEmpty(SanitizeMetadata(skeleton.PrimaryResource?.Title, MaxTitleLength)),
            PrimaryResourceId: NullIfEmpty(skeleton.PrimaryResource?.Id),
            Scope: new CoachResultScope
            {
                // Not a set of the learner's rows: a plan the planner computed from them and did
                // not save. Calling it a complete owned set would invite the model to describe it
                // as something the learner already has.
                Coverage = CoachScopeCoverage.DerivedProjection,
                Order = CoachScopeOrder.PriorityAscending,
                OrderHonored = true,
                Filters = CoachScopeFilters.OwnerScoped,
                AsOfUtc = _dates.UtcNow,
                WindowStartDate = _dates.UserLocalDate,
                WindowEndDate = _dates.UserLocalDate,
                ReturnedCount = items.Count,
                DefinitionCode = CoachScopeDefinition.DeterministicPlanPreview,
                MinimumEvidence = CoachScopeMinimumEvidence.None,
                TieBreak = CoachScopeTieBreak.None,
                ClockBasis = CoachScopeClockBasis.LearnerLocalDay,
                ReferenceMode = CoachScopeReferenceMode.CalendarDay
            });
    }

    /// <summary>
    /// Builds a stable identifier for the preview content.
    /// The same preview always gets the same identifier.
    /// </summary>
    private static string ComputePreviewId(IReadOnlyList<PlanPreviewItem> items)
    {
        var builder = new StringBuilder();
        foreach (var item in items)
        {
            builder.Append(item.ActivityType).Append('|')
                .Append(item.EstimatedMinutes).Append('|')
                .Append(item.Priority).Append('|')
                .Append(item.ResourceId ?? "-").Append('|')
                .Append(item.SkillId ?? "-").Append('|')
                .Append(item.FocusWordCount).Append(';');
        }

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()));
        return "preview-" + Convert.ToHexString(hash)[..16].ToLowerInvariant();
    }

    private static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}
