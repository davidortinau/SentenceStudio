using System.Collections;
using System.Globalization;
using System.Reflection;
using System.Resources;
using FluentAssertions;

namespace SentenceStudio.UnitTests.Resources;

/// <summary>
/// The Korean answer word for "No" is 아니요 wherever the app shows it.
/// </summary>
/// <remarks>
/// 아니오 is the sentence ending of the 하오 style (그것이 아니오), not the answer to a question.
/// Three keys shipped it until issue 339 while the starter vocabulary taught 아니요, so a learner
/// of Korean saw two spellings of the same word. These read the compiled resources through the
/// ResourceManager, so they check what a screen is given.
/// </remarks>
public class KoreanAnswerWordTests
{
    private const string StandardNo = "아니요";
    private const string HaoStyleEnding = "아니오";

    private static readonly CultureInfo Korean = new("ko");

    /// <summary>
    /// AppResources is internal to SentenceStudio.Shared, so the ResourceManager is reached by
    /// reflection, as CoachResourceStringTests does.
    /// </summary>
    private static ResourceManager ResourceManager
    {
        get
        {
            var type = typeof(LocalizationManager).Assembly
                .GetType("SentenceStudio.Resources.Strings.AppResources", throwOnError: true)!;

            var property = type.GetProperty("ResourceManager",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!;

            return (ResourceManager)property.GetValue(null)!;
        }
    }

    [Theory]
    [InlineData("No")]
    [InlineData("VocabQuiz_No")]
    [InlineData("DebugHealth_No")]
    public void Korean_No_is_the_standard_answer_word(string key)
    {
        ResourceManager.GetString(key, Korean).Should().Be(StandardNo,
            $"'{key}' answers a yes-or-no question, and the starter vocabulary teaches {StandardNo}");
    }

    [Theory]
    [InlineData("Yes")]
    [InlineData("VocabQuiz_Yes")]
    [InlineData("DebugHealth_Yes")]
    public void Korean_Yes_stays_the_word_that_pairs_with_it(string key)
    {
        ResourceManager.GetString(key, Korean).Should().Be("예",
            $"'{key}' is shown beside its No, and 예 with {StandardNo} is the standard pair");
    }

    [Fact]
    public void No_Korean_string_uses_the_hao_style_ending_as_an_answer()
    {
        var set = ResourceManager.GetResourceSet(Korean, createIfNotExists: true, tryParents: false);
        set.Should().NotBeNull("the Korean resource set must ship");

        var offenders = set!.Cast<DictionaryEntry>()
            .Where(entry => entry.Value is string value
                && value.Contains(HaoStyleEnding, StringComparison.Ordinal))
            .Select(entry => (string)entry.Key)
            .OrderBy(key => key, StringComparer.Ordinal)
            .ToList();

        offenders.Should().BeEmpty(
            $"no Korean string in the app is written in the 하오 style, so {HaoStyleEnding} "
            + $"in one is a misspelling of {StandardNo}");
    }
}
