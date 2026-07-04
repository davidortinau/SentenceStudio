using FluentAssertions;
using SentenceStudio.Shared.Models;

namespace SentenceStudio.UnitTests.Models;

public sealed class VocabQuizSessionSnapshotTests
{
    [Fact]
    public void BuildLaunchContextKey_WithSameInputs_ReturnsIdenticalKey()
    {
        var first = VocabQuizSessionSnapshot.BuildLaunchContextKey(
            "plan-1",
            ["vocab-a", "vocab-b"],
            ["resource-a", "resource-b"],
            dueOnly: true,
            "skill-1");

        var second = VocabQuizSessionSnapshot.BuildLaunchContextKey(
            "plan-1",
            ["vocab-a", "vocab-b"],
            ["resource-a", "resource-b"],
            dueOnly: true,
            "skill-1");

        second.Should().Be(first);
    }

    [Fact]
    public void BuildLaunchContextKey_NormalizesIdOrderWhitespaceAndDuplicates()
    {
        var canonical = VocabQuizSessionSnapshot.BuildLaunchContextKey(
            " plan-1 ",
            [" vocab-b ", "vocab-a", "vocab-b", " "],
            ["resource-b", " resource-a ", "resource-a", ""],
            dueOnly: false,
            " skill-1 ");

        var reordered = VocabQuizSessionSnapshot.BuildLaunchContextKey(
            "plan-1",
            ["vocab-a", "vocab-b"],
            ["resource-a", "resource-b"],
            dueOnly: false,
            "skill-1");

        canonical.Should().Be(reordered);
        canonical.Should().Contain("focus=vocab-a,vocab-b");
        canonical.Should().Contain("res=resource-a,resource-b");
    }

    [Fact]
    public void BuildLaunchContextKey_ChangesWhenPlanDueOnlyOrSkillChanges()
    {
        var baseline = VocabQuizSessionSnapshot.BuildLaunchContextKey(
            "plan-1",
            ["vocab-a"],
            ["resource-a"],
            dueOnly: false,
            "skill-1");

        VocabQuizSessionSnapshot.BuildLaunchContextKey("plan-2", ["vocab-a"], ["resource-a"], false, "skill-1")
            .Should().NotBe(baseline);
        VocabQuizSessionSnapshot.BuildLaunchContextKey("plan-1", ["vocab-a"], ["resource-a"], true, "skill-1")
            .Should().NotBe(baseline);
        VocabQuizSessionSnapshot.BuildLaunchContextKey("plan-1", ["vocab-a"], ["resource-a"], false, "skill-2")
            .Should().NotBe(baseline);
    }

    [Fact]
    public void BuildLaunchContextKey_TreatsNullAndEmptyListsTheSame()
    {
        var nullLists = VocabQuizSessionSnapshot.BuildLaunchContextKey(
            planItemId: null,
            focusVocabularyIds: null,
            resourceIds: null,
            dueOnly: false,
            skillId: null);

        var emptyLists = VocabQuizSessionSnapshot.BuildLaunchContextKey(
            planItemId: string.Empty,
            focusVocabularyIds: [],
            resourceIds: [],
            dueOnly: false,
            skillId: string.Empty);

        emptyLists.Should().Be(nullLists);
        emptyLists.Should().Be("plan=|focus=|res=|due=false|skill=");
    }

    [Fact]
    public void SerializeThenDeserialize_RoundTripsFullyPopulatedSnapshot()
    {
        var snapshot = new VocabQuizSessionSnapshot
        {
            PlanItemId = "plan-123",
            FocusVocabularyIds = ["focus-1", "focus-2"],
            ResourceIds = ["resource-1", "resource-2"],
            DueOnly = true,
            SkillId = "skill-abc",
            BatchPool =
            [
                new VocabQuizBatchItemSnapshot
                {
                    WordId = "word-1",
                    SessionCorrectCount = 1,
                    SessionMCCorrect = 2,
                    SessionTextCorrect = 3,
                    PendingRecognitionCheck = true,
                    LostKnownThisSession = false,
                    WasCorrectThisSession = true,
                    IsDueOnlySession = true,
                    RequiresFullSessionDemonstration = false,
                    RecognitionDemonstrationsBaseline = 1,
                    ProductionDemonstrationsBaseline = 2
                },
                new VocabQuizBatchItemSnapshot
                {
                    WordId = "word-2",
                    SessionCorrectCount = 4,
                    SessionMCCorrect = 5,
                    SessionTextCorrect = 6,
                    PendingRecognitionCheck = false,
                    LostKnownThisSession = true,
                    WasCorrectThisSession = false,
                    IsDueOnlySession = true,
                    RequiresFullSessionDemonstration = true,
                    UseKnownWordShortcut = true,
                    RecognitionDemonstrationsBaseline = 3,
                    ProductionDemonstrationsBaseline = 4
                }
            ],
            RoundWordOrder = ["word-2", "word-1"],
            RoundCursor = 7,
            CurrentTurnInRound = 8,
            RoundsCompleted = 9,
            WordsMastered = 10,
            TotalTurns = 11,
            CorrectCount = 12,
            SessionItemsWordIds = ["word-1", "word-2", "word-3"],
            PromptUsesNativeLanguage = true,
            UserMode = "recognition"
        };

        var restored = VocabQuizSessionSnapshot.Deserialize(snapshot.Serialize());

        restored.Should().BeEquivalentTo(snapshot, options => options.WithStrictOrdering());
        restored.BatchPool.Select(item => item.WordId).Should().Equal("word-1", "word-2");
        restored.RoundWordOrder.Should().Equal("word-2", "word-1");
        restored.SessionItemsWordIds.Should().Equal("word-1", "word-2", "word-3");
    }
}
