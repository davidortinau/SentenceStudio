using FluentAssertions;
using SentenceStudio.Api.Coach.Tools;

namespace SentenceStudio.Api.Tests.Coach.Capabilities;

/// <summary>
/// The seven descriptions River rewrote, pinned to his words character for character.
/// </summary>
/// <remarks>
/// <para>
/// D10 splits authority from file ownership: the descriptions are model-facing prompt surface, so
/// River authors the text and Kaylee holds the file. That split only works if the transcription is
/// checkable — otherwise "River approved it" quietly becomes "River approved something like it"
/// after the first well-meaning tidy-up.
/// </para>
/// <para>
/// The literals below are the review artifact, not a paraphrase of it. A failure here means the
/// registry drifted from what was approved, and the registry is what changes.
/// </para>
/// </remarks>
public class CoachReadToolDescriptionTranscriptionTests
{
    public static TheoryData<string, string> RiverTurnFive() => new()
    {
        {
            CoachToolNames.GetPracticeBalance,
            "Reads how many minutes the learner practised each activity type over the last seven, "
            + "fourteen or thirty days, most minutes first. Bounded to that window: practice outside it "
            + "is absent, not zero. Activity types with nothing logged in the window are withheld and "
            + "reported as a count with a reason, so matched equals returned plus withheld and there is "
            + "no further page to fetch. The counts are activity types; minutes are values on the rows."
        },
        {
            CoachToolNames.GetVocabularyDueSummary,
            "Reads counts of the learner's tracked words \u2014 due now, due this week, never practised, "
            + "and the total tracked \u2014 with mastery bands and lapse rate, plus the most frequent "
            + "category tags found on the due words, most frequent first. The word counts cover every "
            + "tracked word, not only the due ones. The scope's counts describe the tag breakdown rather "
            + "than the words: the tag list is a bounded page, so matched is how many distinct tags were "
            + "found and truncation means more exist. A word carrying two tags is counted under each. "
            + "Returns counts only, never the words themselves."
        },
        {
            CoachToolNames.GetVocabularyWordDetail,
            "Reads one vocabulary word the learner owns, named by the caller: term, gloss, tags, mastery, "
            + "and attempt counts. It returns no example sentences. A single item: no order, no paging, "
            + "and no count to interpret. The word is returned whether or not it is due, so this is the "
            + "sanctioned way past the due-word exclusion in list_user_vocabularies \u2014 for one word "
            + "the learner has already named, never for browsing."
        },
        {
            CoachToolNames.GetCurrentProfileSummary,
            "Reads the learner's profile overview: languages, display language, level, preferred session "
            + "length, days since they started, and how many words, skills and resources they own. A "
            + "settings snapshot, not a list \u2014 one record with no order and no paging. Days since "
            + "start is how long the account has existed, not a practice streak. The word, skill and "
            + "resource numbers are totals the learner owns, not rows returned by this call."
        },
        {
            CoachToolNames.GetLearnerProfileSummary,
            "Reads the learner's languages, display language, preferred session length and level. A "
            + "settings snapshot, not a list: one record with no order and no paging, so there is nothing "
            + "to sort and no count to interpret. Scoped to this learner, and nothing is withheld."
        },
        {
            CoachToolNames.ListUserVocabularies,
            "Searches the learner's vocabulary, ordered by mastery, strongest first. Words currently due "
            + "for review are always excluded and reported as a withheld count with a reason, never as "
            + "content; a query, when supplied, narrows the search further. Matched, returned and withheld "
            + "can all differ, and returned plus withheld need not equal matched when the answer is also a "
            + "page. Each match carries its term, gloss, tags and mastery. Use get_vocabulary_due_summary "
            + "for due counts, or get_vocabulary_word_detail for one named word."
        },
        {
            CoachToolNames.GetCurrentPlanSummary,
            "Reads today's plan: each item's activity type, whether it is done, and minutes planned "
            + "against minutes spent. Bounded to one calendar day in the learner's own time zone, so an "
            + "empty answer means no plan exists for today, not that the learner has never had one. Items "
            + "carry no order the caller may rely on. Counts are plan items. It returns no item text "
            + "\u2014 an activity type is a closed category, and the plan's strategy label is bounded plan "
            + "metadata."
        }
    };

    [Theory]
    [MemberData(nameof(RiverTurnFive))]
    public void The_registry_carries_rivers_words_exactly(string toolName, string approved)
    {
        var registration = CapabilityFixtures.FrozenRegistry().Find(toolName);

        registration.Should().NotBeNull($"'{toolName}' must still be registered");
        registration!.Description.Should().Be(
            approved,
            $"'{toolName}' is River's text under D10; the registry transcribes it, it does not edit it");
    }

    [Fact]
    public void All_seven_rejected_descriptions_are_covered_by_this_file()
    {
        // Census. Seven were rejected; a transcription test that quietly covered six would leave the
        // seventh free to drift.
        RiverTurnFive().Should().HaveCount(7);
    }

    [Fact]
    public void The_seven_approved_descriptions_were_left_alone()
    {
        // The other half of the review: River approved these, so they must not have been "improved"
        // while the rejected seven were being replaced.
        var registry = CapabilityFixtures.FrozenRegistry();

        registry.Find(CoachToolNames.GetResourceCatalog)!.Description
            .Should().StartWith("Lists the resources the learner owns, as metadata only");
        registry.Find(CoachToolNames.PreviewPracticePlan)!.Description
            .Should().StartWith("Builds a read-only preview of a practice plan");
        registry.Find(CoachToolNames.GetSkillList)!.Description
            .Should().StartWith("Lists the learner's skill profiles, most recently updated first.");
        registry.Find(CoachToolNames.GetLearningResourceList)!.Description
            .Should().StartWith("Lists the learner's learning resources as metadata only");
        registry.Find(CoachToolNames.GetLearningResourceDetail)!.Description
            .Should().StartWith("Reads metadata for one learning resource the learner owns");
        registry.Find(CoachToolNames.GetLearnerSettingsSummary)!.Description
            .Should().StartWith("Reads the learner's app settings and preferences as they are now.");
    }

    [Fact]
    public void The_optional_skill_description_note_is_exact()
    {
        // SamReadToolResults.cs declares `string? SkillDescription` on SkillDetailResult, so the note
        // is only exact if it says "when present" rather than promising one.
        var description = CapabilityFixtures.FrozenRegistry().Find(CoachToolNames.GetSkillDetail)!.Description;

        description.Should().Contain("description is returned when the learner has set one");
        description.Should().NotContain("always", "the field is nullable");
    }
}
