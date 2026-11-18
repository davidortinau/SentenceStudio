using SentenceStudio.Contracts.Coach;
using SentenceStudio.WebUI.Shared.Coach;

namespace SentenceStudio.UI.Tests.Coach;

/// <summary>Sam opens an activity at the address its tile opens, with what the server checked.</summary>
public class CoachActivityLaunchCardTests
{
    [Fact]
    public void A_german_directions_conversation_opens_with_its_scenario_and_language()
    {
        var url = CoachActivityLaunchCard.Url(new CoachActivityLaunchDto
        {
            Activity = CoachActivityKind.Conversation, Language = "German", ScenarioId = "scn:asking-for-directions"
        }, "conversation");

        Assert.Equal("/conversation?scenarioId=scn%3Aasking-for-directions&language=German", url);
    }

    [Fact]
    public void A_quiz_takes_its_resources_as_a_list_and_a_cloze_takes_one()
    {
        var quiz = CoachActivityLaunchCard.Url(new CoachActivityLaunchDto
        {
            Activity = CoachActivityKind.VocabularyQuiz, ResourceIds = ["r1", "r2"], SkillId = "s1"
        }, "vocab-quiz");
        var cloze = CoachActivityLaunchCard.Url(new CoachActivityLaunchDto
        {
            Activity = CoachActivityKind.Cloze, ResourceIds = ["r1"]
        }, "cloze");

        Assert.Equal("/vocab-quiz?resourceIds=r1,r2&skillId=s1", quiz);
        Assert.Equal("/cloze?resourceId=r1", cloze);
    }

    [Fact]
    public void Only_a_conversation_carries_a_scenario_or_language()
    {
        var url = CoachActivityLaunchCard.Url(new CoachActivityLaunchDto
        {
            Activity = CoachActivityKind.Reading, Language = "German", ScenarioId = "scn:first-meeting"
        }, "reading");

        Assert.Equal("/reading", url);
    }
}
