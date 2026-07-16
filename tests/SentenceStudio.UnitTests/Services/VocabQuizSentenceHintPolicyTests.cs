using FluentAssertions;
using SentenceStudio.Shared.Models;
using SentenceStudio.Shared.Services;

namespace SentenceStudio.UnitTests.Services;

public sealed class VocabQuizSentenceHintPolicyTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void TargetLanguagePrompt_WithHints_ShowsOneToThreeHints(int hintCount)
    {
        var hints = CreateHints(hintCount);

        var visible = VocabQuizSentenceHintPolicy.GetHintsForTurn(
            promptUsesNativeLanguage: false,
            prefetchedHints: hints);

        visible.Should().HaveCount(hintCount);
        VocabQuizSentenceHintPolicy.ShouldShowButton(false, hints).Should().BeTrue();
    }

    [Fact]
    public void NativeLanguagePrompt_WithHints_HidesButtonAndContent()
    {
        var hints = CreateHints(3);

        VocabQuizSentenceHintPolicy.ShouldShowButton(true, hints).Should().BeFalse();
        VocabQuizSentenceHintPolicy.GetHintsForTurn(true, hints).Should().BeEmpty();
    }

    [Fact]
    public void TargetLanguagePrompt_WithoutHints_HidesButton()
    {
        VocabQuizSentenceHintPolicy.ShouldShowButton(
                promptUsesNativeLanguage: false,
                Array.Empty<VocabQuizSentenceHint>())
            .Should()
            .BeFalse();
    }

    [Fact]
    public void TargetLanguagePrompt_ProjectsAtMostThreeNonEmptyTargetSentences()
    {
        var hints = CreateHints(4)
            .Append(new VocabQuizSentenceHint(99, "word-a", "   "))
            .ToArray();

        var visible = VocabQuizSentenceHintPolicy.GetHintsForTurn(false, hints);

        visible.Should().HaveCount(3);
        visible.Select(hint => hint.TargetSentence)
            .Should().Equal("target sentence 1", "target sentence 2", "target sentence 3");
    }

    [Fact]
    public void HintDto_ExposesTargetOnlyProjection()
    {
        typeof(VocabQuizSentenceHint).GetProperties()
            .Select(property => property.Name)
            .Should().Equal(
                nameof(VocabQuizSentenceHint.ExampleSentenceId),
                nameof(VocabQuizSentenceHint.VocabularyWordId),
                nameof(VocabQuizSentenceHint.TargetSentence));
    }

    [Fact]
    public void Toggle_OpensAndClosesEligiblePanel()
    {
        var expanded = VocabQuizSentenceHintPolicy.GetExpandedState(
            isExpanded: false,
            isEligible: true,
            transition: VocabQuizSentenceHintTransition.Toggle);

        expanded.Should().BeTrue();

        expanded = VocabQuizSentenceHintPolicy.GetExpandedState(
            expanded,
            isEligible: true,
            transition: VocabQuizSentenceHintTransition.Toggle);

        expanded.Should().BeFalse();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void CorrectAndIncorrectAnswerFeedback_PreserveOpenPanel(bool answerWasCorrect)
    {
        VocabQuizSentenceHintPolicy.GetExpandedState(
                isExpanded: true,
                isEligible: true,
                transition: VocabQuizSentenceHintTransition.AnswerFeedback)
            .Should()
            .BeTrue($"the panel was open before the {(answerWasCorrect ? "correct" : "incorrect")} feedback state");
    }

    [Theory]
    [MemberData(nameof(ResetTransitions))]
    public void TurnSessionAndFullscreenTransitions_ResetPanel(
        VocabQuizSentenceHintTransition transition)
    {
        VocabQuizSentenceHintPolicy.GetExpandedState(
                isExpanded: true,
                isEligible: true,
                transition: transition)
            .Should()
            .BeFalse();
    }

    [Fact]
    public void IneligibleTurn_CannotRemainExpanded()
    {
        VocabQuizSentenceHintPolicy.GetExpandedState(
                isExpanded: true,
                isEligible: false,
                transition: VocabQuizSentenceHintTransition.AnswerFeedback)
            .Should()
            .BeFalse();
    }

    public static TheoryData<VocabQuizSentenceHintTransition> ResetTransitions => new()
    {
        VocabQuizSentenceHintTransition.LoadCurrentItem,
        VocabQuizSentenceHintTransition.Restart,
        VocabQuizSentenceHintTransition.Resume,
        VocabQuizSentenceHintTransition.DirectionChange,
        VocabQuizSentenceHintTransition.OpenFullscreen,
        VocabQuizSentenceHintTransition.SentenceShortcut
    };

    private static IReadOnlyList<VocabQuizSentenceHint> CreateHints(int count)
        => Enumerable.Range(1, count)
            .Select(index => new VocabQuizSentenceHint(
                index,
                "word-a",
                $"target sentence {index}"))
            .ToArray();
}
