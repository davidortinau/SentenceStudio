using SentenceStudio.Api.Coach.Agents;
using SentenceStudio.Api.Coach.Tools;

namespace SentenceStudio.Api.Tests.Coach;

/// <summary>
/// Prompt-level contract tests for learner-state query routing.
/// </summary>
/// <remarks>
/// <para>
/// RCA: river-2026-08-25-sam-latest-study-live-rca.md.
/// The model failed to call <c>get_practice_history_summary</c> for "When was the last time
/// I studied?" because (1) the instructions didn't route that query to the tool, (2)
/// PedagogicalAnswer scope didn't cover practice history, and (3) the "Reading facts" section
/// sat inside the plan block, biasing toward plan mutation.
/// </para>
/// <para>
/// These tests pin the instruction-level fix so the same gaps cannot reappear.
/// </para>
/// </remarks>
public class CoachLearnerStateRoutingTests
{
    // ── AgentDescription is no longer plan-only ──────────────────────────────

    [Fact]
    public void AgentDescription_MentionsLanguageQuestions()
    {
        CoachInstructions.AgentDescription.Should().Contain("language",
            "the agent's identity must include answering language questions, not only plan mutation");
    }

    [Fact]
    public void AgentDescription_MentionsLearnerState()
    {
        CoachInstructions.AgentDescription.Should().Contain("learner-state",
            "the agent's identity must include grounded learner-state reads");
    }

    [Fact]
    public void AgentDescription_StillMentionsPlanConstraints()
    {
        CoachInstructions.AgentDescription.Should().Contain("Plan constraints",
            "plan adjustment is still a capability");
    }

    [Fact]
    public void AgentDescription_DoesNotMarketUnsupportedCapabilities()
    {
        var desc = CoachInstructions.AgentDescription;
        desc.Should().NotContain("diary", "the coach has no diary access");
        desc.Should().NotContain("transcript", "the coach has no conversation transcript access");
        desc.Should().NotContain("grade", "the coach does not grade");
        desc.Should().NotContain("score", "the coach does not score");
    }

    // ── PedagogicalAnswer scope covers practice history ──────────────────────

    [Fact]
    public void PedagogicalAnswer_ScopeIncludesPracticeHistory()
    {
        CoachInstructions.Instructions.Should().Contain("practice history",
            "PedagogicalAnswer scope must explicitly cover practice-history questions");
    }

    [Fact]
    public void PedagogicalAnswer_ScopeIncludesStudyPatterns()
    {
        CoachInstructions.Instructions.Should().Contain("study patterns",
            "PedagogicalAnswer scope must explicitly cover study-pattern questions");
    }

    // ── Explicit tool routing for practice history ───────────────────────────

    [Fact]
    public void Instructions_NameGetPracticeHistorySummaryTool()
    {
        CoachInstructions.Instructions.Should().Contain(CoachToolNames.GetPracticeHistorySummary,
            "the instructions must explicitly name the tool so the model has a routing signal");
    }

    [Fact]
    public void Instructions_RouteLastStudiedToTool()
    {
        var instructions = CoachInstructions.Instructions;
        // The routing instruction must connect "when they last studied" to the tool
        instructions.Should().Contain("last studied or practised",
            "the routing instruction must cover 'when did I last study/practise' queries");
        instructions.Should().Contain("get_practice_history_summary first",
            "the routing instruction must require calling the tool before answering");
    }

    [Fact]
    public void Instructions_ProhibitGuessingDateOrDays()
    {
        CoachInstructions.Instructions.Should().Contain("Do not guess a date or a number of days",
            "the model must never fabricate a timestamp or day count");
    }

    [Fact]
    public void Instructions_ProhibitRevealingVocabularyFromToolResult()
    {
        CoachInstructions.Instructions.Should().Contain(
            "Do not reveal vocabulary terms, content, or hidden",
            "privacy: tool results must not leak vocabulary content");
    }

    // ── Reading facts is standalone, not plan-adjacent ────────────────────────

    [Fact]
    public void ReadingFacts_IsASeparateSection()
    {
        CoachInstructions.Instructions.Should().Contain("READING FACTS ABOUT THE LEARNER",
            "reading facts must be a standalone section heading, not buried in the plan block");
    }

    [Fact]
    public void ReadingFacts_StatesNoImpliedPlanChange()
    {
        CoachInstructions.Instructions.Should().Contain(
            "does not change the plan and does not imply a plan change",
            "the section must explicitly de-associate reads from plan mutation");
    }

    [Fact]
    public void ReadingFacts_AppearsBeforeAdjustingThePlan()
    {
        var instructions = CoachInstructions.Instructions;
        var readingIdx = instructions.IndexOf("READING FACTS ABOUT THE LEARNER", StringComparison.Ordinal);
        var adjustingIdx = instructions.IndexOf("ADJUSTING THE PLAN", StringComparison.Ordinal);

        readingIdx.Should().BeGreaterThan(-1);
        adjustingIdx.Should().BeGreaterThan(-1);
        readingIdx.Should().BeLessThan(adjustingIdx,
            "the reading-facts section must appear before the plan section to avoid plan bias");
    }

    // ── Challenge/dispute follow-up re-read instruction ──────────────────────

    [Fact]
    public void Instructions_RequireReReadOnDispute()
    {
        CoachInstructions.Instructions.Should().Contain(
            "disputes or corrects your answer about their practice history",
            "a learner challenging a practice-history answer must trigger a re-read");
    }

    [Fact]
    public void Instructions_ProhibitDisputeRoutingToPlanChange()
    {
        CoachInstructions.Instructions.Should().Contain(
            "Never route a dispute about",
            "a dispute about learner state must not be misrouted to plan mutation");
        CoachInstructions.Instructions.Should().Contain(
            "a factual learner-state answer to a plan change",
            "the prohibition must name what the dispute must not be routed to");
    }

    [Fact]
    public void Instructions_ProhibitDisputeRoutingToNoChangeFallback()
    {
        CoachInstructions.Instructions.Should().Contain(
            "or to a no-change fallback",
            "a dispute about learner state must not fall through to NoChange");
    }

    // ── Both arms receive identical instructions/description ─────────────────

    [Fact]
    public void BothArms_ReceiveIdenticalDescription()
    {
        // CoachHarnessOptionsFactory and CoachAgentFactory both read from CoachInstructions.
        // The parity test in CoachHarnessOptionsFactoryTests.TheAgentNameAndInstructionsMatchTheBaselineArm
        // asserts this at runtime. This test asserts the source is a single const.
        var description = CoachInstructions.AgentDescription;
        description.Should().NotBeNullOrWhiteSpace();

        // Verify the const is the shared source (compile-time guarantee).
        // Both factories reference CoachInstructions.AgentDescription directly.
        typeof(CoachInstructions).GetField(nameof(CoachInstructions.AgentDescription))!
            .IsLiteral.Should().BeTrue("AgentDescription must be a const so both arms get the same value at compile time");
    }

    [Fact]
    public void BothArms_ReceiveIdenticalInstructions()
    {
        typeof(CoachInstructions).GetField(nameof(CoachInstructions.Instructions))!
            .IsLiteral.Should().BeTrue("Instructions must be a const so both arms get the same value at compile time");
    }

    // ── No general weakening of validator ─────────────────────────────────────

    [Fact]
    public void Instructions_StillRequireAnswerWindow()
    {
        CoachInstructions.Instructions.Should().Contain(
            "must name its window",
            "the window requirement on stated facts is preserved");
    }

    [Fact]
    public void Instructions_StillProhibitDueWordLeaks()
    {
        CoachInstructions.Instructions.Should().Contain(
            "due review words",
            "the due-word privacy rule is preserved");
    }

    [Fact]
    public void Instructions_StillProhibitCitingSources()
    {
        CoachInstructions.Instructions.Should().Contain(
            "Never cite a source",
            "the source-citation prohibition is preserved");
    }

    [Fact]
    public void Instructions_StillRequireOneSuggestionAtATime()
    {
        CoachInstructions.Instructions.Should().Contain(
            "one open suggestion",
            "the suggestion cap is preserved");
    }

    [Fact]
    public void Instructions_StillDescribeProposalContract()
    {
        CoachInstructions.Instructions.Should().Contain(
            "They do not change anything",
            "the proposal contract is preserved");
    }
}
