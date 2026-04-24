using SentenceStudio.Services;
using SentenceStudio.Shared.Models;

namespace SentenceStudio.Api.Tests;

/// <summary>
/// Unit tests for VocabularyClassificationBackfillService.ClassifyHeuristic.
/// Tests all classification scenarios from the agent plan:
/// tags priority, terminal punctuation, whitespace, length threshold, CJK guards, and edge cases.
/// </summary>
public class VocabularyClassificationHeuristicTests
{
    #region Tags Priority Tests (Scenarios 1-4)

    [Theory]
    [InlineData("hello", "phrase", LexicalUnitType.Phrase)]
    [InlineData("hello", "Phrase", LexicalUnitType.Phrase)]
    [InlineData("hello", "PHRASE", LexicalUnitType.Phrase)]
    [InlineData("안녕", "phrase", LexicalUnitType.Phrase)]
    public void ClassifyHeuristic_TagsContainPhrase_ReturnsPhrase(
        string term, string tags, LexicalUnitType expected)
    {
        var result = VocabularyClassificationBackfillService.ClassifyHeuristic(term, tags);
        result.Should().Be(expected, $"term '{term}' with tags '{tags}' should be classified as Phrase");
    }

    [Theory]
    [InlineData("hello", "sentence", LexicalUnitType.Sentence)]
    [InlineData("hello", "Sentence", LexicalUnitType.Sentence)]
    [InlineData("hello", "SENTENCE", LexicalUnitType.Sentence)]
    public void ClassifyHeuristic_TagsContainSentence_ReturnsSentence(
        string term, string tags, LexicalUnitType expected)
    {
        var result = VocabularyClassificationBackfillService.ClassifyHeuristic(term, tags);
        result.Should().Be(expected, $"term '{term}' with tags '{tags}' should be classified as Sentence");
    }

    [Fact]
    public void ClassifyHeuristic_TagsContainBothPhraseAndSentence_ReturnsSentence()
    {
        // Tags are checked in order: sentence first, then phrase
        var result = VocabularyClassificationBackfillService.ClassifyHeuristic("hello", "word,sentence,phrase");
        result.Should().Be(LexicalUnitType.Sentence, "sentence tag takes priority over phrase tag");
    }

    [Theory]
    [InlineData("hello", "word,phrase", LexicalUnitType.Phrase)]
    [InlineData("hello", "word,phrase,other", LexicalUnitType.Phrase)]
    [InlineData("hello", "tag1,phrase,tag2", LexicalUnitType.Phrase)]
    public void ClassifyHeuristic_TagsCommaListContainsPhrase_ReturnsPhrase(
        string term, string tags, LexicalUnitType expected)
    {
        var result = VocabularyClassificationBackfillService.ClassifyHeuristic(term, tags);
        result.Should().Be(expected, $"comma-separated tags containing 'phrase' should classify as Phrase");
    }

    #endregion

    #region Terminal Punctuation Tests (Scenarios 4-5)

    [Theory]
    [InlineData("Hello.", LexicalUnitType.Sentence)]
    [InlineData("How are you?", LexicalUnitType.Sentence)]
    [InlineData("Stop!", LexicalUnitType.Sentence)]
    [InlineData("test?", LexicalUnitType.Sentence)]
    [InlineData("data!", LexicalUnitType.Sentence)]
    public void ClassifyHeuristic_TerminalAsciiPunctuation_ReturnsSentence(
        string term, LexicalUnitType expected)
    {
        var result = VocabularyClassificationBackfillService.ClassifyHeuristic(term, null);
        result.Should().Be(expected, $"term '{term}' with terminal ASCII punctuation should be Sentence");
    }

    [Theory]
    [InlineData("안녕하세요。", LexicalUnitType.Sentence)]
    [InlineData("뭐？", LexicalUnitType.Sentence)]
    [InlineData("멈춰！", LexicalUnitType.Sentence)]
    [InlineData("こんにちは。", LexicalUnitType.Sentence)]
    [InlineData("何？", LexicalUnitType.Sentence)]
    public void ClassifyHeuristic_TerminalCjkPunctuation_ReturnsSentence(
        string term, LexicalUnitType expected)
    {
        var result = VocabularyClassificationBackfillService.ClassifyHeuristic(term, null);
        result.Should().Be(expected, $"term '{term}' with terminal CJK punctuation should be Sentence");
    }

    #endregion

    #region Whitespace and Length Tests (Scenarios 6-9)

    [Theory]
    [InlineData("hello world", LexicalUnitType.Phrase)]
    [InlineData("how are you", LexicalUnitType.Phrase)]
    [InlineData("밥을 먹었어요", LexicalUnitType.Phrase)]
    [InlineData("a b", LexicalUnitType.Phrase)]
    public void ClassifyHeuristic_ContainsWhitespace_ReturnsPhrase(
        string term, LexicalUnitType expected)
    {
        var result = VocabularyClassificationBackfillService.ClassifyHeuristic(term, null);
        result.Should().Be(expected, $"term '{term}' with whitespace should be Phrase");
    }

    [Theory]
    [InlineData("밥을\u3000먹었어요", LexicalUnitType.Phrase)]
    [InlineData("hello\u3000world", LexicalUnitType.Phrase)]
    public void ClassifyHeuristic_ContainsCjkIdeographicSpace_ReturnsPhrase(
        string term, LexicalUnitType expected)
    {
        var result = VocabularyClassificationBackfillService.ClassifyHeuristic(term, null);
        result.Should().Be(expected, $"term '{term}' with CJK ideographic space should be Phrase");
    }

    [Theory]
    [InlineData("thisIsAReallyLongWord", LexicalUnitType.Phrase)] // 21 chars > 12
    [InlineData("1234567890123", LexicalUnitType.Phrase)] // 13 chars > 12
    public void ClassifyHeuristic_LengthExceedsThreshold_ReturnsPhrase(
        string term, LexicalUnitType expected)
    {
        var result = VocabularyClassificationBackfillService.ClassifyHeuristic(term, null);
        result.Should().Be(expected, $"term '{term}' with length > 12 should be Phrase");
    }

    [Fact]
    public void ClassifyHeuristic_LengthExactlyThreshold_ReturnsWord()
    {
        // Implementation uses > operator, so exactly 12 chars is still Word
        var result = VocabularyClassificationBackfillService.ClassifyHeuristic("verylongterm", null);
        result.Should().Be(LexicalUnitType.Word, "term with length == 12 should be Word (threshold is strictly >12)");
    }

    [Theory]
    [InlineData("word", LexicalUnitType.Word)]
    [InlineData("hello", LexicalUnitType.Word)]
    [InlineData("공부하다", LexicalUnitType.Word)] // 4 chars
    [InlineData("test123", LexicalUnitType.Word)]
    [InlineData("shortterm12", LexicalUnitType.Word)] // 11 chars <= 12
    public void ClassifyHeuristic_ShortTermWithoutWhitespaceOrPunctuation_ReturnsWord(
        string term, LexicalUnitType expected)
    {
        var result = VocabularyClassificationBackfillService.ClassifyHeuristic(term, null);
        result.Should().Be(expected, $"term '{term}' (short, no whitespace, no punctuation) should be Word");
    }

    #endregion

    #region CJK Single Character Guard (Scenario 10)

    [Theory]
    [InlineData("책", LexicalUnitType.Unknown)] // Single Korean char
    [InlineData("本", LexicalUnitType.Unknown)] // Single Japanese kanji
    [InlineData("书", LexicalUnitType.Unknown)] // Single Chinese char
    public void ClassifyHeuristic_SingleCjkCharacter_ReturnsUnknown(
        string term, LexicalUnitType expected)
    {
        var result = VocabularyClassificationBackfillService.ClassifyHeuristic(term, null);
        result.Should().Be(expected, $"single CJK character '{term}' should remain Unknown (conservative guard)");
    }

    [Theory]
    [InlineData("a", LexicalUnitType.Word)] // Single ASCII char
    [InlineData("I", LexicalUnitType.Word)]
    public void ClassifyHeuristic_SingleAsciiCharacter_ReturnsWord(
        string term, LexicalUnitType expected)
    {
        var result = VocabularyClassificationBackfillService.ClassifyHeuristic(term, null);
        result.Should().Be(expected, $"single ASCII character '{term}' should be Word");
    }

    #endregion

    #region Edge Cases (Scenarios 11-17)

    [Theory]
    [InlineData("", LexicalUnitType.Unknown)]
    [InlineData("   ", LexicalUnitType.Unknown)]
    [InlineData("\t", LexicalUnitType.Unknown)]
    [InlineData("\n", LexicalUnitType.Unknown)]
    public void ClassifyHeuristic_EmptyOrWhitespace_ReturnsUnknown(
        string term, LexicalUnitType expected)
    {
        var result = VocabularyClassificationBackfillService.ClassifyHeuristic(term, null);
        result.Should().Be(expected, "empty or whitespace-only term should return Unknown");
    }

    [Fact]
    public void ClassifyHeuristic_NullTags_DoesNotCrash()
    {
        var result = VocabularyClassificationBackfillService.ClassifyHeuristic("hello", null);
        result.Should().Be(LexicalUnitType.Word, "null tags should be treated as no tags");
    }

    [Theory]
    [InlineData("hello", "phrase,,,", LexicalUnitType.Phrase)]
    [InlineData("hello", ",,,phrase,,,", LexicalUnitType.Phrase)]
    [InlineData("hello", ",,sentence,,", LexicalUnitType.Sentence)]
    public void ClassifyHeuristic_CorruptedTags_StillDetectsSubstring(
        string term, string tags, LexicalUnitType expected)
    {
        var result = VocabularyClassificationBackfillService.ClassifyHeuristic(term, tags);
        result.Should().Be(expected, $"malformed tags '{tags}' should still detect tag substring");
    }

    [Theory]
    [InlineData("こんにちは！！！", LexicalUnitType.Sentence)] // Triple exclamation (checks last char)
    [InlineData("何？？", LexicalUnitType.Sentence)] // Double question (checks last char)
    public void ClassifyHeuristic_JapaneseCjkVariantPunctuation_ReturnsSentence(
        string term, LexicalUnitType expected)
    {
        var result = VocabularyClassificationBackfillService.ClassifyHeuristic(term, tags: null);
        result.Should().Be(expected, $"term '{term}' with terminal CJK punctuation should be Sentence");
    }

    [Fact]
    public void ClassifyHeuristic_CjkFullWidthSpaceOnly_ReturnsWord()
    {
        // CJK full-width space U+3000 is treated as whitespace, but "안녕 " only has trailing space which gets trimmed
        // After trim, "안녕" has no whitespace and no terminal punctuation, so it's Word
        var result = VocabularyClassificationBackfillService.ClassifyHeuristic("안녕 ", null);
        result.Should().Be(LexicalUnitType.Word, "term with only trailing CJK space gets trimmed first");
    }

    [Theory]
    [InlineData("Hello.   ", LexicalUnitType.Sentence)]
    [InlineData("  How are you?  ", LexicalUnitType.Sentence)]
    [InlineData("   Stop!   ", LexicalUnitType.Sentence)]
    public void ClassifyHeuristic_MultipleTrailingSpacesWithPunctuation_ReturnsSentence(
        string term, LexicalUnitType expected)
    {
        var result = VocabularyClassificationBackfillService.ClassifyHeuristic(term, null);
        result.Should().Be(expected, $"term '{term}' with trailing spaces should be trimmed and classified as Sentence");
    }

    [Theory]
    [InlineData("??!", LexicalUnitType.Sentence)]
    [InlineData("...", LexicalUnitType.Sentence)]
    [InlineData("?", LexicalUnitType.Sentence)]
    public void ClassifyHeuristic_PurePunctuation_ReturnsSentence(
        string term, LexicalUnitType expected)
    {
        var result = VocabularyClassificationBackfillService.ClassifyHeuristic(term, null);
        result.Should().Be(expected, $"pure punctuation '{term}' should not crash and returns Sentence");
    }

    [Theory]
    [InlineData("well-being", LexicalUnitType.Word)]
    [InlineData("self-care", LexicalUnitType.Word)]
    [InlineData("re-do", LexicalUnitType.Word)]
    public void ClassifyHeuristic_AsciiWordWithHyphen_ReturnsWord(
        string term, LexicalUnitType expected)
    {
        var result = VocabularyClassificationBackfillService.ClassifyHeuristic(term, null);
        result.Should().Be(expected, $"hyphenated word '{term}' with no whitespace should be Word");
    }

    #endregion

    #region Korean Nuance Tests (Scenarios 18-22)

    [Fact]
    public void ClassifyHeuristic_KoreanCompoundVerbNoPunctuation_ReturnsWord()
    {
        var result = VocabularyClassificationBackfillService.ClassifyHeuristic("공부하다", null);
        result.Should().Be(LexicalUnitType.Word, "공부하다 (4 chars, no punctuation, no whitespace) should be Word");
    }

    [Fact]
    public void ClassifyHeuristic_KoreanCompoundVerbWithSpace_ReturnsPhrase()
    {
        var result = VocabularyClassificationBackfillService.ClassifyHeuristic("공부 하다", null);
        result.Should().Be(LexicalUnitType.Phrase, "공부 하다 (with space) should be Phrase");
    }

    [Fact]
    public void ClassifyHeuristic_KoreanVerbWithPeriod_ReturnsSentence()
    {
        var result = VocabularyClassificationBackfillService.ClassifyHeuristic("공부하다.", null);
        result.Should().Be(LexicalUnitType.Sentence, "공부하다. (with period) should be Sentence");
    }

    [Fact]
    public void ClassifyHeuristic_KoreanSubjectVerbWithParticle_ReturnsPhrase()
    {
        var result = VocabularyClassificationBackfillService.ClassifyHeuristic("밥을 먹었어요", null);
        result.Should().Be(LexicalUnitType.Phrase, "밥을 먹었어요 (subject + verb with particle) should be Phrase");
    }

    [Fact]
    public void ClassifyHeuristic_KoreanGreetingWithTerminal_ReturnsSentence()
    {
        var result = VocabularyClassificationBackfillService.ClassifyHeuristic("안녕하세요.", null);
        result.Should().Be(LexicalUnitType.Sentence, "안녕하세요. (greeting with terminal) should be Sentence");
    }

    #endregion

    #region Idempotency Test

    [Theory]
    [InlineData("hello", "phrase")]
    [InlineData("How are you?", null)]
    [InlineData("공부하다", null)]
    [InlineData("밥을 먹었어요", null)]
    public void ClassifyHeuristic_CalledTwiceOnSameInput_ReturnsSameResult(string term, string? tags)
    {
        var result1 = VocabularyClassificationBackfillService.ClassifyHeuristic(term, tags);
        var result2 = VocabularyClassificationBackfillService.ClassifyHeuristic(term, tags);
        
        result1.Should().Be(result2, "ClassifyHeuristic should be deterministic and idempotent");
    }

    #endregion
}
