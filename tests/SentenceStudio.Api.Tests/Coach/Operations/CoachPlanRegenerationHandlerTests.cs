using SentenceStudio.Api.Coach.Operations;
using SentenceStudio.Api.Coach.Operations.Handlers;
using SentenceStudio.Api.Coach.Persistence;
using SentenceStudio.Api.Coach.Tools;
using SentenceStudio.Contracts.Plans;
using SentenceStudio.Services.Plans;
using SentenceStudio.Services.Progress;

namespace SentenceStudio.Api.Tests.Coach.Operations;

/// <summary>
/// Sam makes a new plan for today when the learner asks for one (issue 227).
/// </summary>
/// <remarks>
/// The plan work itself is <c>PlanService.RegenerateTodayAsync</c>, tested against a real
/// database in <c>PlanServiceTests</c>. These hold what the handler adds: what the learner is
/// shown before they accept, that nothing is offered or done when no plan can be built, and that
/// each request is its own proposal.
/// </remarks>
public class CoachPlanRegenerationHandlerTests
{
    private const string Learner = "learner-1";
    private static readonly DateOnly Today = new(2026, 11, 16);

    [Fact]
    public async Task TheOffer_SaysWhatIsReplaced_AndWhatIsKept()
    {
        var plans = new FakePlanService(Today) { Today = Plan(total: 5, completed: 2, generatedAtHour: 8) };
        var handler = Handler(plans);

        var preview = await handler.PrepareAsync(Learner, "{}", CancellationToken.None);

        preview.Summary.Should().Be("Replace today’s plan with a new one");
        preview.Lines.Should().Contain("Your plan now: 2 of 5 activities done.");
        preview.Lines.Should().Contain(line => line.Contains("stays in your history", StringComparison.Ordinal));
        plans.RegenerateCallCount.Should().Be(0, "an offer changes nothing");
    }

    [Fact]
    public async Task WithNoPlanYet_TheOfferIsToMakeOne()
    {
        var handler = Handler(new FakePlanService(Today));

        var preview = await handler.PrepareAsync(Learner, "{}", CancellationToken.None);

        preview.Summary.Should().Be("Make a plan for today");
        preview.Lines.Should().ContainSingle().Which.Should().Contain("You have no plan for today yet.");
    }

    [Fact]
    public async Task WhenNoPlanCanBeBuilt_NothingIsOffered()
    {
        var plans = new FakePlanService(Today) { PreviewOutcome = PlanPreviewOutcome.NoFeasiblePlan };
        var handler = Handler(plans);

        var act = () => handler.PrepareAsync(Learner, "{}", CancellationToken.None);

        (await act.Should().ThrowAsync<CoachToolException>())
            .Which.Kind.Should().Be(CoachToolFailureKind.InvalidArgument);
    }

    [Fact]
    public async Task Accepted_ItRegenerates_AndTheReceiptDescribesTheNewPlan()
    {
        var plans = new FakePlanService(Today)
        {
            Today = Plan(total: 5, completed: 2, generatedAtHour: 8),
            NextRegenerated = Plan(total: 4, completed: 0, generatedAtHour: 15, minutes: 22)
        };
        var handler = Handler(plans);
        var offer = await handler.PrepareAsync(Learner, "{}", CancellationToken.None);

        var done = await handler.ExecuteAsync(Learner, offer.CanonicalArgumentsJson, CancellationToken.None);

        plans.RegenerateCallCount.Should().Be(1);
        done.Summary.Should().Be("Made a new plan for today");
        done.Lines.Should().Contain("4 activities, about 22 minutes.");
        done.PriorStateJson.Should().BeNull("the change is not reversible and must not look as if it were");
    }

    [Fact]
    public async Task Accepted_ButNoPlanCouldBeBuilt_ItFails_AndSaysThePlanWasLeft()
    {
        var plans = new FakePlanService(Today) { Today = Plan(total: 5, completed: 2, generatedAtHour: 8) };
        var handler = Handler(plans);
        var offer = await handler.PrepareAsync(Learner, "{}", CancellationToken.None);

        var act = () => handler.ExecuteAsync(Learner, offer.CanonicalArgumentsJson, CancellationToken.None);

        (await act.Should().ThrowAsync<CoachToolException>())
            .Which.Message.Should().Contain("left as it was");
    }

    /// <summary>
    /// An offer accepted after midnight would replace a different day's plan than the one the
    /// learner was shown.
    /// </summary>
    [Fact]
    public async Task AnOfferFromAnotherDay_IsRefused_AndRegeneratesNothing()
    {
        var plans = new FakePlanService(Today) { NextRegenerated = Plan(total: 4, completed: 0, generatedAtHour: 15) };
        var offer = await Handler(plans).PrepareAsync(Learner, "{}", CancellationToken.None);

        var tomorrow = Handler(plans, Today.AddDays(1));
        var act = () => tomorrow.ExecuteAsync(Learner, offer.CanonicalArgumentsJson, CancellationToken.None);

        await act.Should().ThrowAsync<CoachToolException>();
        plans.RegenerateCallCount.Should().Be(0);
    }

    /// <summary>
    /// The card said how much of the plan was done. If the learner practises before accepting,
    /// accepting would throw away work they were not shown, with no undo.
    /// </summary>
    [Fact]
    public async Task AnOfferForAPlanThatHasChangedSince_IsRefused_AndRegeneratesNothing()
    {
        var plans = new FakePlanService(Today)
        {
            Today = Plan(total: 3, completed: 1, generatedAtHour: 8),
            NextRegenerated = Plan(total: 3, completed: 0, generatedAtHour: 15)
        };
        var handler = Handler(plans);
        var offer = await handler.PrepareAsync(Learner, "{}", CancellationToken.None);

        plans.SetItems(
        [
            FakePlanService.Item("done-1", PlanActivityType.VocabularyReview, priority: 1, minutes: 5, spent: 5, completed: true),
            FakePlanService.Item("started-1", PlanActivityType.Reading, priority: 2, minutes: 8, spent: 8, completed: true),
            FakePlanService.Item("fresh-1", PlanActivityType.Listening, priority: 3, minutes: 7, spent: 0, completed: false)
        ]);
        var act = () => handler.ExecuteAsync(Learner, offer.CanonicalArgumentsJson, CancellationToken.None);

        (await act.Should().ThrowAsync<CoachToolException>())
            .Which.Message.Should().Contain("has changed since this was offered");
        plans.RegenerateCallCount.Should().Be(0);
    }

    /// <summary>
    /// The ledger recognises a repeated proposal by its arguments. A second request, after the
    /// first was carried out, has to be a new proposal and not "already done".
    /// </summary>
    [Fact]
    public async Task ASecondRequestAfterTheFirstWasCarriedOut_IsADifferentProposal()
    {
        var plans = new FakePlanService(Today)
        {
            Today = Plan(total: 5, completed: 2, generatedAtHour: 8),
            NextRegenerated = Plan(total: 5, completed: 0, generatedAtHour: 15)
        };
        var handler = Handler(plans);

        var first = await handler.PrepareAsync(Learner, "{}", CancellationToken.None);
        var again = await handler.PrepareAsync(Learner, "{}", CancellationToken.None);
        await handler.ExecuteAsync(Learner, first.CanonicalArgumentsJson, CancellationToken.None);
        var second = await handler.PrepareAsync(Learner, "{}", CancellationToken.None);

        again.CanonicalArgumentsJson.Should().Be(first.CanonicalArgumentsJson, "the same request twice is one proposal");
        second.CanonicalArgumentsJson.Should().NotBe(first.CanonicalArgumentsJson);
    }

    /// <summary>What the model sends is ignored: the application says which day and which plan.</summary>
    [Fact]
    public async Task ArgumentsFromTheModel_AreReplacedByTheApplications()
    {
        var plans = new FakePlanService(Today) { Today = Plan(total: 5, completed: 2, generatedAtHour: 8) };
        var handler = Handler(plans);

        var plain = await handler.PrepareAsync(Learner, "{}", CancellationToken.None);
        var forged = await handler.PrepareAsync(
            Learner,
            CoachNormalizedJson.Serialize(new CoachPlanRegenerationArgs("1999-01-01", "something else")),
            CancellationToken.None);

        forged.CanonicalArgumentsJson.Should().Be(plain.CanonicalArgumentsJson);
    }

    [Fact]
    public async Task WithNoOwner_ItRefuses()
    {
        var plans = new FakePlanService(Today) { NextRegenerated = Plan(total: 4, completed: 0, generatedAtHour: 15) };
        var handler = Handler(plans);

        var act = () => handler.ExecuteAsync(" ", "{}", CancellationToken.None);

        (await act.Should().ThrowAsync<CoachToolException>())
            .Which.Kind.Should().Be(CoachToolFailureKind.Unauthorized);
        plans.RegenerateCallCount.Should().Be(0);
    }

    [Fact]
    public void ItIsAnOrdinaryChangeToTheLearnersPlan_AcceptedInOneStep()
    {
        var handler = Handler(new FakePlanService(Today));

        handler.ToolName.Should().Be(CoachToolNames.ProposePlanRegeneration);
        handler.RiskClass.Should().Be(CoachToolRiskClass.WriteSoft);
        handler.UndoKind.Should().Be(CoachWriteUndoKind.None);
        handler.EntityKind.Should().Be(CoachWriteEntityKind.DailyPlan);
    }

    private static CoachPlanRegenerationHandler Handler(FakePlanService plans, DateOnly? today = null) =>
        new(plans, new FakePlanDateContext(today ?? Today));

    private static TodaysPlanDto Plan(int total, int completed, int generatedAtHour, int minutes = 30) => new()
    {
        GeneratedForDate = Today,
        GeneratedAtUtc = new DateTime(2026, 11, 16, generatedAtHour, 0, 0, DateTimeKind.Utc),
        Strategy = "deterministic",
        Items = [],
        EstimatedTotalMinutes = minutes,
        CompletedCount = completed,
        TotalCount = total,
        CompletionPercentage = total == 0 ? 0 : completed * 100.0 / total,
        Streak = new StreakDto { CurrentStreak = 0, LongestStreak = 0 }
    };
}
