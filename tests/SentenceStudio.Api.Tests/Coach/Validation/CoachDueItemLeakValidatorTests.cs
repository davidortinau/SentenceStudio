using FluentAssertions;
using SentenceStudio.Api.Coach.Validation;
using SentenceStudio.Services;

namespace SentenceStudio.Api.Tests.Coach.Validation;

/// <summary>
/// Proves the leak validator finds a due word, a lemma, a translation, and an
/// example, including Korean spacing and particle variants, and that it leaves
/// aggregate sentences alone.
/// </summary>
public class CoachDueItemLeakValidatorTests
{
    private static CoachDueItemLeakValidator CreateValidator() =>
        new([new KoreanLanguageSegmenter()]);

    private static readonly CoachEmbargoedItem[] DueItems =
    [
        new("사과", "apple", Lemma: "사과", Examples: ["사과를 먹었습니다."]),
        new("한국어", "Korean language"),
        new("책", "book")
    ];

    [Fact]
    public void Clean_coach_text_passes()
    {
        var result = CreateValidator().Validate(
            "You have 12 words due today. A ten minute session fits your time.", DueItems);

        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public void A_literal_word_is_a_leak()
    {
        var result = CreateValidator().Validate("Today you review 사과 first.", DueItems);

        result.IsValid.Should().BeFalse();
        result.Violations.Should().Contain(v => v.Code == "due_term");
    }

    [Fact]
    public void A_word_with_a_particle_is_a_leak()
    {
        var result = CreateValidator().Validate("오늘은 사과를 복습합니다.", DueItems);

        result.Violations.Should().Contain(v => v.Code == "due_term");
    }

    [Fact]
    public void A_word_with_extra_spacing_is_a_leak()
    {
        var result = CreateValidator().Validate("Your session covers 한국 어 grammar.", DueItems);

        result.Violations.Should().Contain(v => v.Code == "due_term");
    }

    [Fact]
    public void A_single_character_word_with_a_particle_is_a_leak()
    {
        var result = CreateValidator().Validate("먼저 책을 읽으세요.", DueItems);

        result.Violations.Should().Contain(v => v.Code == "due_term");
    }

    [Fact]
    public void A_translation_is_a_leak()
    {
        var result = CreateValidator().Validate("One of your due words means apple.", DueItems);

        result.Violations.Should().Contain(v => v.Code == "due_gloss");
    }

    [Fact]
    public void A_plural_translation_is_a_leak()
    {
        var result = CreateValidator().Validate("Two of your due words are books.", DueItems);

        result.Violations.Should().Contain(v => v.Code == "due_gloss");
    }

    [Fact]
    public void An_example_sentence_is_a_leak()
    {
        var result = CreateValidator().Validate("Try this sentence: 사과를 먹었습니다.", DueItems);

        result.Violations.Should().Contain(v => v.Code == "due_example" || v.Code == "due_term");
    }

    [Fact]
    public void A_lemma_is_a_leak()
    {
        var items = new[] { new CoachEmbargoedItem("먹었습니다", "ate", Lemma: "먹다") };

        var result = CreateValidator().Validate("The lesson uses 먹다 many times.", items);

        result.Violations.Should().Contain(v => v.Code == "due_lemma");
    }

    [Fact]
    public void An_aggregate_sentence_with_counts_is_not_a_leak()
    {
        var result = CreateValidator().Validate(
            "Your last 14 days were mostly input. 18 words are due and 4 never practiced.", DueItems);

        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public void A_category_tag_that_matches_a_translation_is_allowed_when_the_tools_returned_it()
    {
        var items = new[] { new CoachEmbargoedItem("음식", "food") };

        var withoutAllowList = CreateValidator().Validate("Most due words are in the food group.", items);
        var withAllowList = CreateValidator().Validate(
            "Most due words are in the food group.", items, allowedVocabulary: ["food"]);

        withoutAllowList.IsValid.Should().BeFalse();
        withAllowList.IsValid.Should().BeTrue();
    }

    [Fact]
    public void A_short_translation_inside_a_longer_word_is_not_a_leak()
    {
        var items = new[] { new CoachEmbargoedItem("예술", "art") };

        var result = CreateValidator().Validate("Your started activity is part of today's plan.", items);

        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public void The_violation_never_repeats_the_word_it_found()
    {
        var result = CreateValidator().Validate("Today you review 사과 first.", DueItems);

        result.Violations.Should().OnlyContain(v => v.MaskedEvidence == null || !v.MaskedEvidence.Contains("사과"));
        result.Violations.Should().Contain(v => v.MaskedEvidence!.Contains('*'));
    }

    [Fact]
    public void The_validator_checks_every_text_of_a_turn()
    {
        var texts = new[] { "Nothing to see here.", "Start with 사과." };

        var result = CreateValidator().ValidateMany(texts, DueItems);

        result.IsValid.Should().BeFalse();
    }

    [Fact]
    public void An_empty_due_list_or_empty_text_passes()
    {
        var validator = CreateValidator();

        validator.Validate("Start with 사과.", Array.Empty<CoachEmbargoedItem>()).IsValid.Should().BeTrue();
        validator.Validate(null, DueItems).IsValid.Should().BeTrue();
        validator.Validate("   ", DueItems).IsValid.Should().BeTrue();
    }

    [Fact]
    public void The_validator_works_without_a_segmenter()
    {
        var validator = new CoachDueItemLeakValidator();

        validator.Validate("오늘은 사과를 복습합니다.", DueItems).IsValid.Should().BeFalse();
    }
}
