using FluentAssertions;
using SentenceStudio.Api.Coach.Tools;
using SentenceStudio.Api.Coach.Validation;
using SentenceStudio.Contracts.Coach;
using SentenceStudio.Contracts.Coach.Intent;

namespace SentenceStudio.Api.Tests.Coach.Validation;

/// <summary>
/// Proves the intent validator refuses a banned claim, a command, a shape that
/// does not agree with itself, an evidence window that is not allowed, and a
/// preview that names a resource the learner does not own.
/// </summary>
public class CoachIntentValidatorTests
{
    private static readonly DateOnly Today = new(2026, 8, 14);
    private readonly CoachIntentValidator _validator = new();

    private static CoachTurnIntent DirectChange() => new()
    {
        Kind = CoachIntentKind.DirectConstraintChange,
        AcceptanceState = CoachAcceptanceState.NotApplicable,
        CoachMessage = "Today's Plan now fits 10 minutes and uses no audio.",
        ConstraintDelta = new CoachConstraintDeltaIntent { AvailableMinutes = 10, AudioAllowed = false },
        EvidenceReferences = [new CoachEvidenceReferenceIntent { Kind = CoachEvidenceKind.PracticeBalance, WindowDays = 14 }]
    };

    [Fact]
    public void A_well_formed_direct_change_passes()
    {
        _validator.ValidateIntent(DirectChange()).IsValid.Should().BeTrue();
    }

    [Fact]
    public void A_direct_change_without_a_delta_is_refused()
    {
        var intent = DirectChange();
        intent.ConstraintDelta = null;

        var result = _validator.ValidateIntent(intent);

        result.Violations.Should().Contain(v => v.Code == "delta_required");
    }

    [Fact]
    public void An_empty_delta_counts_as_no_change()
    {
        var intent = DirectChange();
        intent.ConstraintDelta = new CoachConstraintDeltaIntent();

        _validator.ValidateIntent(intent).Violations.Should().Contain(v => v.Code == "delta_required");
    }

    [Fact]
    public void An_acceptance_must_name_the_suggestion_and_carry_no_change()
    {
        var intent = new CoachTurnIntent
        {
            Kind = CoachIntentKind.AcceptPendingSuggestion,
            AcceptanceState = CoachAcceptanceState.NotApplicable,
            CoachMessage = "Added the speaking activity.",
            ConstraintDelta = new CoachConstraintDeltaIntent { AvailableMinutes = 30 }
        };

        var result = _validator.ValidateIntent(intent);

        result.Violations.Should().Contain(v => v.Code == "suggestion_required");
        result.Violations.Should().Contain(v => v.Code == "acceptance_state");
        result.Violations.Should().Contain(v => v.Code == "delta_forbidden");
    }

    [Fact]
    public void A_valid_acceptance_passes()
    {
        var intent = new CoachTurnIntent
        {
            Kind = CoachIntentKind.AcceptPendingSuggestion,
            AcceptanceState = CoachAcceptanceState.Accepted,
            PendingSuggestionId = "suggestion-1",
            CoachMessage = "Added 4 minutes of speaking."
        };

        _validator.ValidateIntent(intent).IsValid.Should().BeTrue();
    }

    [Fact]
    public void A_rejection_must_set_the_rejected_state()
    {
        var intent = new CoachTurnIntent
        {
            Kind = CoachIntentKind.RejectPendingSuggestion,
            AcceptanceState = CoachAcceptanceState.Accepted,
            PendingSuggestionId = "suggestion-1",
            CoachMessage = "Kept today's plan."
        };

        _validator.ValidateIntent(intent).Violations.Should().Contain(v => v.Code == "acceptance_state");
    }

    [Fact]
    public void An_unclear_answer_must_ask_a_question_and_change_nothing()
    {
        var intent = new CoachTurnIntent
        {
            Kind = CoachIntentKind.DirectConstraintChange,
            AcceptanceState = CoachAcceptanceState.Ambiguous,
            CoachMessage = "Maybe.",
            ConstraintDelta = new CoachConstraintDeltaIntent { AudioAllowed = false }
        };

        _validator.ValidateIntent(intent).Violations
            .Should().Contain(v => v.Code == "ambiguous_requires_question");
    }

    [Fact]
    public void A_clarification_needs_a_question_and_no_change()
    {
        var intent = new CoachTurnIntent
        {
            Kind = CoachIntentKind.AskClarification,
            AcceptanceState = CoachAcceptanceState.Ambiguous,
            CoachMessage = "I need one answer first.",
            ConstraintDelta = new CoachConstraintDeltaIntent { AudioAllowed = false }
        };

        var result = _validator.ValidateIntent(intent);

        result.Violations.Should().Contain(v => v.Code == "question_required");
        result.Violations.Should().Contain(v => v.Code == "delta_forbidden");
    }

    [Fact]
    public void An_off_topic_answer_must_carry_no_change()
    {
        var intent = new CoachTurnIntent
        {
            Kind = CoachIntentKind.OffTopic,
            CoachMessage = "I can help with your study plan only.",
            ConstraintDelta = new CoachConstraintDeltaIntent { TypingAllowed = false }
        };

        _validator.ValidateIntent(intent).Violations.Should().Contain(v => v.Code == "delta_forbidden");
    }

    [Theory]
    [InlineData("You are B2 now.", "proficiency_claim")]
    [InlineData("You will be fluent in 90 days.", "fluency_timeline")]
    [InlineData("You are a fast learner.", "aptitude_claim")]
    [InlineData("This looks like dyslexia.", "health_claim")]
    [InlineData("I guarantee results this month.", "guarantee_claim")]
    public void A_banned_claim_is_refused(string message, string expectedCode)
    {
        var intent = DirectChange();
        intent.CoachMessage = message;

        _validator.ValidateIntent(intent).Violations.Should().Contain(v => v.Code == expectedCode);
    }

    [Theory]
    [InlineData("DROP TABLE DailyPlans", "sql_command")]
    [InlineData("delete from VocabularyProgress", "sql_command")]
    [InlineData("Call /api/v1/plans to apply this.", "route_reference")]
    [InlineData("Open https://example.com for more.", "external_link")]
    [InlineData("Set userProfileId to another value.", "identity_reference")]
    public void A_command_or_a_route_is_refused(string message, string expectedCode)
    {
        var intent = DirectChange();
        intent.CoachMessage = message;

        var result = _validator.ValidateIntent(intent);

        result.Violations.Should().Contain(v => v.Code == expectedCode);
        result.Violations.Should().Contain(v => v.Kind == CoachViolationKind.WriteCommand);
    }

    [Fact]
    public void A_long_message_is_refused()
    {
        var intent = DirectChange();
        intent.CoachMessage = new string('a', 401);

        _validator.ValidateIntent(intent).Violations.Should().Contain(v => v.Code == "text_length");
    }

    [Fact]
    public void A_long_question_is_refused()
    {
        var intent = new CoachTurnIntent
        {
            Kind = CoachIntentKind.AskClarification,
            AcceptanceState = CoachAcceptanceState.Ambiguous,
            CoachMessage = "One question first.",
            ClarifyingQuestion = new string('b', 201)
        };

        _validator.ValidateIntent(intent).Violations.Should().Contain(v => v.Code == "text_length");
    }

    [Fact]
    public void Too_many_facts_are_refused()
    {
        var intent = DirectChange();
        intent.EvidenceReferences = Enumerable.Range(0, 7)
            .Select(_ => new CoachEvidenceReferenceIntent { Kind = CoachEvidenceKind.VocabularyDue })
            .ToList();

        _validator.ValidateIntent(intent).Violations.Should().Contain(v => v.Code == "evidence_count");
    }

    [Theory]
    [InlineData(3)]
    [InlineData(60)]
    public void A_fact_window_outside_the_allowed_set_is_refused(int windowDays)
    {
        var intent = DirectChange();
        intent.EvidenceReferences =
            [new CoachEvidenceReferenceIntent { Kind = CoachEvidenceKind.PracticeBalance, WindowDays = windowDays }];

        _validator.ValidateIntent(intent).Violations.Should().Contain(v => v.Code == "window_length");
    }

    [Fact]
    public void A_practice_balance_fact_without_a_window_is_refused()
    {
        var intent = DirectChange();
        intent.EvidenceReferences = [new CoachEvidenceReferenceIntent { Kind = CoachEvidenceKind.PracticeBalance }];

        _validator.ValidateIntent(intent).Violations.Should().Contain(v => v.Code == "window_required");
    }

    [Fact]
    public void Evidence_with_a_correct_window_passes()
    {
        var evidence = new[] { Evidence(Today.AddDays(-13), Today) };

        _validator.ValidateEvidence(evidence, Today).IsValid.Should().BeTrue();
    }

    [Fact]
    public void Evidence_with_a_window_that_is_not_allowed_is_refused()
    {
        var evidence = new[] { Evidence(Today.AddDays(-9), Today) };

        _validator.ValidateEvidence(evidence, Today).Violations
            .Should().Contain(v => v.Code == "window_length");
    }

    [Fact]
    public void Evidence_that_ends_in_the_future_is_refused()
    {
        var evidence = new[] { Evidence(Today.AddDays(-5), Today.AddDays(1)) };

        _validator.ValidateEvidence(evidence, Today).Violations
            .Should().Contain(v => v.Code == "window_future");
    }

    [Fact]
    public void Evidence_with_a_backward_window_is_refused()
    {
        var evidence = new[] { Evidence(Today, Today.AddDays(-3)) };

        _validator.ValidateEvidence(evidence, Today).Violations
            .Should().Contain(v => v.Code == "window_order");
    }

    [Fact]
    public void A_preview_that_names_an_owned_resource_passes()
    {
        var preview = Preview("resource-1");

        _validator.ValidateOwnedPreview(preview, ["resource-1"]).IsValid.Should().BeTrue();
    }

    [Fact]
    public void A_preview_that_names_an_unowned_resource_is_refused()
    {
        var preview = Preview("resource-of-another-learner");

        var result = _validator.ValidateOwnedPreview(preview, ["resource-1"]);

        result.Violations.Should().Contain(v => v.Code == "unowned_resource");
        result.Violations.Should().OnlyContain(v =>
            v.MaskedEvidence == null || !v.MaskedEvidence.Contains("another-learner"));
    }

    private static CoachEvidenceDto Evidence(DateOnly start, DateOnly end) => new()
    {
        Kind = CoachEvidenceKind.PracticeBalance,
        Label = "Practice balance",
        Summary = "Your last days were mostly input.",
        WindowStartDate = start,
        WindowEndDate = end
    };

    private static PlanPreviewSummary Preview(string resourceId) => new(
        PreviewId: "preview-abc",
        TotalMinutes: 10,
        Items:
        [
            new PlanPreviewItem("Reading", 5, 1, resourceId, "Travel phrases", null, 0)
        ],
        VocabularyReviewWordCount: 0,
        TotalDueCount: 0,
        PrimaryResourceTitle: "Travel phrases",
        PrimaryResourceId: resourceId,
        Scope: Tools.CoachResultScopeSamples.Any(returnedCount: 1));
}
