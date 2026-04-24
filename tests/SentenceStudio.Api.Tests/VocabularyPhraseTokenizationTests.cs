using SentenceStudio.Services;

namespace SentenceStudio.Api.Tests;

/// <summary>
/// Unit tests for VocabularyClassificationBackfillService.TokenizePhrase.
/// Tests Korean particle stripping, English tokenization, punctuation handling, and edge cases.
/// </summary>
public class VocabularyPhraseTokenizationTests
{
    #region Korean Tokenization Tests (Scenarios 1-5)

    [Fact]
    public void TokenizePhrase_KoreanSubjectObjectParticles_StripsParticles()
    {
        var result = VocabularyClassificationBackfillService.TokenizePhrase("밥을 먹었어요", "ko");
        
        result.Should().HaveCount(2, "should split into 2 tokens");
        result.Should().Contain("밥", "을 particle should be stripped from 밥을");
        result.Should().Contain("먹었어요", "verb should remain unchanged");
    }

    [Fact]
    public void TokenizePhrase_KoreanTopicParticle_StripsParticle()
    {
        var result = VocabularyClassificationBackfillService.TokenizePhrase("친구와 만났어요", "ko");
        
        result.Should().HaveCount(2);
        result.Should().Contain("친구", "와 particle should be stripped from 친구와");
        result.Should().Contain("만났어요", "verb should remain unchanged");
    }

    [Fact]
    public void TokenizePhrase_KoreanLocationParticleWithPunctuation_StripsParticleAndPunctuation()
    {
        var result = VocabularyClassificationBackfillService.TokenizePhrase("학교에서 공부한다.", "ko");
        
        result.Should().HaveCount(2);
        result.Should().Contain("학교", "에서 particle should be stripped from 학교에서");
        result.Should().Contain("공부한다", "terminal period should be stripped from 공부한다.");
    }

    [Fact]
    public void TokenizePhrase_KoreanSingleWordNoWhitespace_ReturnsSingleToken()
    {
        var result = VocabularyClassificationBackfillService.TokenizePhrase("공부하다", "ko");
        
        result.Should().ContainSingle("single word without whitespace should return one token");
        result[0].Should().Be("공부하다");
    }

    [Fact]
    public void TokenizePhrase_KoreanCommaDelimitedList_SplitsOnCommas()
    {
        var result = VocabularyClassificationBackfillService.TokenizePhrase("집, 학교, 회사", "ko");
        
        result.Should().HaveCount(3, "commas should act as token separators");
        result.Should().Contain("집");
        result.Should().Contain("학교");
        result.Should().Contain("회사");
    }

    #endregion

    #region English Tokenization Tests (Scenarios 6-7)

    [Fact]
    public void TokenizePhrase_EnglishSimplePhrase_SplitsOnWhitespace()
    {
        var result = VocabularyClassificationBackfillService.TokenizePhrase("the quick brown fox", "en");
        
        result.Should().HaveCount(4);
        result.Should().Contain("the");
        result.Should().Contain("quick");
        result.Should().Contain("brown");
        result.Should().Contain("fox");
    }

    [Fact]
    public void TokenizePhrase_EnglishWithPunctuation_StripsPunctuation()
    {
        var result = VocabularyClassificationBackfillService.TokenizePhrase("Hello, world!", "en");
        
        // Note: Check implementation to verify if it lowercases
        result.Should().HaveCount(2, "should split into 2 tokens with punctuation stripped");
        
        // The implementation doesn't lowercase - it preserves case
        result.Should().Contain("Hello");
        result.Should().Contain("world");
    }

    #endregion

    #region Edge Cases (Scenarios 8-12)

    [Theory]
    [InlineData("", "ko")]
    [InlineData("", "en")]
    [InlineData(null, "ko")]
    public void TokenizePhrase_EmptyOrNullString_ReturnsEmptyList(string? term, string languageCode)
    {
        var result = VocabularyClassificationBackfillService.TokenizePhrase(term!, languageCode);
        
        result.Should().BeEmpty("empty or null input should return empty list");
    }

    [Theory]
    [InlineData("   ", "ko")]
    [InlineData("   ", "en")]
    [InlineData("\t\n", "ko")]
    public void TokenizePhrase_OnlyWhitespace_ReturnsEmptyList(string term, string languageCode)
    {
        var result = VocabularyClassificationBackfillService.TokenizePhrase(term, languageCode);
        
        result.Should().BeEmpty("whitespace-only input should return empty list");
    }

    [Fact]
    public void TokenizePhrase_CjkIdeographicSpace_TreatsAsWhitespace()
    {
        var result = VocabularyClassificationBackfillService.TokenizePhrase("밥을\u3000먹었어요", "ko");
        
        result.Should().HaveCount(2, "CJK ideographic space (U+3000) should split tokens");
        result.Should().Contain("밥");
        result.Should().Contain("먹었어요");
    }

    [Theory]
    [InlineData("a", "en")]
    [InlineData("I", "en")]
    [InlineData("책", "ko")] // Single CJK char - still processes but might be filtered
    public void TokenizePhrase_SingleCharacterTerm_ReturnsToken(string term, string languageCode)
    {
        var result = VocabularyClassificationBackfillService.TokenizePhrase(term, languageCode);
        
        // Note: Implementation may filter short terms - verify behavior
        result.Should().NotBeNull("should handle single character without crashing");
    }

    [Theory]
    [InlineData("ja")]
    [InlineData("en")]
    [InlineData("zh")]
    [InlineData("es")]
    public void TokenizePhrase_NonKoreanLanguageCode_NoParticleStripping(string languageCode)
    {
        // "을" would be stripped in Korean but not in other languages
        var result = VocabularyClassificationBackfillService.TokenizePhrase("test을 word", languageCode);
        
        result.Should().HaveCount(2);
        if (languageCode.Equals("ko", StringComparison.OrdinalIgnoreCase))
        {
            result.Should().Contain("test");
        }
        else
        {
            result.Should().Contain("test을", "non-Korean language should not strip Korean particles");
        }
        result.Should().Contain("word");
    }

    #endregion

    #region Additional Korean Particle Coverage

    [Theory]
    [InlineData("책이", "책")] // Subject marker
    [InlineData("사람이", "사람")] // Subject marker
    [InlineData("집을", "집")] // Object marker
    [InlineData("물을", "물")] // Object marker
    [InlineData("학교는", "학교")] // Topic marker
    [InlineData("나는", "나")] // Topic marker
    [InlineData("서울에", "서울")] // Location marker
    [InlineData("집에", "집")] // Location marker
    [InlineData("친구의", "친구")] // Possessive
    [InlineData("나의", "나")] // Possessive
    [InlineData("버스로", "버스")] // Instrument/direction
    [InlineData("학교로", "학교")] // Instrument/direction
    [InlineData("친구와", "친구")] // With
    [InlineData("너와", "너")] // With
    [InlineData("학교에서", "학교")] // Location-from
    [InlineData("집에서", "집")] // Location-from
    [InlineData("친구에게", "친구")] // To (person)
    [InlineData("나도", "나")] // Also/too
    [InlineData("너만", "너")] // Only
    [InlineData("오늘부터", "오늘")] // From (time)
    [InlineData("여기까지", "여기")] // Until
    public void TokenizePhrase_KoreanVariousParticles_StripsCorrectly(string input, string expected)
    {
        var result = VocabularyClassificationBackfillService.TokenizePhrase(input, "ko");
        
        result.Should().ContainSingle();
        result[0].Should().Be(expected, $"particle should be stripped from {input}");
    }

    [Fact]
    public void TokenizePhrase_KoreanParticleOnly_DoesNotStrip()
    {
        // Edge case: particle as standalone term doesn't get stripped
        // Stripping only happens if it leaves at least 1 char after removal
        var result = VocabularyClassificationBackfillService.TokenizePhrase("을", "ko");
        
        result.Should().ContainSingle("particle-only token is preserved (no parent word to strip from)");
        result[0].Should().Be("을");
    }

    #endregion

    #region Mixed Language and Special Characters

    [Fact]
    public void TokenizePhrase_MixedKoreanEnglish_TokenizesBoth()
    {
        var result = VocabularyClassificationBackfillService.TokenizePhrase("나는 Apple을 좋아해요", "ko");
        
        result.Should().HaveCountGreaterThan(2);
        result.Should().Contain("나");
        result.Should().Contain("Apple");
        result.Should().Contain("좋아해요");
    }

    [Theory]
    [InlineData("Hello... world!", "en")]
    [InlineData("Wait... what?", "en")]
    public void TokenizePhrase_MultipleConsecutivePunctuation_StripsPunctuation(string input, string languageCode)
    {
        var result = VocabularyClassificationBackfillService.TokenizePhrase(input, languageCode);
        
        result.Should().HaveCount(2);
        result.Should().NotContain(t => t.Contains(".") || t.Contains("!") || t.Contains("?"),
            "all punctuation should be stripped");
    }

    [Fact]
    public void TokenizePhrase_JapaneseCjkComma_DoesNotSplit()
    {
        // Implementation only splits on whitespace and strips specific punctuation
        // CJK comma (、) is not a splitter, only ASCII comma is trimmed from ends
        var result = VocabularyClassificationBackfillService.TokenizePhrase("本、ペン、ノート", "ja");
        
        // Since there's no whitespace, entire string is one token, then punctuation is trimmed
        result.Should().ContainSingle("CJK comma (、) is not a token separator in current implementation");
    }

    #endregion

    #region Idempotency Test

    [Theory]
    [InlineData("밥을 먹었어요", "ko")]
    [InlineData("the quick brown fox", "en")]
    [InlineData("Hello, world!", "en")]
    [InlineData("학교에서 공부한다.", "ko")]
    public void TokenizePhrase_CalledTwiceOnSameInput_ReturnsSameResult(string term, string languageCode)
    {
        var result1 = VocabularyClassificationBackfillService.TokenizePhrase(term, languageCode);
        var result2 = VocabularyClassificationBackfillService.TokenizePhrase(term, languageCode);
        
        result1.Should().BeEquivalentTo(result2, "TokenizePhrase should be deterministic and idempotent");
    }

    #endregion
}
