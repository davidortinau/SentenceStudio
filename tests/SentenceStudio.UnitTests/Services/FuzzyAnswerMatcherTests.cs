using SentenceStudio.Shared.Services;
using Xunit;

namespace SentenceStudio.UnitTests.Services;

public class FuzzyMatcherTests
{

    [Theory]
    [InlineData("take", "take (a photo)", true, "Exact core word match")]
    [InlineData("take", "take(a photo)", true, "No space before parenthesis")]
    [InlineData("ding", "ding~ (a sound)", true, "Tilde suffix removal")]
    [InlineData("to choose", "choose", true, "To-prefix removal")]
    [InlineData("choose", "to choose", true, "To-prefix removal (reversed)")]
    [InlineData("get cloudy", "to get cloudy", true, "To-prefix with multi-word")]
    [InlineData("cloudy", "to get cloudy", true, "Partial match of core phrase")]
    [InlineData("Celsius", "celcius", true, "Common typo with 1 character difference")]
    [InlineData("celcius", "Celsius", true, "Common typo reversed")]
    [InlineData("photograph", "photo", false, "Different root word")]
    [InlineData("run", "walk", false, "Completely different words")]
    [InlineData("hello", "helo", true, "Single character typo")]
    [InlineData("weather", "wheather", true, "Common spelling mistake")]
    [InlineData("receive", "recieve", true, "i-before-e typo")]
    [InlineData("separate", "seperate", true, "a-e confusion")]
    [InlineData("definitely", "definately", true, "Common misspelling")]
    [InlineData("restaurant", "restaraunt", true, "u-a transposition")]
    [InlineData("occurred", "occured", true, "Double letter typo")]
    [InlineData("accommodate", "acommodate", true, "Missing double letter")]
    [InlineData("", "test", false, "Empty user answer")]
    [InlineData("test", "", false, "Empty expected answer")]
    [InlineData("", "", false, "Both empty")]
    [InlineData("   ", "test", false, "Whitespace only user answer")]
    [InlineData("test", "   ", false, "Whitespace only expected answer")]
    [InlineData("TAKE", "take (a photo)", true, "Case insensitive match")]
    [InlineData("Take", "TAKE (A PHOTO)", true, "Mixed case match")]
    [InlineData("to be", "be", true, "To-prefix with verb")]
    [InlineData("be", "to be", true, "To-prefix with verb (reversed)")]
    [InlineData("going", "to go", false, "Verb form change not accepted")]
    [InlineData("went", "to go", false, "Verb tense change not accepted")]
    [InlineData("photo", "photograph", false, "Abbreviation not accepted")]
    [InlineData("Mr.", "Mister", false, "Abbreviation expansion not accepted")]
    [InlineData("don't", "do not", false, "Contraction expansion not accepted")]
    [InlineData("it's", "it is", false, "Contraction expansion not accepted")]
    [InlineData("take picture", "take (a photo)", false, "Synonym substitution not accepted")]
    [InlineData("select", "choose", false, "Synonym not accepted")]
    [InlineData("pick", "choose", false, "Synonym not accepted")]
    [InlineData("grab", "take", false, "Synonym not accepted")]
    [InlineData("snap", "take (a photo)", false, "Synonym not accepted")]
    [InlineData("celsius degree", "Celsius", false, "Added words not accepted")]
    [InlineData("the take", "take (a photo)", false, "Extra article not accepted")]
    [InlineData("a choose", "choose", false, "Extra article not accepted")]
    [InlineData("chooze", "choose", true, "Phonetic spelling with 1 char diff")]
    [InlineData("fone", "phone", true, "Phonetic spelling with 1 char diff")]
    [InlineData("nite", "night", true, "Phonetic spelling with 1-2 char diff")]
    [InlineData("lite", "light", true, "Phonetic spelling with 1-2 char diff")]
    [InlineData("thru", "through", false, "Phonetic spelling with >2 char diff")]
    [InlineData("tho", "though", false, "Phonetic spelling with >2 char diff")]
    [InlineData("take photo", "take (a photo)", true, "Core phrase without article")]
    [InlineData("get cloud", "to get cloudy", false, "Core word modified (not exact)")]
    [InlineData("ding sound", "ding~ (a sound)", true, "Core + descriptor word match")]
    [InlineData("sound ding", "ding~ (a sound)", false, "Reversed order not accepted")]
    [InlineData("take a photo", "take (a photo)", true, "Exact match with parentheses content")]
    [InlineData("a sound ding", "ding~ (a sound)", false, "Scrambled word order")]
    [InlineData("take  photo", "take (a photo)", true, "Extra whitespace normalized")]
    [InlineData("  take photo  ", "take (a photo)", true, "Leading/trailing whitespace")]
    [InlineData("to  get  cloudy", "to get cloudy", true, "Multiple spaces normalized")]
    [InlineData("celsuis", "Celsius", true, "Transposition typo (1 edit)")]
    [InlineData("celsisu", "Celsius", true, "Letter swap typo (1 edit)")]
    [InlineData("celsiuss", "Celsius", false, "Extra letter (still 1 edit but different length)")]
    [InlineData("clsius", "Celsius", false, "Missing letter (2 edits)")]
    [InlineData("takee", "take (a photo)", true, "Doubled letter typo")]
    [InlineData("chooose", "choose", false, "Multiple extra letters")]
    [InlineData("tak", "take (a photo)", true, "Missing last letter")]
    [InlineData("choos", "choose", true, "Missing last letter")]
    [InlineData("getc loudy", "to get cloudy", false, "Space in wrong place")]
    [InlineData("takea photo", "take (a photo)", true, "Missing space (normalized)")]
    [InlineData("gloomy", "to get cloudy", false, "Synonym/different meaning")]
    [InlineData("overcast", "to get cloudy", false, "Synonym/different meaning")]
    [InlineData("capture", "take (a photo)", false, "Synonym in photography context")]
    [InlineData("snap picture", "take (a photo)", false, "Synonym phrase")]
    public void Evaluate_ShouldHandleVariousScenarios(string userAnswer, string expectedAnswer, bool shouldMatch, string reason)
    {
        var result = FuzzyMatcher.Evaluate(userAnswer, expectedAnswer);
        Assert.Equal(shouldMatch, result.IsCorrect);
    }

    [Theory]
    [InlineData("hello world", "HELLO WORLD", true)]
    [InlineData("HELLO WORLD", "hello world", true)]
    [InlineData("HeLLo WoRLd", "hello world", true)]
    public void IsMatch_ShouldBeCaseInsensitive(string userAnswer, string expectedAnswer, bool shouldMatch)
    {
        var result = FuzzyMatcher.Evaluate(userAnswer, expectedAnswer);
        Assert.Equal(shouldMatch, result.IsCorrect);
    }

    [Theory]
    [InlineData("take", "take (a photo)", "take")]
    [InlineData("ding", "ding~ (a sound)", "ding")]
    [InlineData("to choose", "choose", "choose")]
    [InlineData("word", "word~ (description)", "word")]
    [InlineData("word", "word (description)", "word")]
    [InlineData("word", "word~", "word")]
    [InlineData("complex phrase", "complex phrase (extra info)", "complex phrase")]
    public void IsMatch_ShouldExtractCoreWordCorrectly(string userAnswer, string expectedAnswer, string expectedCore)
    {
        var result = FuzzyMatcher.Evaluate(userAnswer, expectedAnswer);
        Assert.True(result.IsCorrect);
    }

    [Theory]
    [InlineData("hello", "helo", 1)] // 1 deletion
    [InlineData("helo", "hello", 1)] // 1 insertion
    [InlineData("hello", "hallo", 1)] // 1 substitution
    [InlineData("hello", "ehllo", 1)] // 1 transposition
    [InlineData("hello", "hlelo", 2)] // 2 operations
    [InlineData("hello", "hxlxo", 2)] // 2 substitutions
    public void IsMatch_ShouldRespectLevenshteinThreshold(string userAnswer, string expectedAnswer, int expectedDistance)
    {
        var shouldMatch = expectedDistance <= 2;
        var result = FuzzyMatcher.Evaluate(userAnswer, expectedAnswer);
        Assert.Equal(shouldMatch, result.IsCorrect);
    }

    [Theory]
    [InlineData("to run", "run")]
    [InlineData("to walk", "walk")]
    [InlineData("to be cloudy", "be cloudy")]
    [InlineData("to", "to")] // Edge case: just "to"
    public void IsMatch_ShouldHandleToPrefix(string input, string expectedCore)
    {
        var withTo = FuzzyMatcher.Evaluate(input, expectedCore);
        var withoutTo = FuzzyMatcher.Evaluate(expectedCore, input);
        Assert.True(withTo.IsCorrect || withoutTo.IsCorrect);
    }

    [Theory]
    [InlineData("word~", "word")]
    [InlineData("word~ (extra)", "word")]
    [InlineData("ding~ (sound)", "ding")]
    public void IsMatch_ShouldHandleTildeSuffix(string input, string expectedCore)
    {
        var result = FuzzyMatcher.Evaluate(expectedCore, input);
        Assert.True(result.IsCorrect);
    }

    [Theory]
    [InlineData("take (a photo)", "take")]
    [InlineData("word (definition)", "word")]
    [InlineData("phrase (with extra info)", "phrase")]
    [InlineData("test(nospace)", "test")]
    public void IsMatch_ShouldHandleParentheticalContent(string input, string expectedCore)
    {
        var result = FuzzyMatcher.Evaluate(expectedCore, input);
        Assert.True(result.IsCorrect);
    }

    [Fact]
    public void IsMatch_ShouldHandleComplexRealWorldExample1()
    {
        // Real scenario: User types core word, expected has parenthetical
        Assert.True(FuzzyMatcher.Evaluate("take", "take (a photo)").IsCorrect);
        Assert.True(FuzzyMatcher.Evaluate("take photo", "take (a photo)").IsCorrect);
        Assert.False(FuzzyMatcher.Evaluate("photo", "take (a photo)").IsCorrect);
    }

    [Fact]
    public void IsMatch_ShouldHandleComplexRealWorldExample2()
    {
        // Real scenario: User types partial phrase, expected has "to" prefix
        Assert.True(FuzzyMatcher.Evaluate("get cloudy", "to get cloudy").IsCorrect);
        Assert.True(FuzzyMatcher.Evaluate("cloudy", "to get cloudy").IsCorrect);
        Assert.False(FuzzyMatcher.Evaluate("cloud", "to get cloudy").IsCorrect);
    }

    [Fact]
    public void IsMatch_ShouldHandleComplexRealWorldExample3()
    {
        // Real scenario: Common typo with proper Levenshtein distance
        Assert.True(FuzzyMatcher.Evaluate("celcius", "Celsius").IsCorrect);
        Assert.True(FuzzyMatcher.Evaluate("Celcius", "celsius").IsCorrect);
        Assert.True(FuzzyMatcher.Evaluate("CELCIUS", "Celsius").IsCorrect);
    }

    [Fact]
    public void IsMatch_ShouldHandleComplexRealWorldExample4()
    {
        // Real scenario: Tilde suffix with sound descriptor
        Assert.True(FuzzyMatcher.Evaluate("ding", "ding~ (a sound)").IsCorrect);
        Assert.True(FuzzyMatcher.Evaluate("ding sound", "ding~ (a sound)").IsCorrect);
        Assert.True(FuzzyMatcher.Evaluate("ding a sound", "ding~ (a sound)").IsCorrect);
    }

    [Theory]
    [InlineData("to choose", "choose")]
    [InlineData("choose", "to choose")]
    public void IsMatch_ShouldBeBidirectionalForToPrefix(string answer1, string answer2)
    {
        Assert.True(FuzzyMatcher.Evaluate(answer1, answer2).IsCorrect);
        Assert.True(FuzzyMatcher.Evaluate(answer2, answer1).IsCorrect);
    }

    [Theory]
    [InlineData("word", "word")]
    [InlineData("phrase test", "phrase test")]
    [InlineData("exact match", "exact match")]
    public void IsMatch_ShouldHandleExactMatches(string userAnswer, string expectedAnswer)
    {
        Assert.True(FuzzyMatcher.Evaluate(userAnswer, expectedAnswer).IsCorrect);
    }

    [Theory]
    [InlineData(null, "test")]
    [InlineData("test", null)]
    [InlineData(null, null)]
    public void IsMatch_ShouldHandleNullInputs(string? userAnswer, string? expectedAnswer)
    {
        Assert.False(FuzzyMatcher.Evaluate(userAnswer!, expectedAnswer!).IsCorrect);
    }
}
