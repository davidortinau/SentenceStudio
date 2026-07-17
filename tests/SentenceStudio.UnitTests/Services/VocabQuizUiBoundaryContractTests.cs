using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SentenceStudio.Services;
using SentenceStudio.Services.Progress;
using SentenceStudio.Services.Timer;
using SentenceStudio.Shared.Models;

namespace SentenceStudio.UnitTests.Services;

public sealed class VocabQuizUiBoundaryContractTests
{
    [Fact]
    public async Task DirectRouteValidation_RefusesMismatchedPlanBeforeTimerStarts()
    {
        var progress = new Mock<IProgressService>(MockBehavior.Strict);
        progress.Setup(service => service.ValidatePlanItemAsync(
                "user-a",
                "reading-item",
                PlanActivityType.VocabularyReview,
                "resource-a",
                "skill-a",
                It.IsAny<IReadOnlyCollection<string>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((ValidatedPlanItemProgress?)null);
        var timer = new ActivityTimerService(
            progress.Object,
            NullLogger<ActivityTimerService>.Instance);

        var lease = await timer.StartValidatedSessionAsync(
            new ActivityTimerStartRequest(
                "user-a",
                PlanActivityType.VocabularyReview,
                "reading-item",
                "resource-a",
                "skill-a",
                ["word-a"]));

        lease.Should().BeNull();
        timer.IsActive.Should().BeFalse();
        progress.VerifyAll();
    }

    [Fact]
    public void ChooseOwnNavigation_ForeignReferencesAreRejectedBeforeUse()
    {
        var reconciliation = ChooseOwnSelectionPreferences.Reconcile(
            new ChooseOwnSelection(["owned-resource", "foreign-resource"], "foreign-skill"),
            ["owned-resource"],
            ["owned-skill"]);

        reconciliation.IsFullyOwned.Should().BeFalse();
        reconciliation.RejectedCount.Should().Be(2);
        reconciliation.Selection.ResourceIds.Should().Equal("owned-resource");
        reconciliation.Selection.SkillId.Should().BeNull();
    }

    [Fact]
    public void ChooseOwnUiCallbacks_ClearRejectedValuesBeforeSavingState()
    {
        var state = new ChooseOwnSelectionState();
        state.ActivateProfile("user-a");
        state.Replace(new ChooseOwnSelection(["foreign-resource"], "foreign-skill"));
        var reconciliation = ChooseOwnSelectionPreferences.Reconcile(
            new ChooseOwnSelection(state.ResourceIds, state.SkillId),
            ownedResourceIds: [],
            ownedSkillIds: []);

        state.Replace(reconciliation.Selection);

        state.ResourceIds.Should().BeEmpty();
        state.SkillId.Should().BeNull();
    }

    [Fact]
    public void ResumeSnapshot_MismatchedReferencesAreRejectedBehaviorally()
    {
        var snapshot = new VocabQuizSessionSnapshot
        {
            PlanItemId = "stale-plan",
            ResourceIds = ["foreign-resource"],
            SkillId = "foreign-skill",
            BatchPool =
            [
                new VocabQuizBatchItemSnapshot { WordId = "foreign-word" }
            ],
            RoundWordOrder = ["foreign-word"],
            SessionItemsWordIds = ["foreign-word"]
        };

        var rejected = VocabQuizLaunchValidator.CountRejectedSnapshotReferences(
            snapshot,
            expectedPlanItemId: "current-plan",
            expectedFocusVocabularyIds: ["owned-word"],
            expectedResourceIds: ["owned-resource"],
            expectedDueOnly: false,
            expectedSkillId: "owned-skill",
            reachableWordIds: ["owned-word"]);

        rejected.Should().BeGreaterThan(0);
    }
}
