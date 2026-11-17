using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using SentenceStudio.Api.Coach.Operations;
using SentenceStudio.Api.Coach.Operations.Handlers;
using SentenceStudio.Api.Coach.Persistence;
using SentenceStudio.Api.Coach.Tools;
using SentenceStudio.Api.Tests.Coach.Postgres;
using SentenceStudio.Contracts.Plans;
using SentenceStudio.Services.Progress;

namespace SentenceStudio.Api.Tests.Coach.Operations;

/// <summary>
/// Sam's new plan for today through the real approval ledger on PostgreSQL (issue 227): offered,
/// accepted, refused when the plan changed in between, and asked for again.
/// </summary>
/// <remarks>
/// The plan work is <c>PlanService.RegenerateTodayAsync</c>, tested on a database of its own in
/// <c>PlanServiceTests</c>; here a fake plan service counts what the ledger asks of it.
/// </remarks>
public sealed class CoachPlanRegenerationLedgerPostgresTests : IAsyncLifetime
{
    private const string Owner = "user-owner";
    private const string Conversation = "conv-plan";
    private static readonly DateOnly Today = new(2026, 11, 16);

    private CoachPostgresHarness _harness = null!;

    public async Task InitializeAsync()
    {
        if (CoachPostgresServer.SkipReason is not null)
        {
            return;
        }

        _harness = await CoachPostgresHarness.CreateAsync("plan-regeneration");

        await using var db = _harness.NewContext();
        var now = _harness.Time.GetUtcNow().UtcDateTime;
        db.CoachConversations.Add(new SentenceStudio.Api.Coach.Persistence.History.CoachConversation
        {
            Id = Conversation,
            UserProfileId = Owner,
            ProtectedTitle = "seeded",
            HistoryStartsAt = now,
            ContentProtectionVersion = _harness.ContentProtector.CurrentVersion,
            CreatedAt = now,
            UpdatedAt = now
        });
        await db.SaveChangesAsync();
    }

    public async Task DisposeAsync()
    {
        if (_harness is not null)
        {
            await _harness.DisposeAsync();
        }
    }

    [PostgresFact]
    public async Task Offered_it_changes_nothing_and_accepted_it_makes_the_new_plan_once()
    {
        var plans = Plans();
        var ledger = NewLedger(plans);

        var proposal = await ledger.ProposeAsync(Conversation, "turn-1", CoachToolNames.ProposePlanRegeneration, "{}");

        proposal.ApprovalMode.Should().Be(CoachWriteApprovalModes.Accept, "one step, as on the dashboard");
        proposal.Summary.Should().Be("Replace today’s plan with a new one");
        plans.RegenerateCallCount.Should().Be(0, "an offer changes nothing");

        var receipt = await ledger.AcceptAsync(Conversation, proposal.OperationId);

        receipt.Status.Should().Be(CoachWriteOperationStatus.Executed);
        receipt.Summary.Should().Be("Made a new plan for today");
        receipt.CanUndo.Should().BeFalse();
        receipt.EntityKind.Should().Be(CoachWriteEntityKind.DailyPlan);
        plans.RegenerateCallCount.Should().Be(1);

        var again = await ledger.AcceptAsync(Conversation, proposal.OperationId);
        again.Status.Should().Be(CoachWriteOperationStatus.Executed);
        plans.RegenerateCallCount.Should().Be(1, "a second press replays the receipt");
    }

    [PostgresFact]
    public async Task The_same_request_twice_before_accepting_is_one_offer()
    {
        var ledger = NewLedger(Plans());

        var first = await ledger.ProposeAsync(Conversation, "turn-1", CoachToolNames.ProposePlanRegeneration, "{}");
        var second = await ledger.ProposeAsync(Conversation, "turn-2", CoachToolNames.ProposePlanRegeneration, "{}");

        second.OperationId.Should().Be(first.OperationId);
        second.IsDuplicate.Should().BeTrue();
    }

    /// <summary>
    /// The learner practised between the offer and the tap. The acceptance is refused and nothing
    /// is replaced; asking again makes a fresh offer for the plan as it is now.
    /// </summary>
    [PostgresFact]
    public async Task Accepted_after_the_plan_changed_it_fails_and_a_new_request_is_a_new_offer()
    {
        var plans = Plans();
        var ledger = NewLedger(plans);
        var proposal = await ledger.ProposeAsync(Conversation, "turn-1", CoachToolNames.ProposePlanRegeneration, "{}");

        plans.SetItems(
        [
            FakePlanService.Item("done-1", PlanActivityType.VocabularyReview, priority: 1, minutes: 5, spent: 5, completed: true),
            FakePlanService.Item("started-1", PlanActivityType.Reading, priority: 2, minutes: 8, spent: 8, completed: true),
            FakePlanService.Item("fresh-1", PlanActivityType.Listening, priority: 3, minutes: 7, spent: 0, completed: false)
        ]);

        var accept = () => ledger.AcceptAsync(Conversation, proposal.OperationId);

        await accept.Should().ThrowAsync<CoachToolException>();
        plans.RegenerateCallCount.Should().Be(0);
        await using (var db = _harness.NewContext())
        {
            (await db.CoachWriteOperations.SingleAsync(o => o.Id == proposal.OperationId)).Status
                .Should().Be(CoachWriteOperationStatus.Failed);
        }

        var fresh = await ledger.ProposeAsync(Conversation, "turn-2", CoachToolNames.ProposePlanRegeneration, "{}");
        fresh.OperationId.Should().NotBe(proposal.OperationId);
        fresh.IsDuplicate.Should().BeFalse();
        (await ledger.AcceptAsync(Conversation, fresh.OperationId)).Status.Should().Be(CoachWriteOperationStatus.Executed);
        plans.RegenerateCallCount.Should().Be(1);
    }

    private static FakePlanService Plans() => new(Today)
    {
        Today = Plan(total: 3, completed: 1, generatedAtHour: 8),
        NextRegenerated = Plan(total: 3, completed: 0, generatedAtHour: 15)
    };

    private CoachWriteOperationService NewLedger(FakePlanService plans) =>
        CoachWriteTestScope.NewLedger(
            _harness.NewContext(),
            _harness.ContentProtector,
            [new CoachPlanRegenerationHandler(plans, new FakePlanDateContext(Today))],
            new FakeUserScope(Owner),
            _harness.Time);

    private static TodaysPlanDto Plan(int total, int completed, int generatedAtHour) => new()
    {
        GeneratedForDate = Today,
        GeneratedAtUtc = new DateTime(2026, 11, 16, generatedAtHour, 0, 0, DateTimeKind.Utc),
        Strategy = "deterministic",
        Items = [],
        EstimatedTotalMinutes = 20,
        CompletedCount = completed,
        TotalCount = total,
        CompletionPercentage = total == 0 ? 0 : completed * 100.0 / total,
        Streak = new StreakDto { CurrentStreak = 0, LongestStreak = 0 }
    };
}
