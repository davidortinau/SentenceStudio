using SentenceStudio.Api.Coach.Agents;
using SentenceStudio.Api.Coach.Application;
using SentenceStudio.Contracts.Coach;
using SentenceStudio.Contracts.Coach.Intent;

namespace SentenceStudio.Api.Tests.Coach;

/// <summary>
/// Issue 272: Sam's replies began with "Target:", "Display:" and "Native:", the names of the
/// language field written into the text itself, and a reply to a learner who questioned an
/// answer opened with a flat "No". The learner reads a reply as Sam's own words, so no field
/// name reaches them, and Sam is told to answer warmly.
/// </summary>
public class CoachReplyReadsAsConversationTests
{
    private static readonly CoachLanguageProfile Languages = new("ko-KR", "en-US", "en-US");

    [Fact]
    public void ALanguageNameAtTheStartOfAPieceIsNotShownToTheLearner()
    {
        var result = new CoachAnswerProjection().Project(
            new CoachPedagogicalAnswerIntent
            {
                Topic = CoachAnswerTopic.Vocabulary,
                Blocks =
                [
                    Block(CoachAnswerBlockKind.Answer, Span("Target: Here are two sentences.", CoachLanguageRole.Target)),
                    Block(
                        CoachAnswerBlockKind.Example,
                        Span("Target: 밥 먹었어?", CoachLanguageRole.Target),
                        Span("Native: (asks whether you ate)", CoachLanguageRole.Native)),
                    Block(CoachAnswerBlockKind.Example, Span("target : 물 마셨어?", CoachLanguageRole.Target)),
                    Block(CoachAnswerBlockKind.Note, Span("Display: Note: Both are casual speech.", CoachLanguageRole.Display))
                ]
            },
            Languages);

        result.IsValid.Should().BeTrue();
        var texts = result.Answer!.Blocks.Select(b => b.Spans.Select(s => s.Text).ToArray()).ToArray();
        texts[0].Should().Equal("Here are two sentences.");
        texts[1].Should().Equal("밥 먹었어?", "(asks whether you ate)");
        texts[2].Should().Equal("물 마셨어?");
        // The app already heads this part "Note", so the part's own name goes with the field name.
        texts[3].Should().Equal("Both are casual speech.");

        // The fallback a client without blocks shows is built from the same cleaned pieces.
        result.Answer.PlainText.Should().NotContain("Target:");
        result.Answer.PlainText.Should().NotContain("Display:");
        result.Answer.PlainText.Should().NotContain("Native:");
        result.Answer.PlainText.Should().NotContain("Note:");
    }

    [Fact]
    public void APieceThatIsOnlyALanguageNameIsLeftOutAndTheRestIsKept()
    {
        var result = new CoachAnswerProjection().Project(
            new CoachPedagogicalAnswerIntent
            {
                Topic = CoachAnswerTopic.Vocabulary,
                Blocks =
                [
                    Block(CoachAnswerBlockKind.Answer, Span("It asks whether you ate.", CoachLanguageRole.Display)),
                    Block(
                        CoachAnswerBlockKind.Example,
                        Span("Target:", CoachLanguageRole.Display),
                        Span("밥 먹었어?", CoachLanguageRole.Target))
                ]
            },
            Languages);

        result.IsValid.Should().BeTrue();
        result.Answer!.Blocks[1].Spans.Select(s => s.Text).Should().Equal("밥 먹었어?");
    }

    [Theory]
    [InlineData("Target language sentences need no translation here.")]
    [InlineData("Native speakers shorten it: 밥 먹었어?")]
    [InlineData("The display: what you see on the card.")]
    [InlineData("Notes: there are two.")]
    public void OrdinaryWordsThatOnlyLookLikeAFieldNameAreKept(string text)
    {
        var result = new CoachAnswerProjection().Project(
            new CoachPedagogicalAnswerIntent
            {
                Topic = CoachAnswerTopic.Usage,
                Blocks =
                [
                    Block(CoachAnswerBlockKind.Answer, Span("It depends.", CoachLanguageRole.Display)),
                    Block(CoachAnswerBlockKind.Note, Span(text, CoachLanguageRole.Display))
                ]
            },
            Languages);

        result.IsValid.Should().BeTrue();
        result.Answer!.Blocks[1].Spans.Single().Text.Should().Be(text);
    }

    [Fact]
    public void TheInstructions_KeepTheLanguageNamesOutOfTheText()
    {
        var instructions = OnOneLine(CoachInstructions.Instructions);
        instructions.Should().NotContain("Mark every run of text");
        instructions.Should().Contain("Never write the words Target, Display or Native");
        instructions.Should().Contain("The learner reads the text exactly as you wrote it");
    }

    [Fact]
    public void TheInstructions_SayHowSamSounds()
    {
        var instructions = CoachInstructions.Instructions;
        var start = instructions.IndexOf("HOW YOU SOUND", StringComparison.Ordinal);
        start.Should().BeGreaterThan(0);
        var manner = OnOneLine(
            instructions[start..instructions.IndexOf("CHOOSING THE KIND OF REPLY", StringComparison.Ordinal)]);

        manner.Should().Contain("warm");
        manner.Should().Contain("conversational");
        // A learner who questions an answer is met kindly, never with a flat "No" or a defence.
        manner.Should().Contain("questions, doubts or corrects");
        manner.Should().Contain("Never open with a flat \"No\"");
        manner.Should().Contain("never defend");
        manner.Should().Contain("\"Not quite.");
        manner.Should().Contain("Write it fresh each");
    }

    /// <summary>The instructions wrap at a fixed width; a phrase is looked for across the breaks.</summary>
    private static string OnOneLine(string text) =>
        string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static CoachAnswerBlockIntent Block(CoachAnswerBlockKind kind, params CoachAnswerSpanIntent[] spans) =>
        new() { Kind = kind, Spans = [.. spans] };

    private static CoachAnswerSpanIntent Span(string text, CoachLanguageRole language) =>
        new() { Text = text, Language = language };
}
