using FluentAssertions;
using SentenceStudio.Shared.Models;
using SentenceStudio.Shared.Services;
using Xunit;

namespace SentenceStudio.UnitTests.Services;

/// <summary>
/// A term's parenthetical no longer stops a correct sentence from being recognised as using the
/// target word (#221). Every quiz client grades through <see cref="VocabQuizSentenceUsagePolicy"/>.
/// </summary>
public class VocabQuizSentenceUsagePolicyParentheticalTests
{
    [Theory]
    [InlineData("맛있다 (맛있어요)", new[] { "맛있다", "맛있어요" })]
    [InlineData("(을/를) 좋아하다", new[] { "좋아하다" })]
    [InlineData("배 (pear)", new[] { "배" })]
    [InlineData("가다（가요）", new[] { "가다", "가요" })]
    [InlineData("comer (to eat)", new[] { "comer", "to eat" })]
    [InlineData("느끼다", new[] { "느끼다" })]
    [InlineData("(something)", new[] { "something" })]
    public void Candidates_are_the_bare_term_then_same_script_forms_inside_the_parentheses(
        string term, string[] expected)
    {
        VocabQuizSentenceUsagePolicy.TargetTermCandidates(term).Should().Equal(expected);
    }

    [Fact]
    public void A_term_with_a_parenthetical_form_matches_its_dictionary_form()
    {
        var word = new VocabularyWord { TargetLanguageTerm = "맛있다 (맛있어요)" };
        var grade = Grade(("이 김치는 정말 맛있어요", "맛있다", true));

        VocabQuizSentenceUsagePolicy.GetTargetWordAnalysis(grade, word)!.UsageCorrect.Should().BeTrue();
    }

    [Fact]
    public void A_leading_particle_parenthetical_finds_the_verb_not_the_particle()
    {
        var word = new VocabularyWord { TargetLanguageTerm = "(을/를) 좋아하다" };
        var grade = Grade(("커피를", "를", false), ("좋아해요", "좋아하다", true));

        var analysis = VocabQuizSentenceUsagePolicy.GetTargetWordAnalysis(grade, word);

        analysis!.DictionaryForm.Should().Be("좋아하다");
    }

    [Fact]
    public void A_native_language_hint_in_parentheses_never_counts_as_the_target_word()
    {
        var word = new VocabularyWord { TargetLanguageTerm = "배 (pear)" };
        var grade = Grade(("pear", "pear", true));

        VocabQuizSentenceUsagePolicy.GetTargetWordAnalysis(grade, word).Should().BeNull();
    }

    [Fact]
    public void The_form_inside_the_parentheses_is_accepted_when_the_model_reports_it()
    {
        var word = new VocabularyWord { TargetLanguageTerm = "맛있다 (맛있어요)" };
        var grade = Grade(("맛있어요", "맛있어요", true));

        VocabQuizSentenceUsagePolicy.GetTargetWordAnalysis(grade, word).Should().NotBeNull();
    }

    [Fact]
    public void The_sentence_check_still_requires_the_used_form_in_a_Korean_sentence()
    {
        var word = new VocabularyWord { TargetLanguageTerm = "맛있다 (맛있어요)" };

        VocabQuizSentenceUsagePolicy.HasVerifiedTargetUse(
            "이 김치는 정말 맛있어요.", word, "Korean", "맛있어요").Should().BeTrue();
        VocabQuizSentenceUsagePolicy.HasVerifiedTargetUse(
            "This kimchi is delicious.", word, "Korean", "delicious").Should().BeFalse();
    }

    private static GradeResponse Grade(params (string Used, string Dictionary, bool Correct)[] items) => new()
    {
        VocabularyAnalysis = items.Select(item => new VocabularyAnalysis
        {
            UsedForm = item.Used,
            DictionaryForm = item.Dictionary,
            UsageCorrect = item.Correct,
        }).ToList()
    };
}
