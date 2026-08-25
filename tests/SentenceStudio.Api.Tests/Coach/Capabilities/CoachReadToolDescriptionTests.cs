using FluentAssertions;
using SentenceStudio.Api.Coach.Tools;
using SentenceStudio.Contracts.Coach;

namespace SentenceStudio.Api.Tests.Coach.Capabilities;

/// <summary>
/// The read-tool descriptions are model-facing prompt surface, so their content is a contract.
/// </summary>
/// <remarks>
/// <para>
/// The rewrite's brief was that each read description states three things a model otherwise has to
/// guess: what order the rows come back in, what filters were applied, and what the counts count.
/// Guessing any of the three produces a confident wrong answer — "you have 12 words due" when the
/// number was tags, or "here are your newest resources" when the order was last-used.
/// </para>
/// <para>
/// <b>Text authority is River's.</b> These assertions pin the properties the text must have, not
/// the wording. The wording in the registry is a truthful draft written from the W2 scope
/// declarations each tool actually emits, and it is pending River's review before closure.
/// </para>
/// </remarks>
public class CoachReadToolDescriptionTests
{
    private static IReadOnlyList<CoachToolRegistration> ReadTools() =>
        CapabilityFixtures.FrozenRegistry().All
            .Where(r => r.RiskClass == CoachToolRiskClass.Read)
            .ToList();

    [Fact]
    public void The_read_tool_population_is_the_one_this_file_claims_to_cover()
    {
        // Non-vacuity, and a tripwire. The design review names thirteen read descriptions; the
        // frozen registry declares fourteen Read-class tools, the fourteenth being
        // preview_practice_plan, which computes a projection rather than reading stored rows.
        // Recorded here rather than reconciled silently — flagged for Simon.
        var reads = ReadTools();

        reads.Should().HaveCount(14);
        reads.Count(r => r.Name == CoachToolNames.PreviewPracticePlan).Should().Be(1);
        reads.Count(r => r.Name != CoachToolNames.PreviewPracticePlan)
            .Should().Be(13, "the other thirteen read stored rows");
    }

    [Fact]
    public void Every_read_description_says_something_about_order()
    {
        var reads = ReadTools();
        reads.Should().NotBeEmpty();

        foreach (var tool in reads)
        {
            var text = tool.Description.ToLowerInvariant();

            var statesOrder =
                text.Contains("order") || text.Contains("first") || text.Contains("last")
                || text.Contains("no order") || text.Contains("sort");

            statesOrder.Should().BeTrue(
                $"'{tool.Name}' must tell the model what order it gets, including when the answer "
                + "has none");
        }
    }

    [Fact]
    public void Every_read_description_says_what_the_counts_mean_or_that_there_are_none()
    {
        var reads = ReadTools();
        reads.Should().NotBeEmpty();

        foreach (var tool in reads)
        {
            var text = tool.Description.ToLowerInvariant();

            var statesCounts = text.Contains("count") || text.Contains("total") || text.Contains("minutes");

            statesCounts.Should().BeTrue(
                $"'{tool.Name}' must say what a number in its answer counts, or say there is none "
                + "to interpret");
        }
    }

    [Fact]
    public void Every_read_description_states_its_filters_or_says_it_has_none()
    {
        var reads = ReadTools();
        reads.Should().NotBeEmpty();

        foreach (var tool in reads)
        {
            var text = tool.Description.ToLowerInvariant();

            var statesFilters =
                text.Contains("filter") || text.Contains("exclud") || text.Contains("bounded")
                || text.Contains("window") || text.Contains("page") || text.Contains("single")
                || text.Contains("snapshot") || text.Contains("not listed") || text.Contains("named by the caller");

            statesFilters.Should().BeTrue(
                $"'{tool.Name}' must say what was left out, or make clear nothing was");
        }
    }

    [Fact]
    public void The_descriptions_that_withhold_content_say_so()
    {
        var registry = CapabilityFixtures.FrozenRegistry();

        registry.Find(CoachToolNames.ListUserVocabularies)!.Description
            .Should().Contain("withheld", "the due-word exclusion is disclosed as a count and a reason");

        registry.Find(CoachToolNames.GetLearningResourceList)!.Description
            .Should().Contain("Never returns transcript", "the embargo is stated, not implied");

        registry.Find(CoachToolNames.GetVocabularyDueSummary)!.Description
            .Should().Contain("counts only", "an aggregate must not read as a list of words");
    }

    [Fact]
    public void No_read_description_promises_a_write()
    {
        foreach (var tool in ReadTools())
        {
            var text = tool.Description.ToLowerInvariant();

            text.Should().NotContain("saves", $"'{tool.Name}' is read-only");
            text.Should().NotContain("updates the", $"'{tool.Name}' is read-only");
        }
    }

    [Fact]
    public void Every_description_is_substantive_rather_than_a_placeholder()
    {
        var all = CapabilityFixtures.FrozenRegistry().All;
        all.Should().NotBeEmpty();

        foreach (var tool in all)
        {
            tool.Description.Should().NotBeNullOrWhiteSpace();
            tool.Description.Length.Should().BeGreaterThan(
                40, $"'{tool.Name}' has a description too short to state order, filters and counts");
        }
    }
}
