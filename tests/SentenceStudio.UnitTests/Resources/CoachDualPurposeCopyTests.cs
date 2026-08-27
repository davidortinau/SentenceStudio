using System.Globalization;
using FluentAssertions;
using System.Text.RegularExpressions;

namespace SentenceStudio.UnitTests.Resources;

/// <summary>
/// Stage B copy contract: the coach is dual purpose, and the entry and composer have to say so
/// or learners only ever discover the plan half.
/// </summary>
public class CoachDualPurposeCopyTests
{
    private static readonly CultureInfo Neutral = CultureInfo.InvariantCulture;
    private static readonly CultureInfo Korean = new("ko");

    private static string Get(string key, CultureInfo culture) =>
        LocalizationManager.Instance.GetString(key, culture);

    [Fact]
    public void TheComposerPlaceholderStaysShortEnoughForOneRow()
    {
        // The composer is a one-row textarea: anything long is truncated by the input width, so
        // the fuller wording lives on the entry card instead.
        foreach (var culture in new[] { Neutral, Korean })
        {
            var value = Get("Coach_ComposerPlaceholder", culture);

            value.Length.Should().BeLessThanOrEqualTo(48,
                $"the placeholder is truncated in a one-row textarea in '{culture.Name}'");
            value.Should().NotEndWith(".", "a placeholder is a prompt, not a sentence");
        }
    }

    [Fact]
    public void TheComposerPlaceholderStillOffersBothEverydayActions()
    {
        var english = Get("Coach_ComposerPlaceholder", Neutral);

        english.Should().Contain("Ask", "asking is one of the two things to do here");
        english.Should().Contain("Today's Plan", "and changing the plan is the other");
    }

    [Theory]
    [InlineData("Coach_ComposerPlaceholder")]
    public void TheDualPurposeCopyIsTranslated(string key)
    {
        var english = Get(key, Neutral);
        var korean = Get(key, Korean);

        korean.Should().NotBe(key, "the key must resolve");
        korean.Should().NotBe(english, "and must not fall back to English");
    }

    [Theory]
    [InlineData("Coach_NoPlanExplanation")]
    [InlineData("Coach_NoPlanTitle")]
    [InlineData("Coach_ConversationLabel")]
    [InlineData("Coach_AnswerLabel")]
    public void StageBKeysResolveInBothCultures(string key)
    {
        foreach (var culture in new[] { Neutral, Korean })
        {
            Get(key, culture).Should().NotBe(key, $"'{key}' must resolve in '{culture.Name}'");
            Get(key, culture).Should().NotBeNullOrWhiteSpace();
        }
    }

    [Fact]
    public void EveryAnswerBlockLabelResolvesInBothCultures()
    {
        // One label per block kind the server can emit. A missing one renders as a raw key.
        string[] kinds = ["Form", "Meaning", "Use", "Example", "Contrast", "Correction", "Note", "RetrievalPrompt"];

        foreach (var kind in kinds)
        {
            var key = $"Coach_AnswerBlock_{kind}";

            foreach (var culture in new[] { Neutral, Korean })
            {
                Get(key, culture).Should().NotBe(key, $"'{key}' must resolve in '{culture.Name}'");
            }

            Get(key, Korean).Should().NotBe(Get(key, Neutral), $"'{key}' must be translated");
        }
    }

    [Fact]
    public void TheNoPlanExplanationIsNeutralRatherThanAnApology()
    {
        var english = Get("Coach_NoPlanExplanation", Neutral);

        english.Should().NotContainAny("sorry", "error", "failed", "cannot", "unable");
        english.Should().Contain("still", "it points at what does work");
    }

    [Fact]
    public void TheNoPlanExplanationReadsAsACompleteSentenceOnItsOwn()
    {
        // It is used twice: standalone in the conversation, and as the body of the plan-canvas
        // card. A fragment would read as broken in the standalone position.
        foreach (var culture in new[] { Neutral, Korean })
        {
            var value = Get("Coach_NoPlanExplanation", culture);

            value.Should().EndWith(".", $"it stands alone in '{culture.Name}'");
            value.Should().NotContain("{0}", "it takes no arguments in either position");
        }
    }

    [Fact]
    public void StageBCopyCarriesNoEmoji()
    {
        string[] keys =
        [
            "Coach_ComposerPlaceholder", "Coach_NoPlanTitle",
            "Coach_NoPlanExplanation", "Coach_ConversationLabel", "Coach_AnswerLabel",
            "Coach_AnswerBlock_Form", "Coach_AnswerBlock_Meaning", "Coach_AnswerBlock_Use",
            "Coach_AnswerBlock_Example", "Coach_AnswerBlock_Contrast", "Coach_AnswerBlock_Correction",
            "Coach_AnswerBlock_Note", "Coach_AnswerBlock_RetrievalPrompt"
        ];

        foreach (var key in keys)
        {
            foreach (var culture in new[] { Neutral, Korean })
            {
                Regex.IsMatch(Get(key, culture), @"[\uD800-\uDBFF][\uDC00-\uDFFF]|[\u2600-\u27BF\uFE0F]")
                    .Should().BeFalse($"'{key}' in '{culture.Name}' must not use emoji");
            }
        }
    }

    [Fact]
    public void StageBCopyCarriesNoUnfilledPlaceholders()
    {
        // These strings are rendered directly, with no format arguments.
        string[] keys =
        [
            "Coach_ComposerPlaceholder", "Coach_NoPlanTitle",
            "Coach_NoPlanExplanation", "Coach_ConversationLabel", "Coach_AnswerLabel"
        ];

        foreach (var key in keys)
        {
            foreach (var culture in new[] { Neutral, Korean })
            {
                Get(key, culture).Should().NotContain("{0}", $"'{key}' takes no arguments");
            }
        }
    }
}
