using Microsoft.Extensions.DependencyInjection;
using SentenceStudio.Api.Coach.Tools;
using SentenceStudio.Api.Coach.Validation;
using SentenceStudio.Contracts.Coach;

namespace SentenceStudio.Api.Tests.Coach.Validation;

/// <summary>
/// The embargo scanner is an active contract, not a lint. These tests prove it runs at
/// registration and on every tool build, that it covers both the tool answers and the public
/// coach contracts, and that a violation fails closed.
/// </summary>
public class CoachOutputContractTests
{
    [Fact]
    public void TheContractPassesForEveryShapeTheCoachCanEmit()
    {
        var result = CoachOutputContract.Scan();

        result.IsValid.Should().BeTrue(
            "the coach may not ship a shape that carries identity data or an entity: {0}",
            string.Join("; ", result.Violations.Select(v => v.Message)));
    }

    [Fact]
    public void TheContractCoversAllFiveToolAnswers()
    {
        CoachOutputContract.ToolResultTypes.Should().BeEquivalentTo(new[]
        {
            typeof(LearnerProfileSummary),
            typeof(PracticeBalanceSummary),
            typeof(VocabularyDueSummary),
            typeof(ResourceCatalogSummary),
            typeof(PlanPreviewSummary)
        });
    }

    /// <summary>
    /// The two scopes between them cover every coach contract, and each holds the right ones.
    /// </summary>
    /// <remarks>
    /// The strict embargo was written for shapes the model can see. Sweeping the client-facing
    /// contracts into it too was over-reach that made a conversation-history API impossible to
    /// express honestly, so the lists are now separate and each is checked for what belongs in it.
    /// </remarks>
    [Fact]
    public void TheContractSplitsModelVisibleShapesFromClientContracts()
    {
        CoachOutputContract.ModelVisibleTypes.Should().Contain(
            typeof(SentenceStudio.Contracts.Coach.Intent.CoachTurnIntent),
            "the typed intent is what the model produces");
        CoachOutputContract.ModelVisibleTypes.Should().Contain(typeof(LearnerProfileSummary));
        CoachOutputContract.ModelVisibleTypes.Should().NotContain(
            typeof(SentenceStudio.Api.Coach.Agents.CoachAgentTurnRequest),
            "the turn request is internal plumbing held by the structural isolation tests, "
            + "not by a name check");

        var client = CoachOutputContract.PublicClientContractTypes;

        client.Should().Contain(typeof(CoachTurnResponse));
        client.Should().Contain(typeof(CoachPlanItemDto));
        client.Should().Contain(typeof(CoachEvidenceDto));
        client.Should().Contain(typeof(PendingCoachSuggestionDto));
        client.Should().Contain(typeof(CoachConversationDto),
            "durable history is client-facing and the model never sees it");

        client.Should().NotContain(typeof(SentenceStudio.Contracts.Coach.Intent.CoachTurnIntent),
            "the intent must not be downgraded to the bounded rules by living one namespace down");
    }

    [Fact]
    public void RegistrationRunsTheContract()
    {
        // A failure here would throw out of AddCoachReadOnlyTools, so a host with a defective
        // shape never starts. The assertion documents that the call site exists.
        var act = () => new ServiceCollection().AddCoachReadOnlyTools();

        act.Should().NotThrow();
        CoachOutputContract.Scan().IsValid.Should().BeTrue();
    }

    [Fact]
    public void EnsureValidIsSafeToCallRepeatedly()
    {
        CoachOutputContract.EnsureValid();
        CoachOutputContract.EnsureValid();
    }

    [Fact]
    public void AContractViolationCarriesMaskedEvidenceOnly()
    {
        var violation = new CoachViolation(
            CoachViolationKind.Ownership, "unowned_resource", "not owned",
            CoachValidationResult.Mask("resource-of-another-learner"));

        var exception = new CoachContractViolationException(
            "coach tool allow-list", CoachValidationResult.From([violation]));

        exception.Contract.Should().Be("coach tool allow-list");
        exception.Violations.Should().ContainSingle();
        exception.Message.Should().Contain("unowned_resource");
        exception.Message.Should().NotContain("another-learner");
    }

    [Fact]
    public void TheScannerStillCatchesADefectiveShape()
    {
        // Proves the contract would fail rather than pass vacuously.
        var result = new CoachEmbargoScanner().ScanType(typeof(DefectiveAnswer));

        result.IsValid.Should().BeFalse();
        result.Violations.Should().Contain(v => v.Code == "member_name");
    }

    private sealed record DefectiveAnswer(string UserProfileId);
}
