using System.ComponentModel;
using System.Globalization;
using SentenceStudio.Api.Coach.Tools;
using SentenceStudio.Services.Plans;

namespace SentenceStudio.Api.Coach.Operations.Handlers;

/// <summary>Arguments for <c>propose_plan_regeneration</c>. The model supplies none.</summary>
/// <remarks>
/// Both members are filled in by <see cref="CoachPlanRegenerationHandler"/> when it prepares the
/// proposal; whatever the model sends is discarded. They are what makes one request distinct
/// from the next (the ledger recognises a repeated proposal by its arguments, and a
/// regeneration with no arguments at all would be "already done" for the rest of the
/// conversation after the first one), and what the handler checks an acceptance against.
/// </remarks>
public sealed record CoachPlanRegenerationArgs(
    [property: Description("Leave empty. The application fills in the day the plan is for.")]
    string? PlanDate = null,
    [property: Description("Leave empty. The application fills in which plan would be replaced.")]
    string? Replaces = null);

/// <summary>
/// Makes a new plan for today, replacing the one the learner has: what Regenerate Plan on the
/// dashboard does, offered by Sam and carried out when the learner accepts.
/// </summary>
/// <remarks>
/// <para>
/// Issue 227. A learner asked Sam to create a new plan for today and Sam had no way to: the only
/// plan writes it had were changes to the plan's constraints. The dashboard has always let the
/// learner do this in one tap, so this is the same action behind the same kind of approval every
/// other change of Sam's uses.
/// </para>
/// <para>
/// Not reversible, and the card says so (the clients add that line to every change that cannot
/// be undone). The plan it replaces is archived rather than deleted, so the
/// practice already logged stays in the learner's history, but nothing puts the old plan back on
/// the dashboard. The dashboard's own control offers no undo either.
/// </para>
/// </remarks>
public sealed class CoachPlanRegenerationHandler : CoachWriteHandlerBase<CoachPlanRegenerationArgs>
{
    private const string NoPlanYet = "none";

    private readonly IPlanService _plans;
    private readonly IPlanDateContext _dates;

    public CoachPlanRegenerationHandler(IPlanService plans, IPlanDateContext dates)
    {
        _plans = plans;
        _dates = dates;
    }

    public override string ToolName => CoachToolNames.ProposePlanRegeneration;
    public override CoachToolRiskClass RiskClass => CoachToolRiskClass.WriteSoft;
    public override CoachWriteEntityKind EntityKind => CoachWriteEntityKind.DailyPlan;

    protected override async Task<CoachWritePreview> PrepareAsync(
        string userProfileId, CoachPlanRegenerationArgs args, CancellationToken cancellationToken)
    {
        // Asked before anything is offered: a proposal the learner accepts and that then cannot
        // be carried out is worse than being told now.
        var preview = await _plans.PreviewPlanAsync(constraints: null, cancellationToken).ConfigureAwait(false);
        if (!preview.IsSuccess)
        {
            throw InvalidArgument(
                "No plan can be built yet. A plan needs a learning resource or some vocabulary to draw on.");
        }

        var current = await _plans.GetTodayAsync(cancellationToken).ConfigureAwait(false);
        var canonical = Canonical(new CoachPlanRegenerationArgs(
            DayKey(_dates.UserLocalDate),
            await PlanStateKeyAsync(current, cancellationToken).ConfigureAwait(false)));

        if (current is null || current.TotalCount == 0)
        {
            return new CoachWritePreview(
                "Make a plan for today",
                new[] { "You have no plan for today yet. This makes one and puts it on your dashboard." },
                EntityId: null,
                canonical);
        }

        return new CoachWritePreview(
            "Replace today’s plan with a new one",
            new[]
            {
                $"Your plan now: {current.CompletedCount} of {current.TotalCount} activities done.",
                // That it cannot be undone is said by the card itself, for every change that cannot.
                "The new plan starts with nothing done. The practice you already logged stays in your history."
            },
            EntityId: null,
            canonical);
    }

    protected override async Task<CoachWriteExecution> ExecuteAsync(
        string userProfileId, CoachPlanRegenerationArgs args, CancellationToken cancellationToken)
    {
        // The offer was for a day. Accepted after midnight it would replace a different day's
        // plan than the one the learner was shown.
        if (!string.Equals(args.PlanDate, DayKey(_dates.UserLocalDate), StringComparison.Ordinal))
        {
            throw InvalidArgument("That offer was for a different day. Ask again for a new plan.");
        }

        // And for one plan. The card said how much of it was done; if the learner has practised
        // since, or regenerated on the dashboard, accepting would throw away something they were
        // not shown, and there is no undo. They are asked to ask again.
        var current = await _plans.GetTodayAsync(cancellationToken).ConfigureAwait(false);
        if (!string.Equals(
                args.Replaces,
                await PlanStateKeyAsync(current, cancellationToken).ConfigureAwait(false),
                StringComparison.Ordinal))
        {
            throw InvalidArgument("Today\u2019s plan has changed since this was offered. Ask again for a new plan.");
        }

        var plan = await _plans.RegenerateTodayAsync(cancellationToken).ConfigureAwait(false);
        if (plan is null)
        {
            throw InvalidArgument("No plan could be built, so today’s plan was left as it was.");
        }

        return new CoachWriteExecution(
            "Made a new plan for today",
            new[]
            {
                $"{plan.TotalCount} {(plan.TotalCount == 1 ? "activity" : "activities")}, about {plan.EstimatedTotalMinutes} minutes.",
                "It is on your dashboard."
            },
            EntityId: null,
            PriorStateJson: null);
    }

    private static string DayKey(DateOnly day) => day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>
    /// Names the plan an offer is about: when it was generated, and the version of its items and
    /// their progress. A dashboard regeneration changes the first; practice changes the second.
    /// </summary>
    private async Task<string> PlanStateKeyAsync(
        Contracts.Plans.TodaysPlanDto? current, CancellationToken cancellationToken)
    {
        if (current is null)
        {
            return NoPlanYet;
        }

        var snapshot = await _plans.GetTodaySnapshotAsync(cancellationToken).ConfigureAwait(false);
        return current.GeneratedAtUtc.ToString("O", CultureInfo.InvariantCulture) + "|" + snapshot.Version;
    }
}
