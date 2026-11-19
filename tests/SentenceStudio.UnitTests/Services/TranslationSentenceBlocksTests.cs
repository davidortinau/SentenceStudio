using FluentAssertions;
using SentenceStudio.Shared.Services;

namespace SentenceStudio.UnitTests.Services;

/// <summary>
/// Issue 233: the Translation blocks are the words of the sentence being translated, so the
/// learner can assemble the sentence from them.
/// </summary>
public sealed class TranslationSentenceBlocksTests
{
    private const string Sentence = "시장에 사람이 사흘 동안 더 많았어요.";
    private static readonly string[] Words = ["시장에", "사람이", "사흘", "동안", "더", "많았어요"];

    [Fact]
    public void Blocks_AreTheSentenceWords_WhateverTheAiListed()
    {
        // The AI lists dictionary forms and words that are not in the sentence at all.
        var blocks = TranslationSentenceBlocks.Build(Sentence, ["시장", "많다", "도착하다", "엽서"]);

        blocks.Should().BeEquivalentTo(Words);
    }

    [Fact]
    public void Blocks_AreShuffled_AndTheOrderShownSaysNothingAboutTheSentence()
    {
        var orders = new HashSet<string>(StringComparer.Ordinal);
        for (var seed = 0; seed < 200; seed++)
        {
            var blocks = TranslationSentenceBlocks.Build(Sentence, random: new Random(seed));

            blocks.Should().BeEquivalentTo(Words);
            orders.Add(string.Join(' ', blocks));
        }
        orders.Count.Should().BeGreaterThan(100, "six words have 720 orders");

        // A two-word sentence comes either way round, not always reversed.
        var twoWords = Enumerable.Range(0, 50)
            .Select(seed => string.Join(' ', TranslationSentenceBlocks.Build("지금 가요.", random: new Random(seed))))
            .ToHashSet(StringComparer.Ordinal);
        twoWords.Should().BeEquivalentTo(["지금 가요", "가요 지금"]);
    }

    [Fact]
    public void Blocks_KeepARepeatedWordAndDropTheSentencePunctuationAroundWords()
    {
        TranslationSentenceBlocks.SentenceWords("  \"네, 네!\"  정말요?  ...  ")
            .Should().Equal("네", "네", "정말요");
        TranslationSentenceBlocks.SentenceWords("“좋아요~” ¿Dónde está el café-bar?")
            .Should().Equal("좋아요", "Dónde", "está", "el", "café-bar");
        TranslationSentenceBlocks.Build("네 네.").Should().Equal("네", "네");
    }

    [Fact]
    public void Blocks_KeepTheMarksThatBelongToAWord()
    {
        TranslationSentenceBlocks.SentenceWords("오늘은 -5도이고 습도는 50% (아마) 돼요.")
            .Should().Equal("오늘은", "-5도이고", "습도는", "50%", "(아마)", "돼요");
    }

    [Fact]
    public void Blocks_UseTheAiComponents_OnlyForASentenceWithoutSpacesThatTheyWrite()
    {
        // No spaces between words: the AI's components, when they are the sentence in order.
        TranslationSentenceBlocks.Build("私は学生です。", ["私", "は", " 学生 ", "", null, "です"])
            .Should().BeEquivalentTo(["私", "は", "学生", "です"]);
        // Dictionary forms do not write the sentence: it stays whole.
        TranslationSentenceBlocks.Build("私は学生でした。", ["私", "学生", "です"]).Should().Equal("私は学生でした");
        // A one-word sentence keeps its word, whatever the AI listed.
        TranslationSentenceBlocks.Build("안녕하세요.", ["안녕하다", "안녕"]).Should().Equal("안녕하세요");
        TranslationSentenceBlocks.Build("가요.", ["가다"]).Should().Equal("가요");
    }

    [Fact]
    public void Blocks_AreEmpty_WhenThereIsNoSentence()
    {
        TranslationSentenceBlocks.Build(" ").Should().BeEmpty();
        TranslationSentenceBlocks.Build(null, ["가다", "오다"]).Should().BeEmpty();
    }
}
