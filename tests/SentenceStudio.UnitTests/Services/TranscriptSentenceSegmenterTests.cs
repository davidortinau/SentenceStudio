using FluentAssertions;
using SentenceStudio.Services;

namespace SentenceStudio.UnitTests.Services;

public sealed class TranscriptSentenceSegmenterTests
{
    [Fact]
    public void Split_SplitsKoreanTranscriptOnAsciiAndKoreanSentencePunctuation()
    {
        var sentences = TranscriptSentenceSegmenter.Split("안녕하세요. 오늘은 날씨가 좋아요? 네! 다음 문장입니다。 끝났어요！ 정말요？");

        sentences.Should().BeEquivalentTo(new[]
        {
            "안녕하세요.",
            "오늘은 날씨가 좋아요?",
            "네!",
            "다음 문장입니다。",
            "끝났어요！",
            "정말요？"
        }, options => options.WithStrictOrdering());
    }

    [Fact]
    public void Split_DefaultMode_CollapsesNewlineBoundariesWithoutSplitting()
    {
        var sentences = TranscriptSentenceSegmenter.Split("""
            첫 번째 문장입니다
            두 번째 문장입니다
            세 번째 문장입니다
            """);

        sentences.Should().BeEquivalentTo(new[]
        {
            "첫 번째 문장입니다 두 번째 문장입니다 세 번째 문장입니다."
        }, options => options.WithStrictOrdering());
    }

    [Fact]
    public void Split_NewlineMode_SplitsOnNewlinesAndDropsEmptySegments()
    {
        var sentences = TranscriptSentenceSegmenter.Split("""
            첫 번째 문장입니다

            두 번째 문장입니다
              세 번째 문장입니다
            """, splitOnNewlines: true);

        sentences.Should().BeEquivalentTo(new[]
        {
            "첫 번째 문장입니다.",
            "두 번째 문장입니다.",
            "세 번째 문장입니다."
        }, options => options.WithStrictOrdering());
    }

    [Fact]
    public void Split_NewlineMode_StillSplitsPunctuationWithinEachLine()
    {
        var sentences = TranscriptSentenceSegmenter.Split("""
            첫 번째 문장입니다. 두 번째 문장인가요?
            세 번째 문장입니다！네 번째 문장입니다。
            """, splitOnNewlines: true);

        sentences.Should().BeEquivalentTo(new[]
        {
            "첫 번째 문장입니다.",
            "두 번째 문장인가요?",
            "세 번째 문장입니다！",
            "네 번째 문장입니다。"
        }, options => options.WithStrictOrdering());
    }
}
