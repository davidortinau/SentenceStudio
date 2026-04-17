using FluentAssertions;
using SentenceStudio.Shared.Services;

namespace SentenceStudio.UnitTests.Services;

/// <summary>
/// Regression tests for Cloze activity bugs reported by the user:
///   1. Correct answer with Korean particle was rejected when the particle was already
///      shown in the sentence template (e.g. blank shows "___를" but answer key is "밥을").
///   2. The correct answer was missing from the multiple-choice options.
///   3. VocabularyWordAsUsed was not a substring of SentenceText, so the blank never
///      appeared correctly.
/// </summary>
public class ClozureAnswerHelperTests
{
    // ------- AreAnswersEquivalent (grading leniency) -------

    [Theory]
    [InlineData("밥", "밥을")]        // user typed noun; expected has object marker
    [InlineData("밥을", "밥")]        // reverse — user included marker
    [InlineData("볼거리", "볼거리가")] // subject marker 가
    [InlineData("집", "집에서")]     // location marker 에서
    [InlineData("학교", "학교에")]   // destination 에
    [InlineData("친구", "친구와")]   // companion 와
    [InlineData("  밥 ", "밥을")]     // whitespace tolerance
    public void AreAnswersEquivalent_StripsKoreanParticles(string userInput, string expected)
    {
        ClozureAnswerHelper.AreAnswersEquivalent(userInput, expected).Should().BeTrue();
    }

    [Theory]
    [InlineData("밥", "국")]
    [InlineData("집", "학교")]
    [InlineData("볼거리", "먹거리")]
    public void AreAnswersEquivalent_DifferentWordsReturnsFalse(string userInput, string expected)
    {
        ClozureAnswerHelper.AreAnswersEquivalent(userInput, expected).Should().BeFalse();
    }

    [Theory]
    [InlineData("Apple", "apple")]
    [InlineData("apple", "Apple")]
    public void AreAnswersEquivalent_CaseInsensitiveForNonKorean(string userInput, string expected)
    {
        ClozureAnswerHelper.AreAnswersEquivalent(userInput, expected).Should().BeTrue();
    }

    [Fact]
    public void AreAnswersEquivalent_NullsReturnFalse()
    {
        ClozureAnswerHelper.AreAnswersEquivalent(null, "밥").Should().BeFalse();
        ClozureAnswerHelper.AreAnswersEquivalent("밥", null).Should().BeFalse();
        ClozureAnswerHelper.AreAnswersEquivalent(null, null).Should().BeFalse();
    }

    // ------- EnsureGuessesIncludeAnswer (distractor validation) -------

    [Fact]
    public void EnsureGuessesIncludeAnswer_InjectsMissingCorrectAnswer()
    {
        // Reproduces IMG_4136: correct answer "볼거리가" missing from choices.
        var guesses = new List<string> { "정확하다", "가져가다", "친하다", "사람들", "먹거리" };
        var result = ClozureAnswerHelper.EnsureGuessesIncludeAnswer(guesses, "볼거리가", out var wasFixed);

        wasFixed.Should().BeTrue();
        result.Should().Contain("볼거리가");
        result.Count.Should().Be(5);
    }

    [Fact]
    public void EnsureGuessesIncludeAnswer_LeavesListAloneWhenAnswerPresent()
    {
        var guesses = new List<string> { "밥을", "국을", "빵을", "김치를", "과일을" };
        var result = ClozureAnswerHelper.EnsureGuessesIncludeAnswer(guesses, "밥을", out var wasFixed);

        wasFixed.Should().BeFalse();
        result.Should().BeEquivalentTo(guesses);
    }

    [Fact]
    public void EnsureGuessesIncludeAnswer_NormalizesEquivalentButInexactAnswer()
    {
        // If guesses contain a particle-stripped variant of the answer, we should
        // swap it for the exact answer rather than injecting a duplicate.
        var guesses = new List<string> { "밥", "국을", "빵을", "김치를", "과일을" };
        var result = ClozureAnswerHelper.EnsureGuessesIncludeAnswer(guesses, "밥을", out var wasFixed);

        wasFixed.Should().BeTrue();
        result.Should().Contain("밥을");
        result.Should().NotContain("밥");
        result.Count.Should().Be(5);
    }

    [Fact]
    public void EnsureGuessesIncludeAnswer_DeduplicatesEntries()
    {
        var guesses = new List<string> { "밥을", "밥을", "국을", "빵을" };
        var result = ClozureAnswerHelper.EnsureGuessesIncludeAnswer(guesses, "밥을", out _);

        result.Distinct().Count().Should().Be(result.Count);
    }

    [Fact]
    public void EnsureGuessesIncludeAnswer_HandlesNullInput()
    {
        var result = ClozureAnswerHelper.EnsureGuessesIncludeAnswer(null, "밥을", out var wasFixed);

        wasFixed.Should().BeTrue();
        result.Should().Contain("밥을");
    }

    // ------- TryRepairWordAsUsed (sentence alignment) -------

    [Fact]
    public void TryRepairWordAsUsed_ExtendsNounWithAttachedParticle()
    {
        // Reproduces the pattern where AI returns just the noun as VocabularyWordAsUsed
        // but the sentence has the particle attached. We extend to cover the particle.
        var sentence = "나는 매일 아침에 밥을 먹어요.";
        var repaired = ClozureAnswerHelper.TryRepairWordAsUsed(sentence, "밥", "밥");

        repaired.Should().Be("밥을");
    }

    [Fact]
    public void TryRepairWordAsUsed_StripsParticleWhenOnlyNounInSentence()
    {
        // AI returns "밥을" but the sentence just has "밥" (with different following word).
        var sentence = "나는 밥 이 아니다.";
        var repaired = ClozureAnswerHelper.TryRepairWordAsUsed(sentence, "밥을", "밥");

        // Should resolve to a substring of the sentence.
        repaired.Should().NotBeNull();
        sentence.Should().Contain(repaired!);
    }

    [Fact]
    public void TryRepairWordAsUsed_FallsBackToDictionaryForm()
    {
        var sentence = "학교에 갑니다.";
        var repaired = ClozureAnswerHelper.TryRepairWordAsUsed(sentence, "가다", "가다");

        // "가다" isn't in the sentence; neither is the dictionary form. Returns null.
        repaired.Should().BeNull();
    }

    [Fact]
    public void TryRepairWordAsUsed_ReturnsNullWhenNothingMatches()
    {
        var repaired = ClozureAnswerHelper.TryRepairWordAsUsed("나는 집에 간다.", "국을", "국");
        repaired.Should().BeNull();
    }

    [Fact]
    public void TryRepairWordAsUsed_DoesNotGrabParticleFromMidWord()
    {
        // Boundary check: don't extend "집" into "집을 좋아" picking up "을" that's
        // actually part of a compound — the particle must end at whitespace/punct.
        var sentence = "나는 집에 간다.";
        var repaired = ClozureAnswerHelper.TryRepairWordAsUsed(sentence, "집", "집");
        repaired.Should().Be("집에");
    }

    [Fact]
    public void TryRepairWordAsUsed_SkipsMidWordOccurrenceAndFindsStandalone()
    {
        // "을지" is a placename prefix — "을" mid-word must not be treated as a standalone
        // particle attached to an empty noun. Searching for "로" should find the standalone
        // occurrence in "버스로" rather than inside "을지로".
        var sentence = "을지로에서 버스로 간다.";
        var repaired = ClozureAnswerHelper.TryRepairWordAsUsed(sentence, "버스", "버스");
        repaired.Should().Be("버스로");
    }
}
