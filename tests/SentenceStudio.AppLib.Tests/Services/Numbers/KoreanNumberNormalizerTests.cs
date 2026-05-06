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
}
