using FluentAssertions;
using SentenceStudio.Api.Coach.Tools;

namespace SentenceStudio.Api.Tests.Coach.Tools;

/// <summary>
/// Behavioural coverage for the nine Sam read tools.
/// </summary>
/// <remarks>
/// <para>
/// The core-five suites next to this file prove the original tools behave. They say nothing about
/// these nine, and a count of passing tests in the project was read at one point as if it did. So
/// this file exercises each new tool directly against a seeded database: the identity gate, the
/// cross-tenant gate, the output bounds, and — for the vocabulary list — the due-word embargo that
/// the first version of the tool did not have.
/// </para>
/// <para>
/// Every test drives a real tool instance over real SQLite. Asserting on the registry or on a
/// schema would only prove the tools were declared, which is the gap that let a leak ship.
/// </para>
/// </remarks>
public class SamReadToolBehaviorTests
{
    // ---------------------------------------------------------------------
    // Identity gate: no scope, no query.
    // ---------------------------------------------------------------------

    [Fact]
    public async Task Every_sam_tool_fails_closed_without_a_user_scope()
    {
        using var fixture = new CoachToolTestFixture();
        fixture.SeedProfile(CoachToolTestFixture.UserA);
        fixture.Scope.CurrentUserProfileId = null;

        foreach (var call in SamCalls(fixture))
        {
            var failure = (await call.Should().ThrowAsync<CoachToolException>()).Which;
            failure.Kind.Should().Be(CoachToolFailureKind.Unauthorized);
            failure.Code.Should().Be("unauthorized");
        }
    }

    [Fact]
    public async Task No_sam_tool_queries_the_database_before_the_scope_check_fails()
    {
        using var fixture = new CoachToolTestFixture();
        fixture.SeedProfile(CoachToolTestFixture.UserA);
        fixture.SeedResource(CoachToolTestFixture.UserA);
        fixture.SeedSkill(CoachToolTestFixture.UserA);
        fixture.Scope.CurrentUserProfileId = null;

        foreach (var call in SamCalls(fixture))
        {
            fixture.Commands.Reset();
            await call.Should().ThrowAsync<CoachToolException>();

            // The scope is resolved before any read. A single command here would mean the tool
            // touched a learner's data on behalf of a request that had no learner.
            fixture.Commands.CommandCount.Should().Be(
                0, "the scope check must run before the first query");
        }
    }

    // ---------------------------------------------------------------------
    // Cross-tenant gate: another learner's identifier is not a lookup key.
    // ---------------------------------------------------------------------

    [Fact]
    public async Task Word_detail_refuses_an_identifier_owned_by_another_learner()
    {
        using var fixture = new CoachToolTestFixture();
        fixture.SeedProfile(CoachToolTestFixture.UserA);
        fixture.SeedProfile(CoachToolTestFixture.UserB, email: "other@example.com");

        var word = fixture.SeedWord("비밀", "secret");
        fixture.SeedProgress(CoachToolTestFixture.UserB, word.Id);

        fixture.Scope.CurrentUserProfileId = CoachToolTestFixture.UserA;

        var act = () => fixture.VocabularyWordDetailTool.GetAsync(word.Id);

        var failure = (await act.Should().ThrowAsync<CoachToolException>()).Which;
        failure.Kind.Should().Be(CoachToolFailureKind.InvalidArgument);

        // The refusal must not distinguish "not yours" from "does not exist"; either answer would
        // let the model probe for the existence of another learner's rows one id at a time.
        failure.Reason.Should().NotContain(CoachToolTestFixture.UserB);
    }

    [Fact]
    public async Task Skill_detail_refuses_an_identifier_owned_by_another_learner()
    {
        using var fixture = new CoachToolTestFixture();
        fixture.SeedProfile(CoachToolTestFixture.UserA);
        fixture.SeedProfile(CoachToolTestFixture.UserB, email: "other@example.com");

        var skill = fixture.SeedSkill(CoachToolTestFixture.UserB, title: "Not yours");
        fixture.Scope.CurrentUserProfileId = CoachToolTestFixture.UserA;

        var act = () => fixture.SkillDetailTool.GetAsync(skill.Id);

        var failure = (await act.Should().ThrowAsync<CoachToolException>()).Which;
        failure.Kind.Should().Be(CoachToolFailureKind.InvalidArgument);
        failure.Reason.Should().NotContain("Not yours");
    }

    [Fact]
    public async Task Resource_detail_refuses_an_identifier_owned_by_another_learner()
    {
        using var fixture = new CoachToolTestFixture();
        fixture.SeedProfile(CoachToolTestFixture.UserA);
        fixture.SeedProfile(CoachToolTestFixture.UserB, email: "other@example.com");

        var resource = fixture.SeedResource(CoachToolTestFixture.UserB, title: "Their podcast");
        fixture.Scope.CurrentUserProfileId = CoachToolTestFixture.UserA;

        var act = () => fixture.LearningResourceDetailTool.GetAsync(resource.Id);

        var failure = (await act.Should().ThrowAsync<CoachToolException>()).Which;
        failure.Kind.Should().Be(CoachToolFailureKind.InvalidArgument);
        failure.Reason.Should().NotContain("Their podcast");
    }

    [Fact]
    public async Task List_tools_return_only_the_current_learners_rows()
    {
        using var fixture = new CoachToolTestFixture();
        fixture.SeedProfile(CoachToolTestFixture.UserA);
        fixture.SeedProfile(CoachToolTestFixture.UserB, email: "other@example.com");

        fixture.SeedSkill(CoachToolTestFixture.UserA, title: "Mine");
        fixture.SeedSkill(CoachToolTestFixture.UserB, title: "Theirs");
        fixture.SeedResource(CoachToolTestFixture.UserA, title: "My podcast");
        fixture.SeedResource(CoachToolTestFixture.UserB, title: "Their podcast");

        var mineWord = fixture.SeedWord("내단어", "my word");
        var theirWord = fixture.SeedWord("남단어", "their word");
        fixture.SeedProgress(CoachToolTestFixture.UserA, mineWord.Id);
        fixture.SeedProgress(CoachToolTestFixture.UserB, theirWord.Id);

        fixture.Scope.CurrentUserProfileId = CoachToolTestFixture.UserA;

        var skills = await fixture.SkillListTool.GetAsync();
        skills.Skills.Select(s => s.Title).Should().ContainSingle().Which.Should().Be("Mine");
        skills.TotalCount.Should().Be(1);

        var resources = await fixture.LearningResourceListTool.GetAsync();
        resources.Resources.Select(r => r.Title).Should().ContainSingle().Which.Should().Be("My podcast");

        var words = await fixture.VocabularySearchTool.SearchAsync();
        words.Words.Select(w => w.TargetTerm).Should().ContainSingle().Which.Should().Be("내단어");
    }

    // ---------------------------------------------------------------------
    // The due-word embargo. This is the defect the first version shipped with.
    // ---------------------------------------------------------------------

    [Fact]
    public async Task Vocabulary_list_excludes_words_that_are_due_for_review()
    {
        using var fixture = new CoachToolTestFixture();
        fixture.SeedProfile(CoachToolTestFixture.UserA);

        var due = fixture.SeedWord("복습", "review-me");
        var notDue = fixture.SeedWord("안전", "safe");
        var unscheduled = fixture.SeedWord("새단어", "brand-new");

        fixture.SeedProgress(CoachToolTestFixture.UserA, due.Id,
            masteryScore: 0.9f, nextReviewDate: fixture.Now.AddDays(-1));
        fixture.SeedProgress(CoachToolTestFixture.UserA, notDue.Id,
            masteryScore: 0.5f, nextReviewDate: fixture.Now.AddDays(3));
        fixture.SeedProgress(CoachToolTestFixture.UserA, unscheduled.Id,
            masteryScore: 0.1f, nextReviewDate: null);

        var result = await fixture.VocabularySearchTool.SearchAsync();

        var terms = result.Words.Select(w => w.TargetTerm).ToList();
        terms.Should().NotContain("복습", "a due word's term is the answer to a review the learner has not taken");
        terms.Should().Contain("안전");
        terms.Should().Contain("새단어");

        // The count must agree with the rows, or the model learns the due volume from the gap.
        result.TotalMatchCount.Should().Be(2);
        result.ReturnedCount.Should().Be(2);
    }

    [Fact]
    public async Task Vocabulary_list_excludes_a_due_word_even_when_the_query_names_it()
    {
        using var fixture = new CoachToolTestFixture();
        fixture.SeedProfile(CoachToolTestFixture.UserA);

        var due = fixture.SeedWord("복습", "review-me");
        fixture.SeedProgress(CoachToolTestFixture.UserA, due.Id, nextReviewDate: fixture.Now.AddDays(-5));

        var result = await fixture.VocabularySearchTool.SearchAsync("review");

        result.Words.Should().BeEmpty("a search term must not become a way around the due embargo");
        result.TotalMatchCount.Should().Be(0);
    }

    [Fact]
    public async Task Word_detail_still_answers_for_a_due_word()
    {
        using var fixture = new CoachToolTestFixture();
        fixture.SeedProfile(CoachToolTestFixture.UserA);

        var due = fixture.SeedWord("복습", "review-me");
        fixture.SeedProgress(CoachToolTestFixture.UserA, due.Id, nextReviewDate: fixture.Now.AddDays(-1));

        // The explicit per-word path is the sanctioned disclosure: the learner named this word, so
        // the answer is a response to a request rather than a side effect of browsing.
        var detail = await fixture.VocabularyWordDetailTool.GetAsync(due.Id);

        detail.TargetTerm.Should().Be("복습");
    }

    // ---------------------------------------------------------------------
    // Bounded outputs.
    // ---------------------------------------------------------------------

    [Fact]
    public async Task Vocabulary_list_never_returns_more_than_its_ceiling()
    {
        using var fixture = new CoachToolTestFixture();
        fixture.SeedProfile(CoachToolTestFixture.UserA);

        for (var i = 0; i < 40; i++)
        {
            var word = fixture.SeedWord($"단어{i}", $"word {i}");
            fixture.SeedProgress(CoachToolTestFixture.UserA, word.Id);
        }

        var overAsk = await fixture.VocabularySearchTool.SearchAsync(maxResults: 5000);
        overAsk.Words.Should().HaveCount(25);
        overAsk.TotalMatchCount.Should().Be(40);

        var underAsk = await fixture.VocabularySearchTool.SearchAsync(maxResults: 0);
        underAsk.Words.Should().HaveCount(1, "a non-positive ask clamps up to one rather than to none");
    }

    [Fact]
    public async Task Skill_and_resource_lists_never_return_more_than_their_ceiling()
    {
        using var fixture = new CoachToolTestFixture();
        fixture.SeedProfile(CoachToolTestFixture.UserA);

        for (var i = 0; i < 60; i++)
        {
            fixture.SeedSkill(CoachToolTestFixture.UserA, title: $"Skill {i}");
            fixture.SeedResource(CoachToolTestFixture.UserA, title: $"Resource {i}");
        }

        var skills = await fixture.SkillListTool.GetAsync(maxResults: 5000);
        skills.Skills.Count.Should().BeLessThanOrEqualTo(50);
        skills.TotalCount.Should().Be(60);

        var resources = await fixture.LearningResourceListTool.GetAsync(maxResults: 5000);
        resources.Resources.Count.Should().BeLessThanOrEqualTo(30);
        resources.TotalCount.Should().Be(60);
    }

    [Fact]
    public async Task Vocabulary_list_caps_tags_and_language_length()
    {
        using var fixture = new CoachToolTestFixture();
        fixture.SeedProfile(CoachToolTestFixture.UserA);

        var manyTags = string.Join(',', Enumerable.Range(0, 30).Select(i => $"tag{i}"));
        var word = fixture.SeedWord("단어", "word", tags: manyTags);
        fixture.SeedProgress(CoachToolTestFixture.UserA, word.Id);

        var result = await fixture.VocabularySearchTool.SearchAsync();

        var entry = result.Words.Should().ContainSingle().Which;
        entry.Tags.Should().HaveCountLessThanOrEqualTo(8, "an unbounded tag list is an unbounded prompt");
        entry.Tags.Should().OnlyContain(t => t.Length <= 40);
        (entry.Language?.Length ?? 0).Should().BeLessThanOrEqualTo(40);
    }

    // ---------------------------------------------------------------------
    // Explicit learner content is permitted; bulk content is not.
    // ---------------------------------------------------------------------

    [Fact]
    public async Task Word_detail_returns_the_term_the_learner_asked_about()
    {
        using var fixture = new CoachToolTestFixture();
        fixture.SeedProfile(CoachToolTestFixture.UserA);

        var word = fixture.SeedWord("사과", "apple", tags: "food");
        fixture.SeedProgress(CoachToolTestFixture.UserA, word.Id,
            masteryScore: 0.42f, totalAttempts: 10, correctAttempts: 7);

        var detail = await fixture.VocabularyWordDetailTool.GetAsync(word.Id);

        detail.TargetTerm.Should().Be("사과");
        detail.NativeTerm.Should().Be("apple");
        detail.TotalAttempts.Should().Be(10);
        detail.CorrectAttempts.Should().Be(7);
    }

    [Fact]
    public async Task Resource_tools_never_carry_transcript_or_translation_text()
    {
        using var fixture = new CoachToolTestFixture();
        fixture.SeedProfile(CoachToolTestFixture.UserA);

        const string Transcript = "This transcript must never reach the model.";
        const string Translation = "This translation must never reach the model.";
        var resource = fixture.SeedResource(CoachToolTestFixture.UserA, transcript: Transcript);

        var list = await fixture.LearningResourceListTool.GetAsync();
        var detail = await fixture.LearningResourceDetailTool.GetAsync(resource.Id);

        Serialize(list).Should().NotContain(Transcript).And.NotContain(Translation);
        Serialize(detail).Should().NotContain(Transcript).And.NotContain(Translation);

        // The presence flag is the whole disclosure: the coach may know a transcript exists so it
        // can plan listening time, and may not know what it says.
        list.Resources.Should().ContainSingle().Which.HasTranscript.Should().BeTrue();
    }

    [Fact]
    public async Task Vocabulary_tools_never_carry_the_mnemonic_text()
    {
        using var fixture = new CoachToolTestFixture();
        fixture.SeedProfile(CoachToolTestFixture.UserA);

        const string Mnemonic = "A memory aid the coach must never read.";
        var word = fixture.SeedWord("기억", "memory");
        fixture.SeedProgress(CoachToolTestFixture.UserA, word.Id);

        Serialize(await fixture.VocabularySearchTool.SearchAsync()).Should().NotContain(Mnemonic);
        Serialize(await fixture.VocabularyWordDetailTool.GetAsync(word.Id)).Should().NotContain(Mnemonic);
    }

    [Fact]
    public async Task Profile_tools_never_carry_the_api_key_or_the_email()
    {
        using var fixture = new CoachToolTestFixture();
        fixture.SeedProfile(
            CoachToolTestFixture.UserA, email: "captain@example.com", apiKey: "sk-secret-value");

        Serialize(await fixture.CurrentProfileSummaryTool.GetAsync())
            .Should().NotContain("sk-secret-value").And.NotContain("captain@example.com");

        Serialize(await fixture.LearnerSettingsSummaryTool.GetAsync())
            .Should().NotContain("sk-secret-value").And.NotContain("captain@example.com");
    }

    /// <summary>
    /// Archived skills are absent from every learner-facing read, count included.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The list and the detail lookup already excluded them; the profile summary's count did not.
    /// That number is the one Sam says out loud — "you have four skills" — so a count that
    /// included the archive would have Sam contradicting the learner's own skills screen, and
    /// would do it while sounding authoritative.
    /// </para>
    /// <para>
    /// All three are asserted together because they are one claim about the learner's account seen
    /// from three angles, and the interesting failure is any one of them disagreeing with the
    /// other two.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Archived_skills_are_absent_from_every_learner_facing_read()
    {
        using var fixture = new CoachToolTestFixture();
        fixture.SeedProfile(CoachToolTestFixture.UserA);
        fixture.SeedSkill(CoachToolTestFixture.UserA, title: "Active");
        var archived = fixture.SeedSkill(
            CoachToolTestFixture.UserA, title: "Put away", archived: true);

        var summary = await fixture.CurrentProfileSummaryTool.GetAsync();
        summary.SkillCount.Should().Be(1, "the archived skill is not one the learner practises");

        var list = await fixture.SkillListTool.GetAsync();
        Serialize(list).Should().NotContain("Put away");

        var act = async () => await fixture.SkillDetailTool.GetAsync(archived.Id);
        await act.Should().ThrowAsync<CoachToolException>(
            "an archived skill answers the same way one that never existed does");
    }

    [Fact]
    public async Task No_sam_tool_result_carries_the_learner_identifier()
    {
        using var fixture = new CoachToolTestFixture();
        fixture.SeedProfile(CoachToolTestFixture.UserA);
        var resource = fixture.SeedResource(CoachToolTestFixture.UserA);
        var skill = fixture.SeedSkill(CoachToolTestFixture.UserA);
        var word = fixture.SeedWord("단어", "word");
        fixture.SeedProgress(CoachToolTestFixture.UserA, word.Id);

        object[] results =
        [
            await fixture.VocabularySearchTool.SearchAsync(),
            await fixture.VocabularyWordDetailTool.GetAsync(word.Id),
            await fixture.SkillListTool.GetAsync(),
            await fixture.SkillDetailTool.GetAsync(skill.Id),
            await fixture.LearningResourceListTool.GetAsync(),
            await fixture.LearningResourceDetailTool.GetAsync(resource.Id),
            await fixture.CurrentProfileSummaryTool.GetAsync(),
            await fixture.LearnerSettingsSummaryTool.GetAsync(),
            await fixture.CurrentPlanSummaryTool.GetAsync()
        ];

        foreach (var result in results)
        {
            Serialize(result).Should().NotContain(
                CoachToolTestFixture.UserA,
                "the model authenticates through IUserScopeProvider and never needs to see the id");
        }
    }

    // ---------------------------------------------------------------------
    // Empty answers rather than failures.
    // ---------------------------------------------------------------------

    [Fact]
    public async Task List_tools_answer_empty_for_a_learner_with_no_rows()
    {
        using var fixture = new CoachToolTestFixture();
        fixture.SeedProfile(CoachToolTestFixture.UserA);

        (await fixture.VocabularySearchTool.SearchAsync()).Words.Should().BeEmpty();
        (await fixture.SkillListTool.GetAsync()).Skills.Should().BeEmpty();
        (await fixture.LearningResourceListTool.GetAsync()).Resources.Should().BeEmpty();
    }

    [Fact]
    public async Task Detail_tools_reject_a_blank_identifier_without_querying()
    {
        using var fixture = new CoachToolTestFixture();
        fixture.SeedProfile(CoachToolTestFixture.UserA);

        var calls = new List<Func<Task>>
        {
            () => fixture.VocabularyWordDetailTool.GetAsync("   "),
            () => fixture.SkillDetailTool.GetAsync("   "),
            () => fixture.LearningResourceDetailTool.GetAsync("   ")
        };

        foreach (var call in calls)
        {
            fixture.Commands.Reset();
            var failure = (await call.Should().ThrowAsync<CoachToolException>()).Which;
            failure.Kind.Should().Be(CoachToolFailureKind.InvalidArgument);
            fixture.Commands.CommandCount.Should().Be(0);
        }
    }

    /// <summary>Every Sam tool, invoked with its least-argument form.</summary>
    private static IEnumerable<Func<Task>> SamCalls(CoachToolTestFixture fixture) =>
    [
        () => fixture.VocabularySearchTool.SearchAsync(),
        () => fixture.VocabularyWordDetailTool.GetAsync("any-id"),
        () => fixture.SkillListTool.GetAsync(),
        () => fixture.SkillDetailTool.GetAsync("any-id"),
        () => fixture.LearningResourceListTool.GetAsync(),
        () => fixture.LearningResourceDetailTool.GetAsync("any-id"),
        () => fixture.CurrentProfileSummaryTool.GetAsync(),
        () => fixture.LearnerSettingsSummaryTool.GetAsync(),
        () => fixture.CurrentPlanSummaryTool.GetAsync()
    ];

    private static string Serialize(object value) =>
        System.Text.Json.JsonSerializer.Serialize(value);
}
