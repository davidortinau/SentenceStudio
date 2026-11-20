using FluentAssertions;
using SentenceStudio.Api.Coach.Application;

namespace SentenceStudio.Api.Tests.Coach;

/// <summary>
/// With Today's Plan on screen, a study request is about the plan without naming it, the way a
/// button on that screen would be (Captain, 2026-11-17). Off the plan screen nothing changes.
/// </summary>
public class CoachPlanOnScreenRoutingTests
{
    private readonly CoachWriteAuthority _authority = new();

    [Theory]
    [InlineData("I have 45 minutes today. expand the current plan to suit that.")]
    [InlineData("I have been doing the current plan time for a while now, so let's increase to 45 minutes from now on.")]
    [InlineData("I want to study food and restaurant content.")]
    [InlineData("I'd prefer to study German today.")]
    [InlineData("Today I want to study travel terms for things like using the bus, the subway, walking the street, and taxi.")]
    [InlineData("I only have 20 minutes today")]
    [InlineData("오늘은 30분만 공부할게")]
    [InlineData("let's do 30 minutes of reading")]
    [InlineData("음식 단어 연습하고 싶어")]
    public void A_study_request_with_the_plan_on_screen_is_a_plan_request(string text)
    {
        _authority.ClassifyPlanRouting(text, planOnScreen: true)
            .Should().Be(CoachWriteAuthority.PlanRoutingDecision.ExplicitPlanRequest);
    }

    [Theory]
    [InlineData("I want to study food and restaurant content.")]
    [InlineData("I'd prefer to study German today.")]
    [InlineData("I only have 20 minutes today")]
    public void The_same_words_off_the_plan_screen_name_no_plan(string text)
    {
        _authority.ClassifyPlanRouting(text)
            .Should().Be(CoachWriteAuthority.PlanRoutingDecision.NoPlanReference);
    }

    [Theory]
    [InlineData("What should I study today?")]
    [InlineData("How long should I practice each day")]
    [InlineData("Start a vocabulary review activity with words about food")]
    [InlineData("Add 생선 to my vocabulary")]
    [InlineData("Give me example sentences for this")]
    [InlineData("Maybe I will study German someday")]
    public void Questions_launches_and_other_requests_stay_off_the_plan(string text)
    {
        _authority.ClassifyPlanRouting(text, planOnScreen: true)
            .Should().NotBe(CoachWriteAuthority.PlanRoutingDecision.ExplicitPlanRequest);
    }

    [Theory]
    [InlineData("Don't change anything, I only have 20 minutes today")]
    [InlineData("I don't want to study grammar today")]
    public void A_negated_request_on_the_plan_screen_changes_nothing(string text)
    {
        _authority.ClassifyPlanRouting(text, planOnScreen: true)
            .Should().Be(CoachWriteAuthority.PlanRoutingDecision.NegatedPlanAction);
    }

    [Theory]
    [InlineData("Expand the current plan to 45 minutes")]
    [InlineData("Increase this plan to 30 minutes")]
    public void The_current_plan_names_todays_plan_only_when_it_is_on_screen(string text)
    {
        _authority.ClassifyPlanRouting(text, planOnScreen: true)
            .Should().Be(CoachWriteAuthority.PlanRoutingDecision.ExplicitPlanRequest);
        _authority.ClassifyPlanRouting(text)
            .Should().Be(CoachWriteAuthority.PlanRoutingDecision.NoPlanReference,
                "off the dashboard 'the plan' could be a trip or a vocabulary set");
    }

    [Theory]
    [InlineData("I practiced for 30 minutes today")]
    [InlineData("I have a test in 2 hours")]
    [InlineData("I studied grammar for an hour yesterday")]
    [InlineData("Change the plan for tomorrow")]
    [InlineData("I want to study German tomorrow")]
    public void Statements_and_other_days_are_not_requests_for_the_plan_on_screen(string text)
    {
        _authority.ClassifyPlanRouting(text, planOnScreen: true)
            .Should().NotBe(CoachWriteAuthority.PlanRoutingDecision.ExplicitPlanRequest);
        _authority.Evaluate(text, planOnScreen: true)
            .Should().NotBe(CoachWriteAuthority.Denial.None, "nothing here may change the plan directly");
    }

    [Theory]
    [InlineData("Give me a preview of the vocabulary for today.", true)]
    [InlineData("The plan says I have 36% mastery of the plan content. What vocab am I the worst at?", true)]
    [InlineData("Which words am I weakest at", true)]
    [InlineData("오늘 단어 보여줘", true)]
    [InlineData("How many words are in my plan?", false)]
    [InlineData("I have 45 minutes today", false)]
    public void Only_the_learners_own_words_ask_to_see_their_words(string text, bool asks)
    {
        _authority.IsWordListRequest(text).Should().Be(asks);
    }

    [Theory]
    [InlineData("What is the answer?", true)]
    [InlineData("What's the answer", true)]
    [InlineData("Just tell me the answer.", true)]
    [InlineData("What does this mean?", true)]
    [InlineData("How do you say it?", true)]
    [InlineData("정답 알려줘", true)]
    [InlineData("무슨 뜻이야?", true)]
    [InlineData("Can I get a hint?", false)]
    [InlineData("Help me with this one", false)]
    [InlineData("Help me, but don't tell me the answer.", false)]
    [InlineData("답 말고 힌트 줘", false)]
    [InlineData("I have 45 minutes today", false)]
    public void Only_the_learners_own_words_ask_for_the_answer(string text, bool asks)
    {
        _authority.IsAnswerRequest(text).Should().Be(asks);
    }

    [Fact]
    public void A_plain_command_on_the_plan_screen_may_apply_directly()
    {
        _authority.Evaluate("I only have 20 minutes today", planOnScreen: true)
            .Should().Be(CoachWriteAuthority.Denial.None);
        _authority.Evaluate("I only have 20 minutes today")
            .Should().Be(CoachWriteAuthority.Denial.NamesNoPlanChange);
    }
}
