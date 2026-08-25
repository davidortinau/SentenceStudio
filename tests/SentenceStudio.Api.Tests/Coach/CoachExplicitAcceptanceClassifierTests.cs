using SentenceStudio.Api.Coach.Application;

namespace SentenceStudio.Api.Tests.Coach;

/// <summary>
/// The deterministic acceptance gate. This is the code that decides whether typed words are
/// allowed to change the learner's plan, so an addition here is a security change.
/// </summary>
public class CoachExplicitAcceptanceClassifierTests
{
    private readonly CoachExplicitAcceptanceClassifier _classifier = new();

    [Theory]
    // English
    [InlineData("yes")]
    [InlineData("Yes.")]
    [InlineData("YES!!")]
    [InlineData("  yes  ")]
    [InlineData("yeah")]
    [InlineData("yep")]
    [InlineData("sure")]
    [InlineData("ok")]
    [InlineData("okay")]
    [InlineData("do it")]
    [InlineData("go ahead")]
    [InlineData("please do it")]
    [InlineData("add that")]
    [InlineData("yes, add that")]
    [InlineData("sounds good")]
    [InlineData("accept")]
    // Korean
    [InlineData("네")]
    [InlineData("네.")]
    [InlineData("예")]
    [InlineData("좋아요")]
    [InlineData("그래요")]
    [InlineData("해주세요")]
    [InlineData("추가해줘")]
    public void ClearAffirmatives_AreAffirmative(string text) =>
        _classifier.Classify(text).Should().Be(CoachExplicitAcceptance.Affirmative);

    [Theory]
    // English
    [InlineData("no")]
    [InlineData("No.")]
    [InlineData("nope")]
    [InlineData("no thanks")]
    [InlineData("not now")]
    [InlineData("skip it")]
    [InlineData("cancel")]
    [InlineData("keep the plan")]
    // Korean
    [InlineData("아니요")]
    [InlineData("아뇨")]
    [InlineData("나중에")]
    [InlineData("취소")]
    [InlineData("괜찮아요")]
    public void ClearNegatives_AreNegative(string text) =>
        _classifier.Classify(text).Should().Be(CoachExplicitAcceptance.Negative);

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("maybe")]
    [InlineData("Maybe later")]
    [InlineData("i guess")]
    [InlineData("not sure")]
    [InlineData("whatever you think")]
    [InlineData("hmm")]
    [InlineData("yes but not the speaking one")]
    [InlineData("yes, however keep the reading")]
    [InlineData("no idea")]
    [InlineData("아마도")]
    [InlineData("글쎄요")]
    [InlineData("모르겠어요")]
    [InlineData("네 그런데 듣기는 빼주세요")]
    // A sentence is a request the coach must read, never a bare yes.
    [InlineData("yes and can you also make it fifteen minutes with no audio at all")]
    public void UnknownOrHedgedText_IsAmbiguous(string? text) =>
        _classifier.Classify(text).Should().Be(CoachExplicitAcceptance.Ambiguous);

    [Fact]
    public void OppositeSignalsInOneAnswer_AreAmbiguous()
    {
        _classifier.Classify("yes no").Should().Be(CoachExplicitAcceptance.Ambiguous);
        _classifier.Classify("no yes").Should().Be(CoachExplicitAcceptance.Ambiguous);
    }

    [Fact]
    public void AnythingLongerThanAShortAnswer_IsAmbiguous()
    {
        var text = new string('y', CoachExplicitAcceptanceClassifier.MaxDecisiveLength + 1);
        _classifier.Classify(text).Should().Be(CoachExplicitAcceptance.Ambiguous);
    }

    [Theory]
    // Korean, no question mark.
    [InlineData("\uC88B\uC544\uC694 \uB73B\uC774 \uBB50\uC608\uC694")]
    [InlineData("\uC88B\uC544\uC694 \uBB34\uC2A8 \uC758\uBBF8")]
    [InlineData("\uB124 \uB73B\uC774 \uBB50\uC57C")]
    [InlineData("\uC88B\uC544\uC694 \uBC1C\uC74C \uC54C\uB824\uC918")]
    // English, no question mark.
    [InlineData("does \uC88B\uC544\uC694 mean good")]
    [InlineData("what does yes mean")]
    [InlineData("explain \uB124")]
    [InlineData("the difference between \uB124 and \uC608")]
    [InlineData("how do i say yes")]
    public void AnUnpunctuatedLexicalQuestionIsNeverAnAcceptance(string text)
    {
        // Punctuation is a weak signal on its own. A learner asking what a word means, with no
        // question mark, must not be read as agreeing to change their plan.
        _classifier.Classify(text).Should().Be(CoachExplicitAcceptance.Ambiguous);
    }

    [Fact]
    public void ADecisiveMessageIsMadeOnlyOfDecisiveWords()
    {
        // The invariant behind the rule above: every token in a message that decides must come
        // from the allow-listed vocabulary. A token carrying meaning of its own makes the
        // message something other than a bare decision.
        _classifier.Classify("yes the plan").Should().Be(CoachExplicitAcceptance.Ambiguous);
        _classifier.Classify("\uB124 \uC624\uB298 \uACC4\uD68D").Should().Be(CoachExplicitAcceptance.Ambiguous);

        // And the phrases that are only decisive words still work.
        _classifier.Classify("yes please").Should().Be(CoachExplicitAcceptance.Affirmative);
        _classifier.Classify("\uB124 \uC88B\uC544\uC694").Should().Be(CoachExplicitAcceptance.Affirmative);
    }

    [Theory]
    [InlineData("sounds good")]
    [InlineData("correct")]
    [InlineData("\uC88B\uC544\uC694")]
    [InlineData("\uD574\uC8FC\uC138\uC694")]
    [InlineData("go ahead")]
    public void AWordThatIsItselfADecisionIsNotTreatedAsAQuestionWord(string text)
    {
        // "sounds" and "correct" are plausible question words and also parts of real
        // acceptances. The decision wins, or the phrase list would silently stop working.
        _classifier.Classify(text).Should().Be(CoachExplicitAcceptance.Affirmative);
    }

    [Fact]
    public void CasingAndEmphasisDoNotChangeTheAnswer()
    {
        _classifier.Classify("Y E S").Should().Be(CoachExplicitAcceptance.Ambiguous);
        _classifier.Classify("yes!").Should().Be(CoachExplicitAcceptance.Affirmative);
        _classifier.Classify("  YES  ").Should().Be(CoachExplicitAcceptance.Affirmative);
    }

    [Theory]
    [InlineData("YES?")]
    [InlineData("yes?")]
    [InlineData("\uC88B\uC544\uC694?")]
    [InlineData("ok?")]
    public void AQuestionIsNeverAnAcceptance(string text)
    {
        // Once the coach also answers language questions, a trailing question mark means the
        // learner is asking about a word, not agreeing to change their plan. "\uC88B\uC544\uC694?" is a
        // question about \uC88B\uC544\uC694; "\uC88B\uC544\uC694" is agreement.
        _classifier.Classify(text).Should().Be(CoachExplicitAcceptance.Ambiguous);
    }

    [Theory]
    [InlineData("\"yes\"")]
    [InlineData("what does \uC88B\uC544\uC694 mean")]
    [InlineData("does yes mean \uB124")]
    [InlineData("is \uB124 the same as \uC608")]
    public void TalkingAboutAWordIsNeverAnAcceptance(string text)
    {
        // Quoted text is material being discussed, and a sentence that merely contains a
        // decisive word is not a decision. Only a complete allow-listed phrase decides.
        _classifier.Classify(text).Should().Be(CoachExplicitAcceptance.Ambiguous);
    }
}
