using SentenceStudio.Api.Coach.Tools;

namespace SentenceStudio.Api.Tests.Coach.Tools;

/// <summary>
/// <c>list_user_vocabularies</c> filters: tag, resource, status, and never-practised words
/// (docs/coach/sam-capability-gap.md, gap 3).
/// </summary>
public class VocabularySearchFilterTests
{
    private static async Task<(CoachToolTestFixture Fixture, Dictionary<string, string> Ids)> SeedAsync()
    {
        var fixture = new CoachToolTestFixture();
        var me = CoachToolTestFixture.UserA;
        fixture.SeedProfile(me);
        var market = fixture.SeedResource(me, title: "Market");
        var ids = new Dictionary<string, string>();

        // Never practised, on Market, tagged food.
        var apple = fixture.SeedWord("사과", "apple", tags: "food, fruit", owner: me);
        fixture.Link(market, apple);
        ids["apple"] = apple.Id;

        // Practised and learning, tagged seafood (must not match "food").
        var fish = fixture.SeedWord("생선", "fish", tags: "seafood", owner: me);
        fixture.SeedProgress(me, fish.Id, masteryScore: 0.4f, totalAttempts: 5, nextReviewDate: fixture.Now.AddDays(3));
        ids["fish"] = fish.Id;

        // Due now, tagged food: listed like any other word (issue 258).
        var bread = fixture.SeedWord("빵", "bread", tags: "Food", owner: me);
        fixture.Link(market, bread);
        fixture.SeedProgress(me, bread.Id, totalAttempts: 4, nextReviewDate: fixture.Now.AddDays(-1));
        ids["bread"] = bread.Id;

        // Known.
        var water = fixture.SeedWord("물", "water", tags: "drink", owner: me);
        fixture.SeedProgress(me, water.Id, masteryScore: 0.95f, totalAttempts: 20, correctAttempts: 19,
            productionInStreak: 5, nextReviewDate: fixture.Now.AddDays(20));
        ids["water"] = water.Id;

        // Another learner's food word on another learner's resource.
        var theirs = fixture.SeedWord("우유", "milk", tags: "food", owner: CoachToolTestFixture.UserB);
        var theirResource = fixture.SeedResource(CoachToolTestFixture.UserB, title: "Theirs");
        fixture.Link(theirResource, theirs);
        ids["theirs"] = theirs.Id;
        ids["market"] = market.Id;
        ids["theirResource"] = theirResource.Id;

        await Task.CompletedTask;
        return (fixture, ids);
    }

    [Fact]
    public async Task Never_practised_words_are_found_when_asked_for()
    {
        var (fixture, ids) = await SeedAsync();
        using var _ = fixture;

        var old = await fixture.VocabularySearchTool.SearchAsync("사과");
        old.Words.Should().BeEmpty("the original search sees only practised words");

        var result = await fixture.VocabularySearchTool.SearchAsync(
            "사과", 10, tag: null, resourceId: null, status: null, includeNeverPracticed: true);

        result.Words.Should().ContainSingle().Which.WordId.Should().Be(ids["apple"]);
        result.Words[0].MasteryScore.Should().BeNull("a word never practised has no mastery fact");
        result.Scope.Filters.Should().NotHaveFlag(CoachScopeFilters.ProgressRowExists);
        result.Scope.MinimumEvidence.Should().Be(CoachScopeMinimumEvidence.None);
        CoachScopeInvariants.Violations(result.Scope).Should().BeEmpty();
    }

    [Fact]
    public async Task A_tag_is_matched_whole_and_without_case_and_a_due_word_is_listed()
    {
        // Issue 258: this pinned the due bread as matched, counted and withheld. It is now listed.
        var (fixture, ids) = await SeedAsync();
        using var _ = fixture;

        var result = await fixture.VocabularySearchTool.SearchAsync(
            null, 10, tag: "FOOD", resourceId: null, status: null, includeNeverPracticed: true);

        result.Words.Select(w => w.WordId).Should().BeEquivalentTo([ids["apple"], ids["bread"]]);
        result.Scope.MatchedCount.Should().Be(2, "the due bread matched and is counted");
        result.Scope.WithheldCount.Should().Be(0);
        result.Scope.WithheldReason.Should().Be(CoachScopeWithheldReason.None);
        result.Scope.Filters.Should().HaveFlag(CoachScopeFilters.AttributeFilter);
        result.Scope.Filters.Should().NotHaveFlag(CoachScopeFilters.ExcludeDue);
        result.Words.Should().NotContain(w => w.TargetTerm == "생선", "seafood is not the food tag");
        result.Words.Should().NotContain(w => w.TargetTerm == "우유", "another learner's word is never listed");
        CoachScopeInvariants.Violations(result.Scope).Should().BeEmpty();
    }

    [Fact]
    public async Task A_resource_filter_sees_only_the_learners_own_resource()
    {
        var (fixture, ids) = await SeedAsync();
        using var _ = fixture;

        var mine = await fixture.VocabularySearchTool.SearchAsync(
            null, 10, tag: null, resourceId: ids["market"], status: null, includeNeverPracticed: true);
        // Issue 258: this pinned bread, on Market but due, as withheld. It is now listed.
        mine.Words.Select(w => w.WordId).Should().BeEquivalentTo([ids["apple"], ids["bread"]]);
        mine.Scope.WithheldCount.Should().Be(0);

        var theirs = await fixture.VocabularySearchTool.SearchAsync(
            null, 10, tag: null, resourceId: ids["theirResource"], status: null, includeNeverPracticed: true);
        theirs.Words.Should().BeEmpty();
        theirs.Scope.MatchedCount.Should().Be(0);
    }

    [Theory]
    [InlineData("new", "apple")]
    [InlineData("learning", "fish")]
    [InlineData("KNOWN", "water")]
    public async Task A_status_filter_uses_the_dashboard_statuses(string status, string expected)
    {
        var (fixture, ids) = await SeedAsync();
        using var _ = fixture;

        var result = await fixture.VocabularySearchTool.SearchAsync(
            null, 10, tag: null, resourceId: null, status: status, includeNeverPracticed: true);

        result.Words.Select(w => w.WordId).Should().Equal(ids[expected]);
    }

    [Fact]
    public async Task Review_status_returns_the_words_due_for_review()
    {
        // Issue 258: this pinned a count and no words. The words due for review are now listed.
        var (fixture, ids) = await SeedAsync();
        using var __ = fixture;

        var result = await fixture.VocabularySearchTool.SearchAsync(
            null, 10, tag: null, resourceId: null, status: "review", includeNeverPracticed: true);

        result.Words.Select(w => w.WordId).Should().Equal(ids["bread"]);
        result.Scope.MatchedCount.Should().Be(1);
        result.Scope.WithheldCount.Should().Be(0);
    }

    [Theory]
    [InlineData("mastered")]
    [InlineData("learning, familiar")]
    [InlineData("4")]
    public async Task An_unknown_status_is_refused(string status)
    {
        // Enum.TryParse would take "4" as Known and OR "learning, familiar" into Review.
        var (fixture, _) = await SeedAsync();
        using var __ = fixture;

        var act = () => fixture.VocabularySearchTool.SearchAsync(
            null, 10, tag: null, resourceId: null, status: status, includeNeverPracticed: true);

        await act.Should().ThrowAsync<CoachToolException>();
    }

    [Theory]
    [InlineData("%")]
    [InlineData("f_od")]
    public async Task A_tag_with_wildcard_characters_is_matched_literally(string tag)
    {
        var (fixture, _) = await SeedAsync();
        using var __ = fixture;

        var result = await fixture.VocabularySearchTool.SearchAsync(
            null, 10, tag: tag, resourceId: null, status: null, includeNeverPracticed: true);

        result.Words.Should().BeEmpty();
        result.Scope.MatchedCount.Should().Be(0);
    }

    [Fact]
    public async Task A_word_marked_known_is_known_and_no_word_is_familiar_any_more()
    {
        var (fixture, _) = await SeedAsync();
        using var __ = fixture;
        var me = CoachToolTestFixture.UserA;
        // Marked Known with a Learning score (docs/known-words-plan.md): the mark decides.
        var word = fixture.SeedWord("밥", "rice", owner: me);
        var progress = fixture.SeedProgress(me, word.Id, masteryScore: 0.3f, totalAttempts: 4,
            correctAttempts: 2, productionInStreak: 0, nextReviewDate: fixture.Now.AddDays(20));
        progress.IsUserDeclared = true;
        progress.VerificationState = SentenceStudio.Shared.Models.VerificationStatus.Pending;
        fixture.Db.SaveChanges();

        var familiar = await fixture.VocabularySearchTool.SearchAsync(
            null, 10, tag: null, resourceId: null, status: "familiar", includeNeverPracticed: true);
        var known = await fixture.VocabularySearchTool.SearchAsync(
            null, 10, tag: null, resourceId: null, status: "known", includeNeverPracticed: true);
        var learning = await fixture.VocabularySearchTool.SearchAsync(
            null, 10, tag: null, resourceId: null, status: "learning", includeNeverPracticed: true);

        familiar.Words.Should().BeEmpty("Familiar is no longer a status");
        known.Words.Should().Contain(w => w.WordId == word.Id);
        learning.Words.Should().NotContain(w => w.WordId == word.Id);
    }

    [Fact]
    public async Task Without_filters_the_original_search_lists_every_practised_word_due_or_not()
    {
        // Issue 258: this pinned the due bread as left out. It is a practised word and is listed.
        var (fixture, ids) = await SeedAsync();
        using var _ = fixture;

        var result = await fixture.VocabularySearchTool.SearchAsync();

        result.Words.Select(w => w.WordId).Should().BeEquivalentTo([ids["water"], ids["fish"], ids["bread"]]);
        result.Scope.Filters.Should().HaveFlag(CoachScopeFilters.ProgressRowExists);
        result.Scope.Filters.Should().NotHaveFlag(CoachScopeFilters.AttributeFilter);
    }
}
