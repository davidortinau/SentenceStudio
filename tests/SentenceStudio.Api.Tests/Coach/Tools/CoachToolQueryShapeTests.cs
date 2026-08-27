using SentenceStudio.Api.Coach.Tools;

namespace SentenceStudio.Api.Tests.Coach.Tools;

/// <summary>
/// What the coach tools actually ask the database for.
/// </summary>
/// <remarks>
/// <para>
/// Every other suite in this directory asserts on the object a tool returns. That proves the
/// answer is clean; it does not prove the data was never read. A tool that selected a whole
/// resource row and then built a small record from it would pass every one of those tests while
/// pulling the learner's full transcript into process memory on each call — where the next
/// well-meaning change can put it into a log line, an exception message, or an agent prompt.
/// </para>
/// <para>
/// So these tests read the SQL. They are the reason the persistence boundary is worth having: the
/// projection lives in one query service, and the query service can be checked once for every
/// caller that will ever use it.
/// </para>
/// <para>
/// The transcript is the one column allowed to be named at all, and only inside an emptiness test.
/// "Does this resource have a transcript?" is a fact the coach may know; the transcript itself is
/// not. <c>SELECT "Transcript" IS NOT NULL</c> returns one bit and leaves the document where it
/// is, which is exactly the distinction <see cref="AssertTranscriptIsOnlyTested"/> enforces.
/// </para>
/// </remarks>
public class CoachToolQueryShapeTests
{
    /// <summary>The columns of a word that name it, which a due summary must never select.</summary>
    private static readonly string[] WordTermColumns =
        ["TargetLanguageTerm", "NativeLanguageTerm", "MnemonicText"];

    [Fact]
    public async Task Resource_catalog_never_selects_the_transcript_or_the_translation()
    {
        using var fixture = new CoachToolTestFixture();
        fixture.SeedResource(CoachToolTestFixture.UserA, transcript: "Every word of this stays in the database.");
        fixture.Commands.Reset();

        await fixture.ResourceTool.GetAsync();

        AssertNoneMention(fixture.Commands.CommandTexts, ["Translation"]);
        AssertTranscriptIsOnlyTested(fixture.Commands.CommandTexts);
        AssertEveryReadIsScoped(fixture.Commands.CommandTexts);
    }

    [Fact]
    public async Task Resource_list_never_selects_the_transcript_or_the_translation()
    {
        using var fixture = new CoachToolTestFixture();
        fixture.SeedResource(CoachToolTestFixture.UserA);
        fixture.Commands.Reset();

        await fixture.LearningResourceListTool.GetAsync();

        AssertNoneMention(fixture.Commands.CommandTexts, ["Translation"]);
        AssertTranscriptIsOnlyTested(fixture.Commands.CommandTexts);
        AssertEveryReadIsScoped(fixture.Commands.CommandTexts);
    }

    [Fact]
    public async Task Resource_detail_never_selects_the_transcript_or_the_translation()
    {
        using var fixture = new CoachToolTestFixture();
        var resource = fixture.SeedResource(CoachToolTestFixture.UserA);
        fixture.Commands.Reset();

        await fixture.LearningResourceDetailTool.GetAsync(resource.Id);

        AssertNoneMention(fixture.Commands.CommandTexts, ["Translation"]);
        AssertTranscriptIsOnlyTested(fixture.Commands.CommandTexts);
        AssertEveryReadIsScoped(fixture.Commands.CommandTexts);
    }

    [Fact]
    public async Task Vocabulary_due_summary_never_selects_a_term_or_a_memory_aid()
    {
        using var fixture = new CoachToolTestFixture();
        var user = CoachToolTestFixture.UserA;
        var word = fixture.SeedWord("만기", "due", tags: "food");
        fixture.SeedProgress(user, word.Id, nextReviewDate: fixture.Now.AddDays(-1));
        fixture.Commands.Reset();

        await fixture.VocabularyTool.GetAsync();

        // The summary reports how much is due. Selecting the terms would put the answers to the
        // learner's own upcoming reviews in the process that is about to talk to a model.
        AssertNoneMention(fixture.Commands.CommandTexts, WordTermColumns);
        AssertEveryReadIsScoped(fixture.Commands.CommandTexts);
    }

    [Fact]
    public async Task Profile_reads_never_select_the_email_or_the_api_key()
    {
        using var fixture = new CoachToolTestFixture();
        fixture.SeedProfile(CoachToolTestFixture.UserA);
        fixture.Commands.Reset();

        await fixture.ProfileTool.GetAsync();
        await fixture.LearnerSettingsSummaryTool.GetAsync();
        await fixture.CurrentProfileSummaryTool.GetAsync();

        AssertNoneMention(fixture.Commands.CommandTexts, ["Email", "OpenAI_APIKey", "Name"]);
    }

    [Fact]
    public async Task Every_read_tool_scopes_every_query_it_runs()
    {
        using var fixture = new CoachToolTestFixture();
        var user = CoachToolTestFixture.UserA;
        fixture.SeedProfile(user);
        var resource = fixture.SeedResource(user);
        var skill = fixture.SeedSkill(user);
        var word = fixture.SeedWord("사과", "apple");
        fixture.SeedProgress(user, word.Id);
        fixture.SeedPlan(user);
        fixture.SeedCompletion(user, "Reading", 10, daysAgo: 0);
        fixture.SeedActivity(user, daysAgo: 0);

        var calls = new (string Name, Func<Task> Call)[]
        {
            (nameof(fixture.ProfileTool), () => fixture.ProfileTool.GetAsync()),
            (nameof(fixture.BalanceTool), () => fixture.BalanceTool.GetAsync(CoachPracticeWindow.SevenDays)),
            (nameof(fixture.VocabularyTool), () => fixture.VocabularyTool.GetAsync()),
            (nameof(fixture.ResourceTool), () => fixture.ResourceTool.GetAsync()),
            (nameof(fixture.VocabularySearchTool), () => fixture.VocabularySearchTool.SearchAsync()),
            (nameof(fixture.VocabularyWordDetailTool), () => fixture.VocabularyWordDetailTool.GetAsync(word.Id)),
            (nameof(fixture.SkillListTool), () => fixture.SkillListTool.GetAsync()),
            (nameof(fixture.SkillDetailTool), () => fixture.SkillDetailTool.GetAsync(skill.Id)),
            (nameof(fixture.LearningResourceListTool), () => fixture.LearningResourceListTool.GetAsync()),
            (nameof(fixture.LearningResourceDetailTool), () => fixture.LearningResourceDetailTool.GetAsync(resource.Id)),
            (nameof(fixture.CurrentProfileSummaryTool), () => fixture.CurrentProfileSummaryTool.GetAsync()),
            (nameof(fixture.LearnerSettingsSummaryTool), () => fixture.LearnerSettingsSummaryTool.GetAsync()),
            (nameof(fixture.CurrentPlanSummaryTool), () => fixture.CurrentPlanSummaryTool.GetAsync())
        };

        foreach (var (name, call) in calls)
        {
            fixture.Commands.Reset();
            await call();

            fixture.Commands.CommandTexts.Should().NotBeEmpty($"{name} must actually read something");
            AssertEveryReadIsScoped(fixture.Commands.CommandTexts, name);
        }
    }

    /// <summary>
    /// Every statement filters on an owner column. An unfiltered read is the exact shape of a
    /// cross-tenant leak, so the assertion is about the SQL rather than about the rows the seed
    /// data happened to contain.
    /// </summary>
    private static void AssertEveryReadIsScoped(IReadOnlyList<string> sql, string? subject = null)
    {
        foreach (var statement in sql)
        {
            var scoped = statement.Contains("UserProfileId", StringComparison.Ordinal)
                         || statement.Contains("UserId", StringComparison.Ordinal)
                         // The profile itself is scoped by its own primary key.
                         || (statement.Contains("UserProfile", StringComparison.Ordinal)
                             && statement.Contains("\"Id\" = ", StringComparison.Ordinal));

            scoped.Should().BeTrue(
                "{0} ran a statement with no owner predicate:\n{1}",
                subject ?? "a coach read",
                statement);
        }
    }

    private static void AssertNoneMention(IReadOnlyList<string> sql, IEnumerable<string> columns)
    {
        foreach (var column in columns)
        {
            foreach (var statement in sql)
            {
                statement.Should().NotContain(
                    column,
                    "a coach read must never select {0}; statement was:\n{1}",
                    column,
                    statement);
            }
        }
    }

    /// <summary>
    /// The transcript column may be tested for emptiness and nothing else. Every mention is
    /// stripped when it is immediately part of a null or emptiness comparison; anything left over
    /// is the column being returned.
    /// </summary>
    private static void AssertTranscriptIsOnlyTested(IReadOnlyList<string> sql)
    {
        foreach (var statement in sql)
        {
            var residue = System.Text.RegularExpressions.Regex.Replace(
                statement,
                @"""?Transcript""?\s*(IS\s+NOT\s+NULL|IS\s+NULL|<>|=)",
                string.Empty,
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);

            residue.Should().NotContain(
                "Transcript",
                "the transcript may be tested for emptiness but never selected; statement was:\n{0}",
                statement);
        }
    }
}
