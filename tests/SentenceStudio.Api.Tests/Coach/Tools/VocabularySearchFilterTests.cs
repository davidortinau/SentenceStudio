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

        // Due now, tagged food: always withheld.
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
    public async Task A_tag_is_matched_whole_and_without_case_and_due_words_stay_withheld()
    {
        var (fixture, ids) = await SeedAsync();
        using var _ = fixture;

        var result = await fixture.VocabularySearchTool.SearchAsync(
            null, 10, tag: "FOOD", resourceId: null, status: null, includeNeverPracticed: true);

        result.Words.Select(w => w.WordId).Should().Equal(ids["apple"]);
        result.Scope.MatchedCount.Should().Be(2, "the due bread matched and is counted");
        result.Scope.WithheldCount.Should().Be(1);
        result.Scope.WithheldReason.Should().Be(CoachScopeWithheldReason.DueReviewEmbargo);
        result.Scope.Filters.Should().HaveFlag(CoachScopeFilters.AttributeFilter);
        result.Words.Should().NotContain(w => w.TargetTerm == "빵");
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
        mine.Words.Select(w => w.WordId).Should().Equal(ids["apple"]);
        mine.Scope.WithheldCount.Should().Be(1, "bread is on Market but due");

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
    public async Task Review_status_returns_a_count_and_no_words()
    {
        var (fixture, _) = await SeedAsync();
        using var __ = fixture;

        var result = await fixture.VocabularySearchTool.SearchAsync(
            null, 10, tag: null, resourceId: null, status: "review", includeNeverPracticed: true);

        result.Words.Should().BeEmpty();
        result.Scope.MatchedCount.Should().Be(1);
        result.Scope.WithheldCount.Should().Be(1);
    }

    [Fact]
    public async Task An_unknown_status_is_refused()
    {
        var (fixture, _) = await SeedAsync();
        using var __ = fixture;

        var act = () => fixture.VocabularySearchTool.SearchAsync(
            null, 10, tag: null, resourceId: null, status: "mastered", includeNeverPracticed: true);

        await act.Should().ThrowAsync<CoachToolException>();
    }

    [Fact]
    public async Task Without_filters_the_original_search_is_unchanged()
    {
        var (fixture, ids) = await SeedAsync();
        using var _ = fixture;

        var result = await fixture.VocabularySearchTool.SearchAsync();

        result.Words.Select(w => w.WordId).Should().BeEquivalentTo([ids["water"], ids["fish"]]);
        result.Scope.Filters.Should().HaveFlag(CoachScopeFilters.ProgressRowExists);
        result.Scope.Filters.Should().NotHaveFlag(CoachScopeFilters.AttributeFilter);
    }
}
