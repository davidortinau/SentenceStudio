using FluentAssertions;
using SentenceStudio.Shared.Models;
using SentenceStudio.Shared.Services;
using Xunit;

namespace SentenceStudio.UnitTests.Services;

/// <summary>
/// Tests for <see cref="WritingActivityVocabularyFilter"/>. The Writing activity must NOT load
/// phrase or sentence entries — they would short-circuit the productive task of composing an
/// original sentence. Per Captain directive (June 2026): "Filter to Word for now."
/// </summary>
public class WritingActivityVocabularyFilterTests
{
    [Fact]
    public void FilterToWordsOnly_KeepsWordTypedEntries()
    {
        var input = new[]
        {
            MakeWord("w1", "책", LexicalUnitType.Word),
            MakeWord("w2", "읽다", LexicalUnitType.Word),
        };

        var result = WritingActivityVocabularyFilter.FilterToWordsOnly(input);

        result.Should().HaveCount(2);
        result.Select(w => w.Id).Should().BeEquivalentTo(new[] { "w1", "w2" });
    }

    [Fact]
    public void FilterToWordsOnly_ExcludesPhraseEntries()
    {
        var input = new[]
        {
            MakeWord("w1", "책", LexicalUnitType.Word),
            MakeWord("p1", "어떻게 지내세요?", LexicalUnitType.Phrase),
        };

        var result = WritingActivityVocabularyFilter.FilterToWordsOnly(input);

        result.Should().ContainSingle().Which.Id.Should().Be("w1");
    }

    [Fact]
    public void FilterToWordsOnly_ExcludesSentenceEntries()
    {
        var input = new[]
        {
            MakeWord("w1", "책", LexicalUnitType.Word),
            MakeWord("s1", "저는 책을 읽어요.", LexicalUnitType.Sentence),
        };

        var result = WritingActivityVocabularyFilter.FilterToWordsOnly(input);

        result.Should().ContainSingle().Which.Id.Should().Be("w1");
    }

    [Fact]
    public void FilterToWordsOnly_ExcludesUnknownEntries()
    {
        // Strict filter per "Filter to Word for now" — Unknown is excluded by design.
        // Empty-state diagnostic logging in the caller surfaces this case for data quality review.
        var input = new[]
        {
            MakeWord("w1", "책", LexicalUnitType.Word),
            MakeWord("u1", "legacy", LexicalUnitType.Unknown),
        };

        var result = WritingActivityVocabularyFilter.FilterToWordsOnly(input);

        result.Should().ContainSingle().Which.Id.Should().Be("w1");
    }

    [Fact]
    public void FilterToWordsOnly_PreservesInputOrder()
    {
        var input = new[]
        {
            MakeWord("s1", "sentence", LexicalUnitType.Sentence),
            MakeWord("w1", "first-word", LexicalUnitType.Word),
            MakeWord("p1", "phrase", LexicalUnitType.Phrase),
            MakeWord("w2", "second-word", LexicalUnitType.Word),
            MakeWord("u1", "unknown", LexicalUnitType.Unknown),
            MakeWord("w3", "third-word", LexicalUnitType.Word),
        };

        var result = WritingActivityVocabularyFilter.FilterToWordsOnly(input);

        result.Select(w => w.Id).Should().Equal("w1", "w2", "w3");
    }

    [Fact]
    public void FilterToWordsOnly_EmptyInput_ReturnsEmpty()
    {
        var result = WritingActivityVocabularyFilter.FilterToWordsOnly(Array.Empty<VocabularyWord>());
        result.Should().BeEmpty();
    }

    [Fact]
    public void FilterToWordsOnly_AllPhrasesOrSentences_ReturnsEmpty()
    {
        var input = new[]
        {
            MakeWord("p1", "phrase one", LexicalUnitType.Phrase),
            MakeWord("s1", "sentence one", LexicalUnitType.Sentence),
            MakeWord("p2", "phrase two", LexicalUnitType.Phrase),
        };

        var result = WritingActivityVocabularyFilter.FilterToWordsOnly(input);

        result.Should().BeEmpty();
    }

    [Fact]
    public void FilterToWordsOnly_NullSource_Throws()
    {
        var act = () => WritingActivityVocabularyFilter.FilterToWordsOnly(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Theory]
    [InlineData(LexicalUnitType.Word, true)]
    [InlineData(LexicalUnitType.Phrase, false)]
    [InlineData(LexicalUnitType.Sentence, false)]
    [InlineData(LexicalUnitType.Unknown, false)]
    public void IsWordEntry_MatchesExpectedClassification(LexicalUnitType type, bool expected)
    {
        var word = MakeWord("x", "term", type);
        WritingActivityVocabularyFilter.IsWordEntry(word).Should().Be(expected);
    }

    [Fact]
    public void IsWordEntry_NullWord_ReturnsFalse()
    {
        WritingActivityVocabularyFilter.IsWordEntry(null!).Should().BeFalse();
    }

    private static VocabularyWord MakeWord(string id, string term, LexicalUnitType type) => new()
    {
        Id = id,
        TargetLanguageTerm = term,
        NativeLanguageTerm = $"native-{id}",
        LexicalUnitType = type
    };
}
