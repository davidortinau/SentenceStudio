using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.AI;
using SentenceStudio.Api.Coach.Tools;
using SentenceStudio.Contracts.Coach;

namespace SentenceStudio.Api.Tests.Coach.Tools;

/// <summary>
/// What get_current_screen hands the model: the screen the learner's app reported with the turn,
/// bounded and cleaned, with the main thing first and a hidden quiz answer kept hidden.
/// </summary>
public class CurrentScreenToolTests
{
    private static readonly JsonSerializerOptions ModelSerializerOptions = new(AIJsonUtilities.DefaultOptions);

    [Fact]
    public async Task A_turn_with_no_screen_says_so_rather_than_inventing_one()
    {
        using var fixture = new CoachToolTestFixture();
        fixture.Turn.ClientScreen = null;

        var result = await fixture.CurrentScreenTool.GetAsync();

        result.Available.Should().BeFalse();
        result.Screen.Should().BeNull();
        result.Items.Should().BeEmpty();
        result.Facts.Should().BeEmpty();
    }

    [Fact]
    public async Task The_quiz_word_on_screen_comes_back_with_the_id_the_vocabulary_tools_take()
    {
        using var fixture = new CoachToolTestFixture();

        var result = await fixture.CurrentScreenTool.GetAsync();

        result.Available.Should().BeTrue();
        result.Screen.Should().Be("vocabulary-quiz");
        result.Platform.Should().Be("ios");
        var item = result.Items.Should().ContainSingle().Which;
        item.Kind.Should().Be(CurrentScreenItemKind.VocabularyWord);
        item.Id.Should().Be("word-on-screen");
        item.Label.Should().Be("생선");
        item.Focus.Should().BeTrue();
        result.Facts.Should().ContainSingle(f => f.Name == "phase" && f.Value == "Ready");
    }

    [Fact]
    public async Task The_focused_item_comes_first_whatever_order_the_app_sent()
    {
        using var fixture = new CoachToolTestFixture();
        fixture.Turn.ClientScreen = Screen(
            Item(CoachClientScreenItemKinds.LearningResource, "resource-1", "Drama"),
            Item(CoachClientScreenItemKinds.VocabularyWord, "word-1", "포도", focus: true));

        var result = await fixture.CurrentScreenTool.GetAsync();

        result.Items.Select(i => i.Id).Should().Equal("word-1", "resource-1");
    }

    [Fact]
    public async Task A_hidden_answer_withholds_every_detail_and_says_so()
    {
        using var fixture = new CoachToolTestFixture();
        var screen = Screen(Item(CoachClientScreenItemKinds.VocabularyWord, "word-1", "생선", detail: "fish", focus: true));
        fixture.Turn.ClientScreen = new CoachClientScreenContext
        {
            Route = screen.Route, Items = screen.Items, AnswerHidden = true
        };

        var result = await fixture.CurrentScreenTool.GetAsync();

        result.AnswerHidden.Should().BeTrue();
        result.Items.Single().Detail.Should().BeNull("the meaning is the answer the learner is still working out");
        result.Items.Single().Label.Should().Be("생선");
    }

    [Fact]
    public async Task Shown_details_reach_the_model_once_the_answer_is_shown()
    {
        using var fixture = new CoachToolTestFixture();
        fixture.Turn.ClientScreen = Screen(
            Item(CoachClientScreenItemKinds.VocabularyWord, "word-1", "생선", detail: "fish", focus: true));

        var result = await fixture.CurrentScreenTool.GetAsync();

        result.Items.Single().Detail.Should().Be("fish");
    }

    [Fact]
    public async Task An_unknown_kind_is_relayed_as_other_and_blank_items_are_dropped()
    {
        using var fixture = new CoachToolTestFixture();
        fixture.Turn.ClientScreen = Screen(
            Item("future-thing", "x-1", "Something new"),
            Item(" ", "x-2", "No kind"));

        var result = await fixture.CurrentScreenTool.GetAsync();

        result.Items.Should().ContainSingle().Which.Kind.Should().Be(CurrentScreenItemKind.Other);
    }

    [Fact]
    public async Task Labels_are_cleaned_and_everything_is_bounded()
    {
        using var fixture = new CoachToolTestFixture();
        var items = Enumerable.Range(0, 30)
            .Select(i => Item(CoachClientScreenItemKinds.VocabularyWord, $"word-{i}", "line\nbreak" + new string('가', 400)))
            .ToArray();
        fixture.Turn.ClientScreen = new CoachClientScreenContext
        {
            Route = "vocabulary.list",
            Items = items,
            State = Enumerable.Range(0, 40)
                .Select(i => new CoachClientScreenStateEntry { Key = $"k{i}", Value = new string('v', 500) })
                .ToArray()
        };

        var result = await fixture.CurrentScreenTool.GetAsync();

        result.Items.Should().HaveCount(SentenceStudio.Api.Coach.Tools.SamTools.CurrentScreenTool.MaxItems);
        result.Facts.Should().HaveCount(SentenceStudio.Api.Coach.Tools.SamTools.CurrentScreenTool.MaxFacts);
        result.Items.Should().OnlyContain(i => i.Label!.Length <= 160 && !i.Label.Contains('\n'));
        result.Facts.Should().OnlyContain(f => f.Value!.Length <= 120);
        result.Scope.Truncated.Should().BeTrue();
        result.Scope.ReturnedCount.Should().Be(28);
        result.Scope.MatchedCount.Should().Be(70);
    }

    [Fact]
    public async Task A_focused_item_past_the_bound_is_kept_and_put_first()
    {
        using var fixture = new CoachToolTestFixture();
        var items = Enumerable.Range(0, 20)
            .Select(i => Item(CoachClientScreenItemKinds.VocabularyWord, $"word-{i}", $"w{i}"))
            .Append(Item(CoachClientScreenItemKinds.LearningResource, "the-focus", "Drama", focus: true))
            .ToArray();
        fixture.Turn.ClientScreen = Screen(items);

        var result = await fixture.CurrentScreenTool.GetAsync();

        result.Items.First().Id.Should().Be("the-focus");
    }

    [Fact]
    public async Task An_item_that_names_nothing_is_not_reported()
    {
        using var fixture = new CoachToolTestFixture();
        fixture.Turn.ClientScreen = Screen(
            Item(CoachClientScreenItemKinds.Text, null, "  ", focus: true),
            Item(CoachClientScreenItemKinds.Activity, "writing", null));

        var result = await fixture.CurrentScreenTool.GetAsync();

        result.Items.Should().ContainSingle().Which.Id.Should().Be("writing");
    }

    [Theory]
    [InlineData("native.vocabulary-quiz", "vocabulary-quiz", "vocabulary-quiz")]
    [InlineData("/vocab-quiz", "/vocab-quiz?resourceIds=a,b&planItemId=c", "vocab-quiz")]
    [InlineData(null, "/resources/edit/r-1?tab=words", "resources/edit/r-1")]
    public async Task The_screen_is_named_never_given_as_a_raw_route(string? screenId, string route, string expected)
    {
        using var fixture = new CoachToolTestFixture();
        fixture.Turn.ClientScreen = new CoachClientScreenContext { ScreenId = screenId, Route = route };

        var result = await fixture.CurrentScreenTool.GetAsync();

        result.Screen.Should().Be(expected);
    }

    [Fact]
    public async Task The_model_reads_kinds_as_words()
    {
        using var fixture = new CoachToolTestFixture();

        var json = JsonSerializer.SerializeToElement(await fixture.CurrentScreenTool.GetAsync(), ModelSerializerOptions);

        var item = json.GetProperty("items")[0];
        item.GetProperty("kind").GetString().Should().Be("VocabularyWord");
        item.GetProperty("id").GetString().Should().Be("word-on-screen");
        json.GetProperty("answerHidden").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public void The_pages_own_facts_come_before_the_shell_state()
    {
        var view = SentenceStudio.Api.Coach.Tools.SamTools.CurrentScreenTool.Read(new CoachClientScreenContext
        {
            ScreenId = "/vocab-quiz",
            Facts = [new CoachClientScreenStateEntry { Key = "promptLanguage", Value = "native" }],
            State = [new CoachClientScreenStateEntry { Key = "viewport", Value = "390x844" }]
        });

        view.Facts.Select(f => f.Name).Should().Equal("promptLanguage", "viewport");
    }

    [Fact]
    public void An_item_sent_without_a_kind_is_dropped_and_the_turn_still_reads()
    {
        // The screen is advisory: one malformed item must not fail the learner's message.
        var json = """{"screenId":"/reading","items":[{"id":"w-1","label":"x"},{"kind":"vocabulary-word","id":"w-2","label":"포도"}]}""";

        var screen = JsonSerializer.Deserialize<CoachClientScreenContext>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        var view = SentenceStudio.Api.Coach.Tools.SamTools.CurrentScreenTool.Read(screen);

        view.Items.Should().ContainSingle().Which.Id.Should().Be("w-2");
    }

    private static CoachClientScreenContext Screen(params CoachClientScreenItem[] items) => new()
    {
        Route = "vocabulary-quiz",
        Platform = "ios",
        Items = items
    };

    private static CoachClientScreenItem Item(
        string kind, string? id, string? label, string? detail = null, bool focus = false) => new()
    {
        Kind = kind, Id = id, Label = label, Detail = detail, Focus = focus
    };
}
