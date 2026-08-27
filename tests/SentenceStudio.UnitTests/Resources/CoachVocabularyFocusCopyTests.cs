using System.Globalization;
using FluentAssertions;
using SentenceStudio.Services;

namespace SentenceStudio.UnitTests.Resources;

/// <summary>
/// Copy rules for the vocabulary focus.
/// </summary>
/// <remarks>
/// The focus contract deliberately carries no due dates, mastery scores or progress, because a
/// focus is a choice about today, not a debt the learner has accrued. The copy has to hold the
/// same line: the moment it says "due" or "mastery" it re-frames a suggestion as a schedule the
/// learner is behind on, and it implies data the client was never given.
/// </remarks>
public class CoachVocabularyFocusCopyTests
{
    private static readonly CultureInfo Neutral = CultureInfo.InvariantCulture;
    private static readonly CultureInfo Korean = new("ko");

    private static readonly string[] FocusKeys =
    [
        "Coach_FocusHeading",
        "Coach_FocusGenericLabel",
        "Coach_FocusCount",
        "Coach_FocusCountOfEligible",
        "Coach_FocusApplied",
        "Coach_FocusRestored",
        "Coach_FocusCurrent",
        "Coach_FocusRemoved",
        "Coach_FocusSuggestionSummary",
        "Coach_FocusProposed"
    ];

    private static string Get(string key, CultureInfo culture) =>
        LocalizationManager.Instance.GetString(key, culture);

    [Fact]
    public void EveryFocusKeyExistsInBothCultures()
    {
        foreach (var key in FocusKeys)
        {
            Get(key, Neutral).Should().NotBeNullOrWhiteSpace($"{key} needs English copy");
            Get(key, Korean).Should().NotBeNullOrWhiteSpace($"{key} needs Korean copy");
        }
    }

    [Fact]
    public void EveryFocusKeyIsGenuinelyTranslated()
    {
        foreach (var key in FocusKeys)
        {
            Get(key, Korean).Should().NotBe(Get(key, Neutral),
                $"{key} must be translated, not fall back to English");
        }
    }

    [Fact]
    public void NoFocusCopyUsesReviewOrMasteryLanguage()
    {
        // The client is never told which words are due or weak. Copy that implies otherwise
        // would be describing data the UI does not have.
        string[] banned = ["due", "mastery", "overdue", "weak", "score", "복습", "숙련", "점수"];

        foreach (var key in FocusKeys)
        {
            foreach (var culture in new[] { Neutral, Korean })
            {
                var value = Get(key, culture);

                foreach (var term in banned)
                {
                    value.Should().NotContainEquivalentOf(term,
                        $"{key} in '{culture.Name}' must describe a focus, not a review schedule");
                }
            }
        }
    }

    [Fact]
    public void TheCountKeysCarryTheExpectedPlaceholders()
    {
        Get("Coach_FocusCount", Neutral).Should().Contain("{0}");
        Get("Coach_FocusCount", Korean).Should().Contain("{0}");

        foreach (var culture in new[] { Neutral, Korean })
        {
            var value = Get("Coach_FocusCountOfEligible", culture);
            value.Should().Contain("{0}", "the selected count");
            value.Should().Contain("{1}", "the eligible total");
        }
    }

    [Fact]
    public void TheHeadingCarriesTheFocusLabelPlaceholder()
    {
        foreach (var culture in new[] { Neutral, Korean })
        {
            Get("Coach_FocusHeading", culture).Should().Contain("{0}",
                "the server's localized label is interpolated, never concatenated");
        }
    }

    [Fact]
    public void TheSuggestionSummaryCarriesTheFocusLabelPlaceholder()
    {
        // This sentence replaces the server's English rationale, so it has to carry the label
        // the rationale would have named. Without the placeholder it says nothing concrete.
        foreach (var culture in new[] { Neutral, Korean })
        {
            Get("Coach_FocusSuggestionSummary", culture).Should().Contain("{0}",
                "suppressing the English fallback must not lose the focus label with it");
        }
    }

    [Fact]
    public void TheSuggestionSummaryNamesSam()
    {
        Get("Coach_FocusSuggestionSummary", Neutral).Should().Contain("Sam");
        Get("Coach_FocusSuggestionSummary", Korean).Should().Contain("쌤");
    }

    [Fact]
    public void NoFocusCopyUsesAnEmoji()
    {
        foreach (var key in FocusKeys)
        {
            foreach (var culture in new[] { Neutral, Korean })
            {
                Get(key, culture).EnumerateRunes()
                    .Where(r => r.Value is >= 0x1F300 and <= 0x1FAFF or >= 0x2600 and <= 0x27BF)
                    .Should().BeEmpty($"{key} in '{culture.Name}' must read as plain text");
            }
        }
    }
}
