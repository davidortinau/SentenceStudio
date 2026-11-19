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
    public void Blocks_NeverComeInTheSentenceOrder()
    {
        for (var seed = 0; seed < 500; seed++)
        {
            var blocks = TranslationSentenceBlocks.Build(Sentence, random: new Random(seed));

            blocks.Should().BeEquivalentTo(Words);
            blocks.Should().NotEqual(Words, "seed {0} would show the answer, read left to right", seed);
        }

        for (var seed = 0; seed < 50; seed++)
            TranslationSentenceBlocks.Build("가요 지금", random: new Random(seed)).Should().Equal("지금", "가요");
    }

    [Fact]
    public void Blocks_KeepARepeatedWordAndDropThePunctuationAroundWords()
    {
        TranslationSentenceBlocks.SentenceWords("  \"네, 네!\"  정말요?  -  ")
            .Should().Equal("네", "네", "정말요");
        TranslationSentenceBlocks.SentenceWords("¿Dónde está el café-bar?")
            .Should().Equal("Dónde", "está", "el", "café-bar");
        TranslationSentenceBlocks.Build("네 네").Should().Equal("네", "네");
    }

    [Fact]
    public void Blocks_FallBackToTheAiComponents_OnlyWhenTheSentenceHasNoSpaces()
    {
        TranslationSentenceBlocks.Build("私は学生です。", ["私", "は", " 学生 ", "", null, "です"])
            .Should().BeEquivalentTo(["私", "は", "学生", "です"]);
        TranslationSentenceBlocks.Build("가요.", ["가다"]).Should().Equal("가요");
        TranslationSentenceBlocks.Build(null, ["가다"]).Should().Equal("가다");
    }

    [Fact]
    public void Blocks_AreEmpty_WhenThereIsNoSentenceAndNoComponents()
    {
        TranslationSentenceBlocks.Build(" ").Should().BeEmpty();
        TranslationSentenceBlocks.Build(null, [" ", null]).Should().BeEmpty();
    }
}
