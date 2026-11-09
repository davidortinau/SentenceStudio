namespace SentenceStudio.Api.Tests.Coach.Tools;

/// <summary>
/// Sam's word count is the dashboard's word count (docs/coach/sam-capability-gap.md, gap 1).
/// </summary>
/// <remarks>
/// Before this, <c>get_current_profile_summary</c> counted only words with a progress row, so a
/// learner who had just added words, or had words they never practised, heard a smaller number
/// than the dashboard's "Total words".
/// </remarks>
public class CoachWordCountParityTests
{
    [Fact]
    public async Task The_total_matches_the_dashboard_and_the_other_counts_are_labelled()
    {
        using var fixture = new CoachToolTestFixture();
        var me = CoachToolTestFixture.UserA;
        fixture.SeedProfile(me);
        var resource = fixture.SeedResource(me);

        // On a resource, never practised: the dashboard counts them as New; the old count did not.
        var fresh1 = fixture.SeedWord("사과", "apple", owner: me);
        var fresh2 = fixture.SeedWord("바나나", "banana", owner: me);
        fixture.Link(resource, fresh1);
        fixture.Link(resource, fresh2);

        // On a resource and practised, due now: counted everywhere.
        var practised = fixture.SeedWord("하늘", "sky", owner: me);
        fixture.Link(resource, practised);
        fixture.SeedProgress(me, practised.Id, totalAttempts: 4, nextReviewDate: fixture.Now.AddDays(-1));

        // Practised but on no resource: the Vocabulary screen counts it, the dashboard does not.
        var loose = fixture.SeedWord("바다", "sea", owner: me);
        fixture.SeedProgress(me, loose.Id, totalAttempts: 3, nextReviewDate: fixture.Now.AddDays(5));

        var summary = await fixture.CurrentProfileSummaryTool.GetAsync();
        var (n, l, f, r, k) = await fixture.VocabularyRepository.GetVocabSummaryCountsAsync(me);

        summary.TotalWordCount.Should().Be(n + l + f + r + k, "Sam must say the number the dashboard shows");
        summary.TotalWordCount.Should().Be(3);
        summary.NewWordCount.Should().Be(n).And.Be(2);
        summary.LearningWordCount.Should().Be(l);
        summary.FamiliarWordCount.Should().Be(f);
        summary.ReviewWordCount.Should().Be(r).And.Be(1);
        summary.KnownWordCount.Should().Be(k);
        summary.VocabularyScreenWordCount.Should().Be(4, "the Vocabulary screen lists every owned word");
        summary.TrackedWordCount.Should().Be(2, "the progress-row count is kept, and labelled as such");
    }

    [Fact]
    public async Task Another_learners_words_are_never_counted()
    {
        using var fixture = new CoachToolTestFixture();
        var me = CoachToolTestFixture.UserA;
        var them = CoachToolTestFixture.UserB;
        fixture.SeedProfile(me);
        fixture.SeedProfile(them, name: "Other", email: "other@example.com");

        var mine = fixture.SeedResource(me);
        var theirs = fixture.SeedResource(them);
        var myWord = fixture.SeedWord("사과", "apple", owner: me);
        fixture.Link(mine, myWord);

        var theirWord1 = fixture.SeedWord("자동차", "car", owner: them);
        var theirWord2 = fixture.SeedWord("기차", "train", owner: them);
        fixture.Link(theirs, theirWord1);
        fixture.Link(theirs, theirWord2);
        // Their word on my resource still is not mine (the one ownership rule).
        fixture.Link(mine, theirWord2);

        var summary = await fixture.CurrentProfileSummaryTool.GetAsync();
        summary.TotalWordCount.Should().Be(1);
        summary.VocabularyScreenWordCount.Should().Be(1);

        fixture.Scope.CurrentUserProfileId = them;
        var theirSummary = await fixture.CurrentProfileSummaryTool.GetAsync();
        theirSummary.TotalWordCount.Should().Be(2);
        theirSummary.VocabularyScreenWordCount.Should().Be(2);
    }

    [Fact]
    public async Task A_word_Sam_just_added_raises_the_total_at_once()
    {
        // The case in the gap report: a word added with no practice yet left Sam's old count
        // unchanged.
        using var fixture = new CoachToolTestFixture();
        var me = CoachToolTestFixture.UserA;
        fixture.SeedProfile(me);
        var resource = fixture.SeedResource(me);

        var before = (await fixture.CurrentProfileSummaryTool.GetAsync()).TotalWordCount;
        fixture.Link(resource, fixture.SeedWord("포도", "grape", owner: me));
        var after = await fixture.CurrentProfileSummaryTool.GetAsync();

        after.TotalWordCount.Should().Be(before + 1);
        after.TrackedWordCount.Should().Be(0);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task The_count_query_fails_closed_without_an_owner(string owner)
    {
        using var fixture = new CoachToolTestFixture();
        fixture.SeedWord("사과", "apple");

        (await fixture.VocabularyRepository.GetVocabularyCountsAsync(owner))
            .Should().Be(SentenceStudio.Application.Vocabulary.VocabularyCountFacts.Empty);
    }
}
