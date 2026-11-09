using SentenceStudio.Api.Coach.Tools;
using SentenceStudio.Api.Coach.Tools.SamTools;

namespace SentenceStudio.Api.Tests.Coach.Tools;

/// <summary>
/// <c>get_vocabulary_example_sentences</c>: a word's example sentences, owner-scoped, with a due
/// word's sentences withheld (docs/coach/sam-capability-gap.md, gap 2).
/// </summary>
public class VocabularyExampleSentencesToolTests
{
    [Fact]
    public async Task Returns_the_learners_sentences_main_first_even_for_a_word_never_practised()
    {
        using var fixture = new CoachToolTestFixture();
        var word = fixture.SeedWord("사과", "apple", owner: CoachToolTestFixture.UserA);
        fixture.SeedExampleSentence(word, "사과를 샀어요.", "I bought an apple.", createdDaysAgo: 9);
        fixture.SeedExampleSentence(word, "사과가 맛있어요.", "The apple is tasty.", isCore: true, createdDaysAgo: 1);

        var result = await fixture.VocabularyExampleSentencesTool.GetAsync(word.Id);

        result.IsDueForReview.Should().BeFalse();
        result.TargetTerm.Should().Be("사과");
        result.TotalCount.Should().Be(2);
        result.Sentences.Select(s => s.TargetSentence).Should().Equal("사과가 맛있어요.", "사과를 샀어요.");
        result.Sentences[0].IsCore.Should().BeTrue();
        result.Sentences[0].NativeSentence.Should().Be("The apple is tasty.");
        result.Scope.WithheldCount.Should().Be(0);
        CoachScopeInvariants.Violations(result.Scope).Should().BeEmpty();
    }

    [Fact]
    public async Task A_due_words_sentences_are_counted_and_withheld()
    {
        using var fixture = new CoachToolTestFixture();
        var user = CoachToolTestFixture.UserA;
        var word = fixture.SeedWord("만기", "due", owner: user);
        fixture.SeedProgress(user, word.Id, nextReviewDate: fixture.Now.AddHours(-1));
        fixture.SeedExampleSentence(word, "SENTINEL 만기 문장이에요.");

        var result = await fixture.VocabularyExampleSentencesTool.GetAsync(word.Id);

        result.IsDueForReview.Should().BeTrue();
        result.TargetTerm.Should().BeNull();
        result.TotalCount.Should().Be(1);
        result.Sentences.Should().BeEmpty();
        result.Scope.WithheldCount.Should().Be(1);
        result.Scope.WithheldReason.Should().Be(CoachScopeWithheldReason.DueReviewEmbargo);
        result.Scope.Filters.Should().HaveFlag(CoachScopeFilters.ExcludeDue);
        CoachScopeInvariants.Violations(result.Scope).Should().BeEmpty();

        // Not selected at all, so no query text carries the sentence back.
        fixture.Commands.CommandTexts.Should().NotContain(t => t.Contains("\"TargetSentence\"") && t.Contains("LIMIT"));
    }

    [Fact]
    public async Task Another_learners_word_or_sentence_is_never_read()
    {
        using var fixture = new CoachToolTestFixture();
        var theirs = fixture.SeedWord("바나나", "banana", owner: CoachToolTestFixture.UserB);
        fixture.SeedExampleSentence(theirs, "바나나가 좋아요.");

        var act = () => fixture.VocabularyExampleSentencesTool.GetAsync(theirs.Id);
        await act.Should().ThrowAsync<CoachToolException>();

        // A sentence another learner put on my word is not mine either.
        var mine = fixture.SeedWord("사과", "apple", owner: CoachToolTestFixture.UserA);
        fixture.SeedExampleSentence(mine, "남의 문장", owner: CoachToolTestFixture.UserB);
        fixture.SeedExampleSentence(mine, "내 문장");

        var result = await fixture.VocabularyExampleSentencesTool.GetAsync(mine.Id);
        result.Sentences.Select(s => s.TargetSentence).Should().Equal("내 문장");
        result.TotalCount.Should().Be(1);
    }

    [Fact]
    public async Task A_long_list_is_a_page_that_says_so()
    {
        using var fixture = new CoachToolTestFixture();
        var word = fixture.SeedWord("사과", "apple", owner: CoachToolTestFixture.UserA);
        for (var i = 0; i < 3; i++)
        {
            fixture.SeedExampleSentence(word, $"문장 {i}", createdDaysAgo: 10 - i);
        }

        var result = await fixture.VocabularyExampleSentencesTool.GetAsync(word.Id, maxResults: 2);

        result.Sentences.Should().HaveCount(2);
        result.TotalCount.Should().Be(3);
        result.Scope.Truncated.Should().BeTrue();
        result.Scope.Coverage.Should().Be(CoachScopeCoverage.PageOfOwnedSet);
        CoachScopeInvariants.Violations(result.Scope).Should().BeEmpty();
    }

    [Fact]
    public async Task No_learner_means_no_read()
    {
        using var fixture = new CoachToolTestFixture();
        var word = fixture.SeedWord("사과", "apple", owner: CoachToolTestFixture.UserA);
        fixture.Scope.CurrentUserProfileId = null;

        var act = () => fixture.VocabularyExampleSentencesTool.GetAsync(word.Id);
        await act.Should().ThrowAsync<CoachToolException>();
    }

    [Fact]
    public void The_result_passes_the_tool_result_embargo_scan()
    {
        new SentenceStudio.Api.Coach.Validation.CoachEmbargoScanner()
            .ScanType(typeof(VocabularyExampleSentencesResult), SentenceStudio.Api.Coach.Validation.CoachEmbargoScope.ToolResult)
            .IsValid.Should().BeTrue();
        VocabularyExampleSentencesTool.MaxResults.Should().Be(20);
    }
}
