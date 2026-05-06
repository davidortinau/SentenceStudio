using SentenceStudio.Services.Numbers;
using SentenceStudio.Shared.Models.Numbers;
using Xunit;
using Microsoft.Extensions.Logging.Abstractions;

namespace SentenceStudio.AppLib.Tests.Services.Numbers;

/// <summary>
/// Tests for narrow permissive normalization rules in NumberDrill grader:
/// - Trailing punctuation tolerance
/// - Fullwidth digit normalization (０-９ → 0-9)
/// 
/// These tests document Captain's Phase 1 decision: normalize ONLY trailing punctuation
/// and fullwidth digits. No fuzzy matching, no Levenshtein, no internal punctuation permissiveness.
/// </summary>
public class KoreanNumberAnswerGrader_NormalizationTests
{
    private readonly KoreanNumberAnswerGrader _grader = new();
    private readonly KoreanNumberItemGenerator _generator = new(NullLogger<KoreanNumberItemGenerator>.Instance);

    #region Trailing Punctuation Tolerance

    [Theory]
    [InlineData("100000원.", "십만 원")] // Period
    [InlineData("100000원,", "십만 원")] // Comma
    [InlineData("100000원!", "십만 원")] // Exclamation
    [InlineData("100000원?", "십만 원")] // Question mark
    [InlineData("100000원。", "십만 원")] // Japanese period
    [InlineData("100000원？", "십만 원")] // Japanese question mark
    public void Grade_TrailingPunctuation_IsStrippedAndAccepted(string userInput, string canonical)
    {
        // Find or create Money item with value 100000 (십만 원)
        NumberItem item = null!;
        for (int seed = 0; seed < 10000; seed++)
        {
            var candidate = _generator.GenerateItem(new NumberItemRequest("Money", "ReadAndProduce", RandomSeed: seed));
            if (candidate.DigitValue == 100000 && candidate.CounterText == "원")
            {
                item = candidate;
                break;
            }
        }
        
        Assert.NotNull(item);
        Assert.Equal(canonical, item.CanonicalAnswer);
        
        var result = _grader.Grade(item, userInput, latencyMs: 1000);
        
        Assert.True(result.IsCorrect, 
            $"Expected '{userInput}' to be accepted after stripping trailing punctuation. " +
            $"Canonical: '{canonical}', Error: {result.ErrorClass}");
    }

    [Theory]
    [InlineData("만 원!", "만 원")]
    [InlineData("만원.", "만 원")]
    [InlineData("만 원。", "만 원")]
    public void Grade_TrailingPunctuation_Korean_IsStrippedAndAccepted(string userInput, string canonical)
    {
        // Find item with value 10000 (만 원)
        NumberItem item = null!;
        for (int seed = 0; seed < 10000; seed++)
        {
            var candidate = _generator.GenerateItem(new NumberItemRequest("Money", "ReadAndProduce", RandomSeed: seed));
            if (candidate.DigitValue == 10000 && candidate.CounterText == "원")
            {
                item = candidate;
                break;
            }
        }
        
        Assert.NotNull(item);
        
        var result = _grader.Grade(item, userInput, latencyMs: 1000);
        
        Assert.True(result.IsCorrect, 
            $"Expected '{userInput}' to be accepted after stripping trailing punctuation. " +
            $"Error: {result.ErrorClass}");
    }

    [Theory]
    [InlineData("15000 원。", "만 오천 원")] // Japanese period
    [InlineData("15000 원？", "만 오천 원")] // Japanese question
    [InlineData("15000원!", "만 오천 원")]
    [InlineData("15000원,", "만 오천 원")]
    public void Grade_TrailingPunctuation_DigitsWithCounter_IsStrippedAndAccepted(string userInput, string canonical)
    {
        // Find item with value 15000 (만 오천 원)
        NumberItem item = null!;
        for (int seed = 0; seed < 10000; seed++)
        {
            var candidate = _generator.GenerateItem(new NumberItemRequest("Money", "ReadAndProduce", RandomSeed: seed));
            if (candidate.DigitValue == 15000 && candidate.CounterText == "원")
            {
                item = candidate;
                break;
            }
        }
        
        Assert.NotNull(item);
        
        var result = _grader.Grade(item, userInput, latencyMs: 1000);
        
        Assert.True(result.IsCorrect, 
            $"Expected '{userInput}' to be accepted after stripping trailing punctuation. " +
            $"Canonical: '{canonical}', Error: {result.ErrorClass}");
    }

    [Theory]
    [InlineData("100 개!", "백 개")]
    [InlineData("100개.", "백 개")]
    [InlineData("백 개。", "백 개")]
    public void Grade_TrailingPunctuation_Counting_IsStrippedAndAccepted(string userInput, string canonical)
    {
        // Find item with value 100 counting (백 개)
        NumberItem item = null!;
        for (int seed = 0; seed < 10000; seed++)
        {
            var candidate = _generator.GenerateItem(new NumberItemRequest("Counting", "ReadAndProduce", CounterId: "개", RandomSeed: seed));
            if (candidate.DigitValue == 100)
            {
                item = candidate;
                break;
            }
        }
        
        Assert.NotNull(item);
        
        var result = _grader.Grade(item, userInput, latencyMs: 1000);
        
        Assert.True(result.IsCorrect, 
            $"Expected '{userInput}' to be accepted after stripping trailing punctuation. " +
            $"Canonical: '{canonical}', Error: {result.ErrorClass}");
    }

    [Fact(Skip = "Punctuation INSIDE number is NOT accepted per Captain decision - flag for review")]
    public void Grade_InternalPunctuation_IsRejected()
    {
        // This test documents that commas INSIDE numbers (e.g., "15,000원") are NOT normalized
        // Captain decision: only trailing punctuation is stripped. Internal punctuation needs
        // a separate decision.
        
        NumberItem item = null!;
        for (int seed = 0; seed < 10000; seed++)
        {
            var candidate = _generator.GenerateItem(new NumberItemRequest("Money", "ReadAndProduce", RandomSeed: seed));
            if (candidate.DigitValue == 15000 && candidate.CounterText == "원")
            {
                item = candidate;
                break;
            }
        }
        
        Assert.NotNull(item);
        
        var result = _grader.Grade(item, "15,000원", latencyMs: 1000);
        
        // Currently this will be rejected. If Captain decides to accept it, remove Skip and flip assertion.
        Assert.False(result.IsCorrect, 
            "Internal punctuation (comma separator) should be rejected per current decision");
    }

    #endregion

    #region Fullwidth Digit Normalization

    [Theory]
    [InlineData("１００００원", "만 원")] // Full set of fullwidth
    [InlineData("１５０００ 원", "만 오천 원")]
    [InlineData("１００ 개", "백 개")]
    [InlineData("１５０원", "백오십 원")]
    public void Grade_FullwidthDigits_AreNormalizedToHalfwidthAndAccepted(string userInput, string canonical)
    {
        // Parse the expected digit value from userInput
        var digitsPart = new string(userInput.TakeWhile(c => 
            (c >= '０' && c <= '９') || (c >= '0' && c <= '9')).ToArray());
        
        // Convert fullwidth to halfwidth for extraction
        var normalized = digitsPart
            .Replace('０', '0').Replace('１', '1').Replace('２', '2')
            .Replace('３', '3').Replace('４', '4').Replace('５', '5')
            .Replace('６', '6').Replace('７', '7').Replace('８', '8')
            .Replace('９', '9');
        
        var expectedValue = int.Parse(normalized);
        var category = userInput.Contains("원") ? "Money" : "Counting";
        var counterId = userInput.Contains("원") ? null : "개";
        
        NumberItem item = null!;
        for (int seed = 0; seed < 10000; seed++)
        {
            var candidate = _generator.GenerateItem(new NumberItemRequest(category, "ReadAndProduce", CounterId: counterId, RandomSeed: seed));
            if (candidate.DigitValue == expectedValue)
            {
                item = candidate;
                break;
            }
        }
        
        Assert.NotNull(item);
        
        var result = _grader.Grade(item, userInput, latencyMs: 1000);
        
        Assert.True(result.IsCorrect, 
            $"Expected '{userInput}' (fullwidth) to be normalized and accepted. " +
            $"Canonical: '{canonical}', Error: {result.ErrorClass}");
    }

    [Theory]
    [InlineData("1００00원", "만 원")] // Mixed: halfwidth + fullwidth
    [InlineData("１5000 원", "만 오천 원")] // Mixed: fullwidth start + halfwidth
    [InlineData("15０00원", "만 오천 원")] // Mixed: scattered fullwidth
    public void Grade_MixedWidthDigits_AreNormalizedAndAccepted(string userInput, string canonical)
    {
        // Parse the expected digit value from userInput
        var digitsPart = new string(userInput.TakeWhile(c => 
            (c >= '０' && c <= '９') || (c >= '0' && c <= '9')).ToArray());
        
        // Convert fullwidth to halfwidth for extraction
        var normalized = digitsPart
            .Replace('０', '0').Replace('１', '1').Replace('２', '2')
            .Replace('３', '3').Replace('４', '4').Replace('５', '5')
            .Replace('６', '6').Replace('７', '7').Replace('８', '8')
            .Replace('９', '9');
        
        var expectedValue = int.Parse(normalized);
        
        NumberItem item = null!;
        for (int seed = 0; seed < 10000; seed++)
        {
            var candidate = _generator.GenerateItem(new NumberItemRequest("Money", "ReadAndProduce", RandomSeed: seed));
            if (candidate.DigitValue == expectedValue && candidate.CounterText == "원")
            {
                item = candidate;
                break;
            }
        }
        
        Assert.NotNull(item);
        
        var result = _grader.Grade(item, userInput, latencyMs: 1000);
        
        Assert.True(result.IsCorrect, 
            $"Expected '{userInput}' (mixed width) to be normalized and accepted. " +
            $"Canonical: '{canonical}', Error: {result.ErrorClass}");
    }

    [Theory]
    [InlineData("１００００원.", "만 원")] // Fullwidth + trailing period
    [InlineData("１５０００ 원!", "만 오천 원")] // Fullwidth + trailing exclamation
    [InlineData("１００ 개。", "백 개")] // Fullwidth + Japanese period
    public void Grade_FullwidthDigits_WithTrailingPunctuation_BothNormalizationsApply(string userInput, string canonical)
    {
        // This tests that BOTH normalizations work together:
        // 1. Fullwidth digits → halfwidth
        // 2. Trailing punctuation → stripped
        
        var digitsPart = new string(userInput.TakeWhile(c => 
            (c >= '０' && c <= '９') || (c >= '0' && c <= '9')).ToArray());
        
        var normalized = digitsPart
            .Replace('０', '0').Replace('１', '1').Replace('２', '2')
            .Replace('３', '3').Replace('４', '4').Replace('５', '5')
            .Replace('６', '6').Replace('７', '7').Replace('８', '8')
            .Replace('９', '9');
        
        var expectedValue = int.Parse(normalized);
        var category = userInput.Contains("원") ? "Money" : "Counting";
        var counterId = userInput.Contains("원") ? null : "개";
        
        NumberItem item = null!;
        for (int seed = 0; seed < 10000; seed++)
        {
            var candidate = _generator.GenerateItem(new NumberItemRequest(category, "ReadAndProduce", CounterId: counterId, RandomSeed: seed));
            if (candidate.DigitValue == expectedValue)
            {
                item = candidate;
                break;
            }
        }
        
        Assert.NotNull(item);
        
        var result = _grader.Grade(item, userInput, latencyMs: 1000);
        
        Assert.True(result.IsCorrect, 
            $"Expected '{userInput}' (fullwidth + trailing punct) to be normalized and accepted. " +
            $"Canonical: '{canonical}', Error: {result.ErrorClass}");
    }

    #endregion

    #region Confirm Existing Tests Still Pass
    
    [Fact]
    public void Regression_ExactMatch_StillWorks()
    {
        // Verify existing exact-match behavior isn't broken
        var item = _generator.GenerateItem(new NumberItemRequest("Counting", "ReadAndProduce", CounterId: "개", RandomSeed: 42));
        var result = _grader.Grade(item, item.CanonicalAnswer, latencyMs: 1000);

        Assert.True(result.IsCorrect);
        Assert.Null(result.ErrorClass);
    }

    [Fact]
    public void Regression_SystemAwareGrading_StillWorks()
    {
        // Verify system-aware grading (Directive 2026-05-06) still works
        NumberItem item = null!;
        for (int seed = 0; seed < 10000; seed++)
        {
            var candidate = _generator.GenerateItem(new NumberItemRequest("Counting", "ReadAndProduce", CounterId: "개", RandomSeed: seed));
            if (candidate.DigitValue == 46)
            {
                item = candidate;
                break;
            }
        }
        
        Assert.NotNull(item);
        Assert.Equal(NumberSystem.Native, item.System);
        
        // Should reject Sino with Native counter
        var result = _grader.Grade(item, "사십육 개", latencyMs: 1000);
        Assert.False(result.IsCorrect);
        Assert.Equal("SinoNativeSwap", result.ErrorClass);
        
        // Should accept bare digit
        result = _grader.Grade(item, "46", latencyMs: 1000);
        Assert.True(result.IsCorrect);
    }

    #endregion
}
