using FluentAssertions;
using SentenceStudio.Contracts.Coach;
using SentenceStudio.WebUI.Services;

namespace SentenceStudio.UI.Tests.Coach;

/// <summary>
/// How the web and MAUI pages tell Sam what they show: the page's own description, plus what the
/// route names, with only the open page answering.
/// </summary>
public class CoachScreenSubjectTests
{
    [Fact]
    public async Task Only_the_page_that_registered_last_describes_the_screen()
    {
        var subject = new CoachScreenSubject();
        var quiz = subject.Publish(() => new CoachScreenDescription([Word("quiz-word")]));
        var flashcards = subject.Publish(() => new CoachScreenDescription([Word("card-word")]));

        // The quiz page closing after the flashcards opened must not clear the flashcards.
        quiz.Dispose();
        (await subject.DescribeAsync())!.Items.Single().Id.Should().Be("card-word");

        flashcards.Dispose();
        (await subject.DescribeAsync()).Should().BeNull();
    }

    [Fact]
    public async Task The_description_is_read_when_asked_not_when_registered()
    {
        var subject = new CoachScreenSubject();
        var current = "first";
        using var _ = subject.Publish(() => new CoachScreenDescription([Word(current)]));

        current = "second";

        (await subject.DescribeAsync())!.Items.Single().Id.Should().Be("second");
    }

    [Fact]
    public async Task The_description_is_read_through_the_pages_own_dispatcher()
    {
        var subject = new CoachScreenSubject();
        var marshalled = 0;
        Task Invoke(Action work)
        {
            marshalled++;
            work();
            return Task.CompletedTask;
        }

        using var _ = subject.Publish(() => new CoachScreenDescription([Word("w-1")]), Invoke);

        (await subject.DescribeAsync())!.Items.Single().Id.Should().Be("w-1");
        marshalled.Should().Be(1, "page fields are read on the page's dispatcher, never on the sender's thread");
    }

    [Theory]
    [InlineData("vocabulary/edit/{Id}", "vocabulary/edit/w-9", CoachClientScreenItemKinds.VocabularyWord, "w-9")]
    [InlineData("resources/edit/{Id}", "resources/edit/r-3", CoachClientScreenItemKinds.LearningResource, "r-3")]
    [InlineData("skills/edit/{Id}", "skills/edit/s-1", CoachClientScreenItemKinds.Skill, "s-1")]
    [InlineData("minimal-pairs/session/{Id}", "minimal-pairs/session/p-2", CoachClientScreenItemKinds.MinimalPair, "p-2")]
    public void A_route_id_becomes_the_focused_item(string template, string relative, string kind, string id)
    {
        var item = CoachScreenContextCollector.RouteItems(template, relative).First();

        item.Kind.Should().Be(kind);
        item.Id.Should().Be(id);
        item.Focus.Should().BeTrue();
    }

    [Fact]
    public void An_activity_names_itself_and_the_resources_and_skill_it_was_opened_with()
    {
        var items = CoachScreenContextCollector.RouteItems(
            "reading", "reading?resourceId=r-1&skillId=s-4&planItemId=x").ToList();

        items.Should().ContainSingle(i => i.Kind == CoachClientScreenItemKinds.LearningResource && i.Id == "r-1");
        items.Should().ContainSingle(i => i.Kind == CoachClientScreenItemKinds.Skill && i.Id == "s-4");
        items.Should().ContainSingle(i => i.Kind == CoachClientScreenItemKinds.Activity && i.Id == "reading");
        items.Should().NotContain(i => i.Id == "x", "a plan item id is not something Sam's tools take");
    }

    [Fact]
    public void The_page_item_keeps_focus_and_route_duplicates_are_dropped()
    {
        var page = new[] { Word("w-9", focus: true) };
        var route = CoachScreenContextCollector.RouteItems("vocabulary/edit/{Id}", "vocabulary/edit/w-9");

        var items = CoachScreenContextCollector.Items(page, route);

        items.Should().ContainSingle().Which.Id.Should().Be("w-9");
    }

    [Fact]
    public void A_route_item_loses_focus_when_the_page_already_names_one()
    {
        var page = new[] { Word("quiz-word", focus: true) };
        var route = CoachScreenContextCollector.RouteItems("resources/edit/{Id}", "resources/edit/r-1");

        var items = CoachScreenContextCollector.Items(page, route);

        items.Where(i => i.Focus).Should().ContainSingle().Which.Id.Should().Be("quiz-word");
    }

    [Fact]
    public void The_activity_is_the_focus_when_the_page_names_nothing_else()
    {
        // Native marks the activity as "this" when nothing else is; the web does the same.
        var items = CoachScreenContextCollector.Items([], CoachScreenContextCollector.RouteItems(
            "reading", "reading?resourceId=r-1"));

        items.Where(i => i.Focus).Should().ContainSingle().Which.Kind.Should().Be(CoachClientScreenItemKinds.Activity);
    }

    [Fact]
    public void The_activity_yields_focus_to_what_the_page_names()
    {
        var items = CoachScreenContextCollector.Items([Word("quiz-word", focus: true)],
            CoachScreenContextCollector.RouteItems("vocab-quiz", "vocab-quiz"));

        items.Where(i => i.Focus).Should().ContainSingle().Which.Id.Should().Be("quiz-word");
    }

    private static CoachClientScreenItem Word(string id, bool focus = false) =>
        CoachScreenDescription.Item(CoachClientScreenItemKinds.VocabularyWord, id, null, focus: focus);
}
