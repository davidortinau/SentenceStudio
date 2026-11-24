using SentenceStudio.Api.Coach.Agents;
using SentenceStudio.Api.Coach.Tools;
using SentenceStudio.Api.Tests.Coach.Tools;

namespace SentenceStudio.Api.Tests.Coach;

/// <summary>
/// Issue 258: Sam answers what the learner asks, and being due for review is not a secret. Pins
/// the instruction lines that carry that, and the two read tools that used to leave due words out.
/// </summary>
public class CoachDueWordsAreNotWithheldTests
{
    // ---------------------------------------------------------------- the instructions

    [Theory]
    [InlineData("Words due for review")]
    [InlineData("say how many were withheld")]
    [InlineData("keeps its example sentences hidden")]
    [InlineData("hidden until")]
    [InlineData("from their review list")]
    [InlineData("due review words")]
    [InlineData("due-review")]
    public void TheInstructions_NoLongerTellSamToWithholdDueWords(string removed)
    {
        CoachInstructions.Instructions.Should().NotContainEquivalentOf(removed);
    }

    [Fact]
    public void TheInstructions_SayThatBeingDueIsNotASecret()
    {
        CoachInstructions.Instructions.Should().Contain("Being due for review is not a secret.");
        CoachInstructions.Instructions.Should().Contain(
            "Never hold back, hide or refuse a word, its meaning or its sentences");
    }

    [Fact]
    public void TheInstructions_SayHowToAnswerARequestForExampleSentences()
    {
        var instructions = CoachInstructions.Instructions;
        instructions.Should().Contain("\"show me 3 sentences with");
        instructions.Should().Contain("as many as they asked for");
        instructions.Should().Contain("in the target language with its translation");
        instructions.Should().Contain("whether the word is in their vocabulary, whether it is due for review");
        instructions.Should().Contain("offer to save the sentences to it");
        instructions.Should().Contain("Nothing is saved until they approve.");
    }

    [Fact]
    public void TheInstructions_SayHowToDeflectAnOffTopicMessage()
    {
        var instructions = CoachInstructions.Instructions;
        var start = instructions.IndexOf("- OffTopic is a message about neither language learning nor this app", StringComparison.Ordinal);
        start.Should().BeGreaterThan(0);
        var guidance = instructions[start..instructions.IndexOf("- The plan kinds are only", start, StringComparison.Ordinal)];
        guidance.Should().Contain("Do not answer the question.");
        guidance.Should().Contain("good-humoured");
        guidance.Should().Contain("in your own words");
        guidance.Should().Contain("in the language");
        guidance.Should().Contain("you are using with them");
        guidance.Should().Contain("steer back to their learning");
        guidance.Should().Contain("Write it fresh each");
    }

    [Fact]
    public void TheInstructions_SayToAskHintOrAnswerWhenTheScreenHidesAnAnswer()
    {
        var instructions = CoachInstructions.Instructions;
        instructions.Should().Contain("\"Would you like a hint, or the answer?\"");
        instructions.Should().Contain("Then honour their reply");
        instructions.Should().Contain("When the context");
        // Unchanged from PR 253: asked for outright, it is given; a hint request gets a hint.
        instructions.Should().Contain("do not refuse, and do not offer a hint in its place");
        instructions.Should().Contain("help or for a hint still gets a hint, not the answer");
    }

    [Fact]
    public void TheInstructions_AllowAGeneralReplyOfSixteenHundredCharacters()
    {
        CoachInstructions.Instructions.Should().Contain("CoachMessage stays under 1,600 characters");
        CoachInstructions.Instructions.Should().NotContain("400 characters");
    }

    // ---------------------------------------------------------------- the read tools

    [Fact]
    public async Task ListUserVocabularies_ReturnsADueWord()
    {
        using var fixture = new CoachToolTestFixture();
        var user = CoachToolTestFixture.UserA;
        var due = fixture.SeedWord("\uC0DD\uC120", "fish", owner: user);
        fixture.SeedProgress(user, due.Id, masteryScore: 0.4f, nextReviewDate: fixture.Now.AddDays(-1));
        var later = fixture.SeedWord("\uC0AC\uACFC", "apple", owner: user);
        fixture.SeedProgress(user, later.Id, masteryScore: 0.6f, nextReviewDate: fixture.Now.AddDays(3));

        var result = await fixture.VocabularySearchTool.SearchAsync(null, 10, null, null, null, false);

        result.Words.Select(w => w.TargetTerm).Should().BeEquivalentTo(["\uC0DD\uC120", "\uC0AC\uACFC"]);
        result.Scope.WithheldCount.Should().Be(0);
        result.Scope.WithheldReason.Should().Be(CoachScopeWithheldReason.None);
        result.Scope.Filters.Should().NotHaveFlag(CoachScopeFilters.ExcludeDue);
        CoachScopeInvariants.Violations(result.Scope).Should().BeEmpty();
    }

    [Fact]
    public async Task ListUserVocabularies_ReturnsADueWord_ThroughTheFilteredSearch()
    {
        using var fixture = new CoachToolTestFixture();
        var user = CoachToolTestFixture.UserA;
        var due = fixture.SeedWord("\uC0DD\uC120", "fish", owner: user);
        fixture.SeedProgress(user, due.Id, nextReviewDate: fixture.Now.AddDays(-1));

        var result = await fixture.VocabularySearchTool.SearchAsync("\uC0DD\uC120", 10, null, null, null, true);

        result.Words.Should().ContainSingle().Which.TargetTerm.Should().Be("\uC0DD\uC120");
        result.Scope.WithheldCount.Should().Be(0);
    }

    [Fact]
    public async Task GetVocabularyExampleSentences_ReturnsADueWordsSentences()
    {
        using var fixture = new CoachToolTestFixture();
        var user = CoachToolTestFixture.UserA;
        var word = fixture.SeedWord("\uC0DD\uC120", "fish", owner: user);
        fixture.SeedProgress(user, word.Id, nextReviewDate: fixture.Now.AddHours(-1));
        fixture.SeedExampleSentence(word, "\uC2DC\uC7A5\uC5D0\uC11C \uC0DD\uC120\uC744 \uC0C0\uC5B4\uC694.", "I bought fish at the market.");

        var result = await fixture.VocabularyExampleSentencesTool.GetAsync(word.Id);

        result.IsDueForReview.Should().BeTrue("being due is still reported, as a fact");
        result.TargetTerm.Should().Be("\uC0DD\uC120");
        result.Sentences.Should().ContainSingle()
            .Which.TargetSentence.Should().Be("\uC2DC\uC7A5\uC5D0\uC11C \uC0DD\uC120\uC744 \uC0C0\uC5B4\uC694.");
        result.Scope.WithheldCount.Should().Be(0);
        result.Scope.WithheldReason.Should().Be(CoachScopeWithheldReason.None);
        result.Scope.Filters.Should().NotHaveFlag(CoachScopeFilters.ExcludeDue);
        CoachScopeInvariants.Violations(result.Scope).Should().BeEmpty();
    }
}
