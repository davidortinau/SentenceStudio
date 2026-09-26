using SentenceStudio.Api.Coach.Agents;
using SentenceStudio.Api.Coach.Application;
using SentenceStudio.Api.Coach.Persistence;
using SentenceStudio.Contracts.Coach;
using SentenceStudio.Contracts.Coach.Intent;

namespace SentenceStudio.Api.Tests.Coach;

/// <summary>
/// Product-policy containment at the final application boundary.
/// </summary>
/// <remarks>
/// Model-selected plan intents are untrusted routing hints. These tests pin the learner's current
/// message as the authority for entering a plan reducer, while preserving explicit plan requests
/// and deterministic decisions about an already-open suggestion.
/// </remarks>
public sealed class CoachProductPolicyContainmentTests
{
    public static IEnumerable<object[]> ReadOnlyKoreanPlanRequests =>
        new[]
        {
            "오늘 계획을 확인해줘",
            "오늘 계획을 보여줘",
            "오늘 계획을 설명해줘",
            "오늘 계획을 알려줘",
            "오늘 계획을 읽어줘"
        }.SelectMany(learnerText => new[]
        {
            new object[] { learnerText, CoachIntentKind.DirectConstraintChange },
            new object[] { learnerText, CoachIntentKind.SuggestConstraintChange }
        });

    public static IEnumerable<object[]> KoreanBoundedGrammarFalsePositives =>
        new[]
        {
            // A mutation-root noun is not a conjugated mutation predicate.
            "오늘 계획 변경사항을 확인해줘",
            "오늘 계획의 변경사항을 확인해주세요",
            "오늘 계획 변경사항을 수정해줘",
            // A later destination marked with 에 owns the action, not the earlier plan context.
            "오늘 계획을 메모에 추가해줘",
            "오늘 계획을 일정에 추가해주세요",
            // Generic 하다 accepts only the closed plan-value grammar.
            "오늘 계획을 기준으로 해줘",
            "오늘 계획을 기준으로 해주세요",
            "오늘 계획을 지침으로 해줘",
            // A bare plan target accepts only the closed numeric-duration bridge.
            "오늘 계획 기준으로 바꿔줘",
            "오늘 계획 메모로 바꿔줘",
            "오늘 계획 일정으로 바꿔줘",
            // Read-only and negated forms never authorize a reducer.
            "오늘 계획을 확인해주세요",
            "오늘 계획을 보여주세요",
            "오늘 계획을 변경하지 말아줘",
            "오늘 계획은 바꾸지 마",
            "오늘 계획은 안 바꿔도 돼"
        }.SelectMany(learnerText => new[]
        {
            new object[] { learnerText, CoachIntentKind.DirectConstraintChange },
            new object[] { learnerText, CoachIntentKind.SuggestConstraintChange }
        });

    public static IEnumerable<object[]> InvalidKoreanNumericDurationBridges =>
        new[]
        {
            // Outside the authoritative available-minutes range.
            "오늘 계획 0분으로 바꿔줘",
            "오늘 계획 1분으로 바꿔줘",
            "오늘 계획 2분으로 바꿔줘",
            "오늘 계획 91분으로 바꿔줘",
            "오늘 계획 999분으로 바꿔줘",
            // Signs and non-canonical decimal forms must survive long enough to be rejected.
            "오늘 계획 +10분으로 바꿔줘",
            "오늘 계획 -10분으로 바꿔줘",
            "오늘 계획 + 10분으로 바꿔줘",
            "오늘 계획 - 10분으로 바꿔줘",
            "오늘 계획 10.5분으로 바꿔줘",
            "오늘 계획 10,5분으로 바꿔줘",
            "오늘 계획 010분으로 바꿔줘",
            // The numeric value and suffix are one exact ASCII-decimal eojol.
            "오늘 계획 10 분으로 바꿔줘",
            "오늘 계획 １０분으로 바꿔줘",
            "오늘 계획 ١٠분으로 바꿔줘",
            "오늘 계획 x10분으로 바꿔줘",
            "오늘 계획 10분으로x 바꿔줘",
            "오늘 계획 (10분으로) 바꿔줘",
            "오늘 계획 10분으로/바꿔줘",
            // Parsing is length-capped before conversion, and exactly one duration may appear.
            "오늘 계획 999999999999999999999999999999분으로 바꿔줘",
            "10분으로 오늘 계획 20분으로 바꿔줘",
            "오늘 계획 10분으로 20분으로 바꿔줘"
        }.SelectMany(learnerText => new[]
        {
            new object[] { learnerText, CoachIntentKind.DirectConstraintChange },
            new object[] { learnerText, CoachIntentKind.SuggestConstraintChange }
        });

    private const string GeneralVocabularyRequest =
        "I wnto study vocabulary about house rooms and things I'd see in a house.";

    private const string ActivityRequest =
        "start a vocabulary review activity with words about food";

    [Theory]
    [InlineData("Change Today's Plan to 10 minutes.")]
    [InlineData("Please change my plan to 10 minutes.")]
    [InlineData("Make Today's Plan 10 minutes so I can do pronunciation practice.")]
    [InlineData("오늘 계획을 10분으로 해줘")]
    [InlineData("오늘 계획이 10분으로 변경되게 해줘")]
    [InlineData("오늘 계획을 10분으로 줄여줘")]
    [InlineData("오늘 계획을 10분으로 바꿔")]
    [InlineData("오늘 계획 10분으로 바꿔주세요")]
    [InlineData("오늘 계획 변경해줘")]
    [InlineData("오늘 계획을 변경해줘")]
    [InlineData("오늘의 학습 계획을 10분으로 조정해줘")]
    [InlineData("오늘 계획을 어휘 중심으로 바꿔줘")]
    [InlineData("오늘 계획은 동사 위주로 바꿔줘")]
    public void Explicit_plan_action_grammar_recognizes_only_the_governing_construction(string learnerText)
    {
        var authority = new CoachWriteAuthority();

        authority.ClassifyPlanRouting(learnerText)
            .Should().Be(CoachWriteAuthority.PlanRoutingDecision.ExplicitPlanRequest);
        authority.Evaluate(learnerText).Should().Be(CoachWriteAuthority.Denial.None);
    }

    [Theory]
    [InlineData(3, CoachIntentKind.DirectConstraintChange)]
    [InlineData(3, CoachIntentKind.SuggestConstraintChange)]
    [InlineData(10, CoachIntentKind.DirectConstraintChange)]
    [InlineData(10, CoachIntentKind.SuggestConstraintChange)]
    [InlineData(90, CoachIntentKind.DirectConstraintChange)]
    [InlineData(90, CoachIntentKind.SuggestConstraintChange)]
    public async Task Canonical_korean_numeric_duration_boundaries_preserve_the_forced_effect_contract(
        int minutes,
        CoachIntentKind modelIntent)
    {
        var learnerText = $"오늘 계획 {minutes}분으로 바꿔줘";
        new CoachWriteAuthority().ClassifyPlanRouting(learnerText)
            .Should().Be(CoachWriteAuthority.PlanRoutingDecision.ExplicitPlanRequest);

        using var harness = new CoachApplicationHarness();
        var sessionId = await harness.StartSessionAsync();
        harness.Coach.NextResult = PlanResult(
            modelIntent,
            new CoachConstraintDeltaIntent { AvailableMinutes = minutes });

        var result = await SubmitAsync(harness, sessionId, learnerText);

        result.IsOk.Should().BeTrue(result.Detail);
        harness.PlanService.PreviewCallCount.Should().Be(1);

        if (modelIntent == CoachIntentKind.DirectConstraintChange)
        {
            result.Value!.ChangeReceipt.Should().NotBeNull();
            result.Value.PendingSuggestion.Should().BeNull();
            harness.PlanService.ApplyCallCount.Should().Be(1);
            harness.PlanService.LastAppliedConstraints!.AvailableMinutes.Should().Be(minutes);
            harness.Db.CoachPlanRevisions.Should().ContainSingle()
                .Which.Source.Should().Be(CoachRevisionSource.DirectRequest);
            return;
        }

        result.Value!.PendingSuggestion.Should().NotBeNull();
        result.Value.PendingSuggestion!.Delta.AvailableMinutes.Should().Be(minutes);
        result.Value.ChangeReceipt.Should().BeNull();
        harness.PlanService.ApplyCallCount.Should().Be(0);
        harness.Db.CoachPlanRevisions.Should().BeEmpty();
    }

    [Theory]
    [MemberData(nameof(InvalidKoreanNumericDurationBridges))]
    public async Task Invalid_korean_numeric_duration_bridges_cannot_authorize_a_forced_plan_effect(
        string learnerText,
        CoachIntentKind modelIntent)
    {
        new CoachWriteAuthority().ClassifyPlanRouting(learnerText)
            .Should().NotBe(CoachWriteAuthority.PlanRoutingDecision.ExplicitPlanRequest);

        using var harness = new CoachApplicationHarness();
        var sessionId = await harness.StartSessionAsync();
        harness.Coach.NextResult = PlanResult(
            modelIntent,
            new CoachConstraintDeltaIntent { AvailableMinutes = 10 },
            "Updated Today's Plan.");

        var result = await SubmitAsync(harness, sessionId, learnerText);

        result.IsOk.Should().BeTrue(result.Detail);
        result.Value!.Messages.Should().NotContain(
            message => message.Text.Contains("Updated", StringComparison.Ordinal));
        AssertNoPlanEffect(harness, result.Value);
    }

    [Theory]
    [InlineData(
        "Explain how Korean topic particles work",
        CoachAnswerTopic.Grammar,
        "Korean topic particles mark what the sentence is about.")]
    [InlineData(
        "How should I study pronunciation?",
        CoachAnswerTopic.StudyStrategy,
        "Practice one sound contrast at a time.")]
    public async Task Ordinary_teaching_and_study_advice_answer_without_a_plan_effect(
        string learnerText,
        CoachAnswerTopic topic,
        string answerText)
    {
        using var harness = new CoachApplicationHarness();
        var sessionId = await harness.StartSessionAsync();
        harness.Coach.NextResult = AnswerResult(topic, answerText);

        var result = await SubmitAsync(harness, sessionId, learnerText);

        result.IsOk.Should().BeTrue(result.Detail);
        result.Value!.Answer.Should().NotBeNull();
        result.Value.Answer!.PlainText.Should().Contain(answerText);
        AssertNoPlanEffect(harness, result.Value);
    }

    [Theory]
    [InlineData(CoachIntentKind.DirectConstraintChange)]
    [InlineData(CoachIntentKind.SuggestConstraintChange)]
    public async Task General_vocabulary_request_cannot_become_a_plan_effect(
        CoachIntentKind modelIntent)
    {
        using var harness = new CoachApplicationHarness();
        var sessionId = await harness.StartSessionAsync();
        harness.Coach.NextResult = PlanResult(
            modelIntent,
            new CoachConstraintDeltaIntent
            {
                SkillEmphasis = CoachSkillEmphasis.Vocabulary,
                VocabularyFocusDescription = "house rooms and things"
            });

        var result = await SubmitAsync(harness, sessionId, GeneralVocabularyRequest);

        AssertClarification(result, "studying that directly");
        AssertNoPlanEffect(harness, result.Value!);
        harness.FocusResolver.ResolveCount.Should().Be(
            0,
            "an implicit study request must not fall back to a part-of-speech focus");
    }

    [Theory]
    [InlineData(CoachIntentKind.DirectConstraintChange)]
    [InlineData(CoachIntentKind.SuggestConstraintChange)]
    public async Task Explicit_vocabulary_review_is_not_redirected_to_the_plan_when_model_selects_a_plan_kind(
        CoachIntentKind modelIntent)
    {
        using var harness = new CoachApplicationHarness();
        var sessionId = await harness.StartSessionAsync();
        harness.Coach.NextResult = PlanResult(
            modelIntent,
            new CoachConstraintDeltaIntent
            {
                SkillEmphasis = CoachSkillEmphasis.Vocabulary,
                VocabularyFocusDescription = "food words"
            });

        var result = await SubmitAsync(harness, sessionId, ActivityRequest);

        result.Value!.Status.Should().Be(CoachTurnStatus.Completed);
        result.Value.ClarifyingQuestion.Should().BeNull();
        result.Value.Messages.Should().NotContain(message =>
            message.Text.Contains("replace the vocabulary", StringComparison.OrdinalIgnoreCase));
        AssertNoPlanEffect(harness, result.Value);
        harness.FocusResolver.ResolveCount.Should().Be(
            0,
            "an activity request must not fall back to a part-of-speech focus");
    }

    [Theory]
    [InlineData("Teach me how Korean topic particles work", "studying that directly")]
    [InlineData("I need advice on studying pronunciation", "studying that directly")]
    [InlineData("start a listening activity now", "can't start activities")]
    public async Task Non_plan_learning_requests_are_contained_even_when_the_model_selects_a_plan_kind(
        string learnerText,
        string expectedClarification)
    {
        using var harness = new CoachApplicationHarness();
        var sessionId = await harness.StartSessionAsync();
        harness.Coach.NextResult = PlanResult(
            CoachIntentKind.DirectConstraintChange,
            new CoachConstraintDeltaIntent { AvailableMinutes = 12 });

        var result = await SubmitAsync(harness, sessionId, learnerText);

        AssertClarification(result, expectedClarification);
        AssertNoPlanEffect(harness, result.Value!);
    }

    [Fact]
    public async Task Negated_explicit_plan_action_has_no_effect_and_never_surfaces_model_success()
    {
        using var harness = new CoachApplicationHarness();
        var sessionId = await harness.StartSessionAsync();
        harness.Coach.NextResult = PlanResult(
            CoachIntentKind.DirectConstraintChange,
            new CoachConstraintDeltaIntent { AvailableMinutes = 12 },
            "Updated Today's Plan.");

        var result = await SubmitAsync(
            harness,
            sessionId,
            "Do not change Today's Plan; leave Today's Plan unchanged.");

        result.IsOk.Should().BeTrue();
        result.Value!.Status.Should().Be(CoachTurnStatus.Completed);
        result.Value.Messages.Should().ContainSingle()
            .Which.Text.Should().Be("I won't change Today's Plan.");
        result.Value.Messages.Should().NotContain(message => message.Text.Contains("Updated", StringComparison.Ordinal));
        AssertNoPlanEffect(harness, result.Value);
    }

    [Theory]
    [InlineData("Change my reminder, show Today's Plan.")]
    [InlineData("Change my reminder; show Today's Plan.")]
    [InlineData("Does Today's Plan include listening?")]
    [InlineData("I don't want you to change Today's Plan.")]
    [InlineData("You must not change Today's Plan.")]
    [InlineData("Do not change Today's Plan; explain it.")]
    [InlineData("Change my reminder so I can update Today's Plan later.")]
    [InlineData("Change my reminder to update Today's Plan.")]
    [InlineData("Review reminders before changing Today's Plan.")]
    [InlineData("Please explain how to change Today's Plan.")]
    [InlineData("How can I change Today's Plan?")]
    [InlineData("I might change Today's Plan later.")]
    [InlineData("오늘 계획을 바꾸지 마.")]
    [InlineData("오늘 계획은 변경하지 마.")]
    [InlineData("오늘 계획을 변경하지 말아줘.")]
    [InlineData("오늘 계획은 안 바꿔도 돼.")]
    [InlineData("오늘 계획을 바꾸고 싶지 않아.")]
    [InlineData("오늘 계획을 어떻게 바꿀 수 있어?")]
    public async Task Unrelated_actions_plan_queries_and_scoped_negation_cannot_authorize_a_plan_effect(
        string learnerText)
    {
        foreach (var modelIntent in new[]
                 {
                     CoachIntentKind.DirectConstraintChange,
                     CoachIntentKind.SuggestConstraintChange
                 })
        {
            using var harness = new CoachApplicationHarness();
            var sessionId = await harness.StartSessionAsync();
            harness.Coach.NextResult = PlanResult(
                modelIntent,
                new CoachConstraintDeltaIntent { AvailableMinutes = 12 },
                "Updated Today's Plan.");

            var result = await SubmitAsync(harness, sessionId, learnerText);

            result.IsOk.Should().BeTrue(result.Detail);
            result.Value!.Messages.Should().NotContain(
                message => message.Text.Contains("Updated", StringComparison.Ordinal));
            AssertNoPlanEffect(harness, result.Value);
        }
    }

    [Theory]
    [MemberData(nameof(ReadOnlyKoreanPlanRequests))]
    public async Task Read_only_korean_plan_requests_cannot_authorize_a_forced_plan_effect(
        string learnerText,
        CoachIntentKind modelIntent)
    {
        new CoachWriteAuthority().ClassifyPlanRouting(learnerText)
            .Should().Be(CoachWriteAuthority.PlanRoutingDecision.PlanMentionOnly);

        using var harness = new CoachApplicationHarness();
        var sessionId = await harness.StartSessionAsync();
        harness.Coach.NextResult = PlanResult(
            modelIntent,
            new CoachConstraintDeltaIntent { AvailableMinutes = 12 },
            "Updated Today's Plan.");

        var result = await SubmitAsync(harness, sessionId, learnerText);

        result.IsOk.Should().BeTrue(result.Detail);
        result.Value!.Messages.Should().NotContain(
            message => message.Kind == CoachMessageKind.Receipt
                || message.Kind == CoachMessageKind.Suggestion);
        AssertNoPlanEffect(harness, result.Value);
    }

    [Theory]
    [MemberData(nameof(KoreanBoundedGrammarFalsePositives))]
    public async Task Korean_tokens_outside_the_closed_mutation_grammar_have_no_plan_effect(
        string learnerText,
        CoachIntentKind modelIntent)
    {
        new CoachWriteAuthority().ClassifyPlanRouting(learnerText)
            .Should().NotBe(CoachWriteAuthority.PlanRoutingDecision.ExplicitPlanRequest);

        using var harness = new CoachApplicationHarness();
        var sessionId = await harness.StartSessionAsync();
        harness.Coach.NextResult = PlanResult(
            modelIntent,
            new CoachConstraintDeltaIntent { AvailableMinutes = 12 },
            "Updated Today's Plan.");

        var result = await SubmitAsync(harness, sessionId, learnerText);

        result.IsOk.Should().BeTrue(result.Detail);
        result.Value!.Messages.Should().NotContain(
            message => message.Text.Contains("Updated", StringComparison.Ordinal));
        AssertNoPlanEffect(harness, result.Value);
    }

    [Theory]
    [InlineData("오늘 계획을 보여주고 알림을 변경해줘")]
    [InlineData("오늘 계획을 확인하고 알림을 바꿔줘")]
    [InlineData("오늘 계획을 보여 준 뒤 목표를 수정해줘")]
    [InlineData("오늘 계획을 확인한 후 알림은 변경해줘")]
    [InlineData("오늘 계획에 맞춰 알림이 변경되게 해줘")]
    [InlineData("오늘 계획에 맞춰 알림을 변경해줘")]
    [InlineData("오늘 계획에 맞춰 알림은 변경되게 해줘")]
    [InlineData("오늘 계획에 맞춰 메모가 변경되게 해줘")]
    [InlineData("오늘 계획에 맞춰 메모를 변경해줘")]
    [InlineData("오늘 계획에 맞춰 메모는 변경되게 해줘")]
    [InlineData("오늘 계획에 맞춰 알림이 변경되지 않게 해줘")]
    [InlineData("오늘 계획에 맞춰 변경되게 해줘")]
    [InlineData("오늘 계획에 대해 알림을 변경해줘")]
    [InlineData("오늘 계획을 보여주고 알림을 10분으로 설정해줘")]
    [InlineData("오늘 계획을 보여주고 알림을 변경하지 말아줘")]
    [InlineData("오늘 계획을 변경하지 말고 알림을 바꿔줘")]
    [InlineData("오늘 계획 알림 변경해줘")]
    [InlineData("오늘 계획 메모 변경해줘")]
    [InlineData("오늘 계획 알림 바꿔줘")]
    [InlineData("오늘 계획 메모 수정해줘")]
    [InlineData("오늘 계획 목표 조정해줘")]
    [InlineData("오늘 계획 일정 변경해줘")]
    [InlineData("오늘 계획 숙제 변경해줘")]
    [InlineData("오늘 계획을 알림 변경해줘")]
    [InlineData("오늘 계획은 메모 수정해줘")]
    public async Task Coordinated_korean_non_plan_mutations_cannot_authorize_a_plan_effect(
        string learnerText)
    {
        new CoachWriteAuthority().ClassifyPlanRouting(learnerText)
            .Should().NotBe(CoachWriteAuthority.PlanRoutingDecision.ExplicitPlanRequest);

        foreach (var modelIntent in new[]
                 {
                     CoachIntentKind.DirectConstraintChange,
                     CoachIntentKind.SuggestConstraintChange
                 })
        {
            using var harness = new CoachApplicationHarness();
            var sessionId = await harness.StartSessionAsync();
            harness.Coach.NextResult = PlanResult(
                modelIntent,
                new CoachConstraintDeltaIntent { AvailableMinutes = 12 },
                "Updated Today's Plan.");

            var result = await SubmitAsync(harness, sessionId, learnerText);

            result.IsOk.Should().BeTrue(result.Detail);
            result.Value!.Messages.Should().NotContain(
                message => message.Text.Contains("Updated", StringComparison.Ordinal));
            AssertNoPlanEffect(harness, result.Value);
        }
    }

    [Theory]
    [InlineData("Change Today's Plan to 10 minutes.")]
    [InlineData("Please change my plan to 10 minutes.")]
    [InlineData("Make Today's Plan 10 minutes and no audio")]
    [InlineData("Change Today's Plan to 10 minutes, no audio, more vocabulary.")]
    [InlineData("오늘 계획을 10분으로 줄여줘")]
    [InlineData("오늘 계획을 10분으로 해줘")]
    [InlineData("오늘 계획이 10분으로 변경되게 해줘")]
    [InlineData("오늘 계획 변경해줘")]
    [InlineData("오늘 계획을 변경해줘")]
    [InlineData("오늘의 학습 계획을 10분으로 조정해줘")]
    [InlineData("오늘 계획을 어휘 중심으로 바꿔줘")]
    [InlineData("오늘 계획을 10분으로 바꿔")]
    [InlineData("오늘 계획은 동사 위주로 바꿔줘")]
    public async Task Explicit_english_and_korean_plan_requests_still_apply(string learnerText)
    {
        using var harness = new CoachApplicationHarness();
        var sessionId = await harness.StartSessionAsync();
        harness.Coach.NextResult = PlanResult(
            CoachIntentKind.DirectConstraintChange,
            new CoachConstraintDeltaIntent { AvailableMinutes = 10 });

        var result = await SubmitAsync(harness, sessionId, learnerText);

        result.IsOk.Should().BeTrue(result.Detail);
        result.Value!.ChangeReceipt.Should().NotBeNull();
        harness.PlanService.ApplyCallCount.Should().Be(1);
        harness.Db.CoachPlanRevisions.Should().ContainSingle()
            .Which.Source.Should().Be(CoachRevisionSource.DirectRequest);
    }

    [Fact]
    public async Task Explicit_plan_request_with_an_activity_purpose_clause_still_applies_the_plan()
    {
        using var harness = new CoachApplicationHarness();
        var sessionId = await harness.StartSessionAsync();
        harness.Coach.NextResult = PlanResult(
            CoachIntentKind.DirectConstraintChange,
            new CoachConstraintDeltaIntent { AvailableMinutes = 10 });

        var result = await SubmitAsync(
            harness,
            sessionId,
            "Make Today's Plan 10 minutes so I can do pronunciation practice.");

        result.IsOk.Should().BeTrue(result.Detail);
        result.Value!.ChangeReceipt.Should().NotBeNull();
        result.Value.PendingSuggestion.Should().BeNull();
        harness.PlanService.PreviewCallCount.Should().Be(1);
        harness.PlanService.ApplyCallCount.Should().Be(1);
        harness.Db.CoachPlanRevisions.Should().ContainSingle();
    }

    [Fact]
    public async Task Explicit_vocabulary_focus_for_todays_plan_still_offers_an_inert_suggestion()
    {
        using var harness = new CoachApplicationHarness();
        var sessionId = await harness.StartSessionAsync();
        harness.Coach.NextResult = PlanResult(
            CoachIntentKind.SuggestConstraintChange,
            new CoachConstraintDeltaIntent { VocabularyFocusDescription = "active verbs" });

        var result = await SubmitAsync(
            harness,
            sessionId,
            "Suggest focusing Today's Plan on active verbs.");

        result.IsOk.Should().BeTrue(result.Detail);
        result.Value!.PendingSuggestion.Should().NotBeNull();
        result.Value.ChangeReceipt.Should().BeNull();
        result.Value.ActiveConstraints.VocabularyFocus.Should().BeNull(
            "a semantic focus remains inert until the learner accepts it");
        harness.FocusResolver.ResolveCount.Should().Be(1);
        harness.PlanService.PreviewCallCount.Should().Be(1);
        harness.PlanService.ApplyCallCount.Should().Be(0);
        harness.Db.CoachPlanRevisions.Should().BeEmpty();
    }

    [Fact]
    public async Task Mixed_teaching_and_explicit_plan_request_keeps_the_answer_and_offers_only()
    {
        using var harness = new CoachApplicationHarness();
        var sessionId = await harness.StartSessionAsync();
        harness.Coach.NextResult = PlanResult(
            CoachIntentKind.SuggestConstraintChange,
            new CoachConstraintDeltaIntent { AvailableMinutes = 10 },
            answer: VocabularyAnswer());

        var result = await SubmitAsync(
            harness,
            sessionId,
            "What does 좋아하다 mean? Also suggest making Today's Plan 10 minutes.");

        result.IsOk.Should().BeTrue(result.Detail);
        result.Value!.Answer.Should().NotBeNull();
        result.Value.PendingSuggestion.Should().NotBeNull();
        result.Value.ChangeReceipt.Should().BeNull();
        result.Value.Messages.Select(message => message.Kind).Should()
            .Equal(CoachMessageKind.PedagogicalAnswer, CoachMessageKind.Suggestion);
        harness.PlanService.PreviewCallCount.Should().Be(1);
        harness.PlanService.ApplyCallCount.Should().Be(0);
        harness.Db.CoachPlanRevisions.Should().BeEmpty();
    }

    [Fact]
    public async Task Clear_typed_acceptance_applies_the_exact_explicit_suggestion_once()
    {
        using var harness = new CoachApplicationHarness();
        var sessionId = await harness.StartSessionAsync();
        var suggestion = await OfferMinutesSuggestionAsync(harness, sessionId);
        harness.Coach.NextResult = DecisionResult(
            CoachIntentKind.AcceptPendingSuggestion,
            suggestion.SuggestionId,
            CoachAcceptanceState.Accepted);

        var result = await SubmitAsync(harness, sessionId, "yes");

        result.IsOk.Should().BeTrue(result.Detail);
        result.Value!.ChangeReceipt.Should().NotBeNull();
        result.Value.PendingSuggestion.Should().BeNull();
        harness.PlanService.ApplyCallCount.Should().Be(1);
        harness.PlanService.LastAppliedConstraints!.AvailableMinutes.Should().Be(12);
        harness.Db.CoachPlanRevisions.Should().ContainSingle()
            .Which.Source.Should().Be(CoachRevisionSource.AcceptedSuggestion);
    }

    [Fact]
    public async Task Clear_typed_rejection_clears_the_explicit_suggestion_without_a_write()
    {
        using var harness = new CoachApplicationHarness();
        var sessionId = await harness.StartSessionAsync();
        var suggestion = await OfferMinutesSuggestionAsync(harness, sessionId);
        harness.Coach.NextResult = DecisionResult(
            CoachIntentKind.RejectPendingSuggestion,
            suggestion.SuggestionId,
            CoachAcceptanceState.Rejected);

        var result = await SubmitAsync(harness, sessionId, "no");

        result.IsOk.Should().BeTrue(result.Detail);
        result.Value!.PendingSuggestion.Should().BeNull();
        result.Value.ChangeReceipt.Should().BeNull();
        harness.PlanService.ApplyCallCount.Should().Be(0);
        harness.Db.CoachPlanRevisions.Should().BeEmpty();
        harness.Db.CoachSessions.Single().PendingSuggestionId.Should().BeNull();
    }

    [Theory]
    [InlineData(CoachIntentKind.DirectConstraintChange)]
    [InlineData(CoachIntentKind.SuggestConstraintChange)]
    public async Task A_model_plan_kind_is_insufficient_without_current_learner_authority(
        CoachIntentKind modelIntent)
    {
        using var harness = new CoachApplicationHarness();
        var sessionId = await harness.StartSessionAsync();
        harness.Coach.NextResult = PlanResult(
            modelIntent,
            new CoachConstraintDeltaIntent { AvailableMinutes = 12 });

        var result = await SubmitAsync(harness, sessionId, "Give me a Korean greeting to practice.");

        AssertClarification(result, "studying that directly");
        AssertNoPlanEffect(harness, result.Value!);
    }

    private static void AssertClarification(
        CoachOperationResult<CoachTurnResponse> result,
        string expectedText)
    {
        result.IsOk.Should().BeTrue("a policy mismatch is a handled product boundary");
        result.Value!.Status.Should().Be(CoachTurnStatus.Incomplete);
        result.Value.StopReason.Should().Be(CoachStopReason.ClarificationRequested);
        result.Value.ClarifyingQuestion.Should().Contain(expectedText);
        result.Value.Messages.Should().ContainSingle()
            .Which.Kind.Should().Be(CoachMessageKind.Clarification);
    }

    private static void AssertNoPlanEffect(
        CoachApplicationHarness harness,
        CoachTurnResponse response)
    {
        response.PendingSuggestion.Should().BeNull();
        response.ChangeReceipt.Should().BeNull();
        response.Messages.Should().NotContain(
            message => message.Kind == CoachMessageKind.Receipt
                || message.Kind == CoachMessageKind.Suggestion);
        harness.PlanService.PreviewCallCount.Should().Be(0);
        harness.PlanService.ApplyCallCount.Should().Be(0);
        harness.PlanService.LastPreviewConstraints.Should().BeNull();
        harness.PlanService.LastAppliedConstraints.Should().BeNull();
        harness.PlanService.LastPreviewFocusIds.Should().BeNull();
        harness.PlanService.LastApplyFocusIds.Should().BeNull();
        harness.Db.CoachPlanRevisions.Should().BeEmpty();
        harness.Db.CoachSessions.Single().PendingSuggestionId.Should().BeNull();
        harness.Db.CoachSessions.Single().PendingSuggestionDeltaJson.Should().BeNull();
    }

    private static async Task<PendingCoachSuggestionDto> OfferMinutesSuggestionAsync(
        CoachApplicationHarness harness,
        string sessionId)
    {
        harness.Coach.NextResult = PlanResult(
            CoachIntentKind.SuggestConstraintChange,
            new CoachConstraintDeltaIntent { AvailableMinutes = 12 });

        var result = await SubmitAsync(
            harness,
            sessionId,
            "Suggest making Today's Plan 12 minutes.");

        result.IsOk.Should().BeTrue(result.Detail);
        result.Value!.PendingSuggestion.Should().NotBeNull();
        harness.PlanService.ApplyCallCount.Should().Be(0);
        harness.Db.CoachPlanRevisions.Should().BeEmpty();
        return result.Value.PendingSuggestion!;
    }

    private static CoachAgentTurnResult PlanResult(
        CoachIntentKind kind,
        CoachConstraintDeltaIntent delta,
        string coachMessage = "Updated Today's Plan.",
        CoachPedagogicalAnswerIntent? answer = null) =>
        new()
        {
            Outcome = CoachAgentOutcome.Completed,
            Intent = new CoachTurnIntent
            {
                Kind = kind,
                ConstraintDelta = delta,
                CoachMessage = coachMessage,
                PedagogicalAnswer = answer
            }
        };

    private static CoachAgentTurnResult DecisionResult(
        CoachIntentKind kind,
        string suggestionId,
        CoachAcceptanceState acceptanceState) =>
        new()
        {
            Outcome = CoachAgentOutcome.Completed,
            Intent = new CoachTurnIntent
            {
                Kind = kind,
                PendingSuggestionId = suggestionId,
                AcceptanceState = acceptanceState,
                CoachMessage = "Decision recorded."
            }
        };

    private static CoachAgentTurnResult AnswerResult(CoachAnswerTopic topic, string text) =>
        new()
        {
            Outcome = CoachAgentOutcome.Completed,
            Intent = new CoachTurnIntent
            {
                Kind = CoachIntentKind.PedagogicalAnswer,
                PedagogicalAnswer = new CoachPedagogicalAnswerIntent
                {
                    Topic = topic,
                    Blocks =
                    [
                        new CoachAnswerBlockIntent
                        {
                            Kind = CoachAnswerBlockKind.Answer,
                            Spans =
                            [
                                new CoachAnswerSpanIntent
                                {
                                    Text = text,
                                    Language = CoachLanguageRole.Display
                                }
                            ]
                        }
                    ]
                }
            }
        };

    private static CoachPedagogicalAnswerIntent VocabularyAnswer() =>
        new()
        {
            Topic = CoachAnswerTopic.Vocabulary,
            Blocks =
            [
                new CoachAnswerBlockIntent
                {
                    Kind = CoachAnswerBlockKind.Answer,
                    Spans =
                    [
                        new CoachAnswerSpanIntent
                        {
                            Text = "좋아하다 means to like something.",
                            Language = CoachLanguageRole.Display
                        }
                    ]
                }
            ]
        };

    private static Task<CoachOperationResult<CoachTurnResponse>> SubmitAsync(
        CoachApplicationHarness harness,
        string sessionId,
        string learnerText) =>
        harness.Service.SubmitTurnAsync(
            sessionId,
            new CoachTurnRequest
            {
                InputKind = CoachTurnInputKind.Text,
                Text = learnerText
            });
}
