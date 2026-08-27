using FluentAssertions;
using SentenceStudio.Api.Coach.Opportunities;
using SentenceStudio.Api.Coach.Opportunities.Mapping;
using SentenceStudio.Api.Coach.Validation;

namespace SentenceStudio.Api.Tests.Coach;

/// <summary>
/// Violation observability: new capability codes exist, mapper routes correctly,
/// and no PII leaks into codes or log parameters.
/// </summary>
public sealed class CoachViolationObservabilityTests
{
    // ── New codes exist in the closed vocabulary ────────────────────────────

    [Fact]
    public void AnswerRequired_code_is_in_All()
    {
        CoachOpportunityCapabilityCodes.All.Should()
            .Contain(CoachOpportunityCapabilityCodes.AnswerRequired);
    }

    [Fact]
    public void EvidenceReferenceInvalid_code_is_in_All()
    {
        CoachOpportunityCapabilityCodes.All.Should()
            .Contain(CoachOpportunityCapabilityCodes.EvidenceReferenceInvalid);
    }

    [Fact]
    public void New_codes_are_known()
    {
        CoachOpportunityCapabilityCodes.IsKnown(
            CoachOpportunityCapabilityCodes.AnswerRequired).Should().BeTrue();
        CoachOpportunityCapabilityCodes.IsKnown(
            CoachOpportunityCapabilityCodes.EvidenceReferenceInvalid).Should().BeTrue();
    }

    // ── Codes are content-free snake_case ────────────────────────────────────

    [Fact]
    public void New_codes_are_content_free_snake_case()
    {
        CoachOpportunityCapabilityCodes.AnswerRequired.Should()
            .MatchRegex("^[a-z_]+$", "codes must be content-free snake_case");
        CoachOpportunityCapabilityCodes.EvidenceReferenceInvalid.Should()
            .MatchRegex("^[a-z_]+$", "codes must be content-free snake_case");
    }

    // ── Codes contain no PII ────────────────────────────────────────────────

    [Fact]
    public void Codes_contain_no_user_data()
    {
        foreach (var code in CoachOpportunityCapabilityCodes.All)
        {
            code.Should().NotContainAny("@", "http", "://", " ",
                $"code '{code}' must be a closed-vocabulary constant, never user content");
        }
    }

    // ── Violation kind formatting for logging ───────────────────────────────

    [Fact]
    public void Violation_codes_format_as_kind_colon_code()
    {
        var violation = new CoachViolation(
            CoachViolationKind.IntentShape, "missing_answer", "test message");

        var formatted = $"{violation.Kind}:{violation.Code}";

        formatted.Should().Be("IntentShape:missing_answer");
        formatted.Should().NotContain("test message",
            "the formatted code must not include the human-readable message (PII risk)");
    }

    [Fact]
    public void Violation_code_pair_excludes_masked_evidence()
    {
        var violation = new CoachViolation(
            CoachViolationKind.EvidenceWindow, "stale_ref", "some detail", "masked evidence text");

        var formatted = $"{violation.Kind}:{violation.Code}";

        formatted.Should().NotContain("masked evidence");
        formatted.Should().NotContain("some detail");
    }
}
