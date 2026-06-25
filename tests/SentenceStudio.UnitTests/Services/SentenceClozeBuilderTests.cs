using FluentAssertions;
using SentenceStudio.Shared.Models;
using SentenceStudio.Shared.Services;

namespace SentenceStudio.UnitTests.Services;

public class SentenceClozeBuilderTests
{
    private static VocabularyWord Word(string target, string? lemma = null) =>
        new() { Id = "w", TargetLanguageTerm = target, Lemma = lemma };

    private static ExampleSentence Sentence(string target, string? native = "I ordered pizza",
        ExampleSentenceStatus status = ExampleSentenceStatus.Curated, bool flagged = false) =>
        new()
        {
            VocabularyWordId = "w",
            TargetSentence = target,
            NativeSentence = native,
            Status = status,
            IsFlagged = flagged,
        };

    [Fact]
    public void TryBuild_BlanksDictionaryForm_KeepsAttachedParticle()
    {
        var cloze = SentenceClozeBuilder.TryBuild(Sentence("저는 주문을 했어요"), Word("주문"));

        cloze.Should().NotBeNull();
        cloze!.BlankedText.Should().Be("저는 ____을 했어요");
        cloze.Answer.Should().Be("주문");
        cloze.Translation.Should().Be("I ordered pizza");
        cloze.Options.Should().BeNull(); // open typing when no distractors
    }

    [Fact]
    public void TryBuild_BuildsWordBank_WhenDistractorsProvided()
    {
        var cloze = SentenceClozeBuilder.TryBuild(
            Sentence("저는 주문을 했어요"), Word("주문"),
            distractors: new[] { "예약", "주문", "취소" }); // dup of answer is de-duped

        cloze!.Options.Should().NotBeNull();
        cloze.Options!.First().Should().Be("주문");
        cloze.Options.Should().BeEquivalentTo(new[] { "주문", "예약", "취소" });
    }

    [Fact]
    public void TryBuild_FallsBackToLemma_WhenTargetTermAbsent()
    {
        var cloze = SentenceClozeBuilder.TryBuild(
            Sentence("피자를 주문하고 싶어요"), Word("주문하다", lemma: "주문"));

        cloze.Should().NotBeNull();
        cloze!.Answer.Should().Be("주문");
        cloze.BlankedText.Should().Be("피자를 ____하고 싶어요");
    }

    [Fact]
    public void TryBuild_ReturnsNull_ForSuggestedOrFlaggedSentence()
    {
        SentenceClozeBuilder.TryBuild(Sentence("저는 주문을 했어요", status: ExampleSentenceStatus.Suggested), Word("주문"))
            .Should().BeNull();
        SentenceClozeBuilder.TryBuild(Sentence("저는 주문을 했어요", flagged: true), Word("주문"))
            .Should().BeNull();
    }

    [Fact]
    public void TryBuild_ReturnsNull_WhenWordNotInSentence()
    {
        SentenceClozeBuilder.TryBuild(Sentence("안녕하세요"), Word("주문")).Should().BeNull();
    }

    [Fact]
    public void TryBuild_NullTranslation_WhenSentenceHasNoNative()
    {
        var cloze = SentenceClozeBuilder.TryBuild(Sentence("주문을 했어요", native: null), Word("주문"));
        cloze!.Translation.Should().BeNull();
    }
}
