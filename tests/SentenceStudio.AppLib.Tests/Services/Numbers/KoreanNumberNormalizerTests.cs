using SentenceStudio.Services.Numbers;
using Xunit;

namespace SentenceStudio.AppLib.Tests.Services.Numbers;

public class KoreanNumberNormalizerTests
{
    [Theory]
    [InlineData("만 오천 원", "15000 원")]
    [InlineData("만오천원", "15000원")]
    [InlineData("이만 삼천 원", "23000 원")]
    [InlineData("이만삼천원", "23000원")]
    [InlineData("오천 원", "5000 원")]
    [InlineData("오천원", "5000원")]
    [InlineData("천 원", "1000 원")]
    [InlineData("천원", "1000원")]
    [InlineData("만 원", "10000 원")]
    [InlineData("만원", "10000원")]
    [InlineData("백오십 원", "150 원")]
    [InlineData("백오십원", "150원")]
    [InlineData("삼십칠", "37")]
    [InlineData("삼십칠 원", "37 원")]
    public void GenerateEquivalentForms_SinoAdditiveComposition_ParsesCorrectly(string input, string expectedContains)
    {
        // This tests the underlying normalization that happens during GenerateEquivalentForms
        var forms = KoreanNumberNormalizer.GenerateEquivalentForms(input, Shared.Models.Numbers.NumberSystem.Sino);
        
        // The bare digit form should be generated
        Assert.Contains(expectedContains, forms);
    }

    [Theory]
    [InlineData("십만 원", "100000 원")]      // 십 × 만 = 10 × 10,000 = 100,000
    [InlineData("십만원", "100000원")]
    [InlineData("백만 원", "1000000 원")]     // 백 × 만 = 100 × 10,000 = 1,000,000
    [InlineData("백만원", "1000000원")]
    [InlineData("천만 원", "10000000 원")]    // 천 × 만 = 1,000 × 10,000 = 10,000,000
    [InlineData("천만원", "10000000원")]
    [InlineData("이십만 원", "200000 원")]    // 이십 × 만 = 20 × 10,000 = 200,000
    [InlineData("이십만원", "200000원")]
    [InlineData("십이만 오천 원", "125000 원")] // (십이 × 만) + 오천 = (12 × 10,000) + 5,000 = 125,000
    [InlineData("십이만 오천원", "125000원")]
    [InlineData("백오십만 원", "1500000 원")]  // 백오십 × 만 = 150 × 10,000 = 1,500,000
    [InlineData("백오십만원", "1500000원")]
    public void GenerateEquivalentForms_SinoMyriadMultiplication_ParsesCorrectly(string input, string expectedContains)
    {
        // Tests multiplicative scaling at myriad boundaries (만, 억)
        var forms = KoreanNumberNormalizer.GenerateEquivalentForms(input, Shared.Models.Numbers.NumberSystem.Sino);
        
        // The bare digit form should be generated
        Assert.Contains(expectedContains, forms);
    }

    [Theory]
    [InlineData("스물 셋", "23")]              // Native compound: 20 + 3
    [InlineData("마흔 다섯", "45")]            // Native compound: 40 + 5
    [InlineData("아흔 아홉", "99")]            // Native compound: 90 + 9
    [InlineData("스물 셋 개", "23 개")]
    [InlineData("마흔 다섯 개", "45 개")]
    public void GenerateEquivalentForms_NativeCompounds_ParsesCorrectly(string input, string expectedContains)
    {
        // Tests Native Korean compound numbers (1-99 only)
        var forms = KoreanNumberNormalizer.GenerateEquivalentForms(input, Shared.Models.Numbers.NumberSystem.Native);
        
        // The bare digit form should be generated
        Assert.Contains(expectedContains, forms);
    }
}
