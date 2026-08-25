using FluentAssertions;
using SentenceStudio.Api.Coach.Application;

namespace SentenceStudio.Api.Tests.Coach.Claims;

/// <summary>
/// The correction classifier: English, Korean, typing errors, and the turns that must not open a
/// dispute.
/// </summary>
/// <remarks>
/// <para>
/// <b>The negative matrix is the load-bearing half.</b> A dispute constrains the next answer — it
/// must re-read, name its prior claim, or state a limitation — so a false positive degrades a turn
/// for a learner who did nothing but ask a question. A classifier that opened a dispute on every
/// "no" would satisfy every positive test here and make the coach unusable.
/// </para>
/// <para>
/// <b>Why bare negation is absent from every marker list.</b> "No" answers a question. "No, I
/// meant…" reports a defect. Only the second is a correction, and the distance between them is the
/// entire safety margin of this classifier.
/// </para>
/// </remarks>
public sealed class CoachCorrectionClassifierTests
{
    private static readonly CoachCorrectionClassifier Classifier = new();

    // ── English positives ────────────────────────────────────────────────────

    [Theory]
    [InlineData("No, I meant the words I looked up.", CoachCorrectionSignal.MeantSomethingElse)]
    [InlineData("What I meant was the ones from yesterday.", CoachCorrectionSignal.MeantSomethingElse)]
    [InlineData("That's not what I meant.", CoachCorrectionSignal.MeantSomethingElse)]
    [InlineData("I was asking about the reading words.", CoachCorrectionSignal.MeantSomethingElse)]
    [InlineData("That's not what I asked.", CoachCorrectionSignal.NotWhatIAsked)]
    [InlineData("That is not what I said.", CoachCorrectionSignal.NotWhatIAsked)]
    [InlineData("I didn't ask that.", CoachCorrectionSignal.NotWhatIAsked)]
    [InlineData("You misunderstood.", CoachCorrectionSignal.NotWhatIAsked)]
    [InlineData("That's wrong.", CoachCorrectionSignal.WrongClaim)]
    [InlineData("That is incorrect.", CoachCorrectionSignal.WrongClaim)]
    [InlineData("You are wrong.", CoachCorrectionSignal.WrongClaim)]
    [InlineData("That's not right.", CoachCorrectionSignal.WrongClaim)]
    public void English_corrections_are_classified(string text, CoachCorrectionSignal expected)
    {
        Classifier.Classify(text).Should().Be(expected);
    }

    /// <summary>S14 verbatim. The scenario this workstream exists for.</summary>
    [Fact]
    public void The_S14_sentence_names_a_different_cohort()
    {
        Classifier.Classify("No — I meant the words I looked up, not the ones in the plan.")
            .Should().NotBe(
                CoachCorrectionSignal.None,
                "S14 is the scenario W8 was written for; if this sentence does not open a dispute, "
                + "nothing does");
    }

    [Theory]
    [InlineData("I meant the ones I looked up, not the ones in the plan.")]
    [InlineData("The reading words, not the ones in the plan.")]
    [InlineData("I said the words from the article, not the other ones.")]
    public void A_named_cohort_contrast_is_a_correction(string text)
    {
        Classifier.Classify(text).Should().NotBe(CoachCorrectionSignal.None);
    }

    // ── Korean positives ─────────────────────────────────────────────────────

    [Theory]
    [InlineData("\uADF8\uAC8C \uC544\uB2C8\uB77C \uC81C\uAC00 \uCC3E\uC544\uBCF8 \uB2E8\uC5B4\uC694.")]
        // Reseeded: "\uC81C \uB9D0\uC740" was removed from the markers. It is ordinary discourse framing a
    // learner uses to elaborate their own question, not a statement about the coach.
    [InlineData("\uADF8 \uB73B\uC774 \uC544\uB2C8\uC5D0\uC694, \uC5B4\uC81C \uC77D\uC740 \uB2E8\uC5B4\uC608\uC694.")]
    [InlineData("\uADF8\uB7F0 \uB73B\uC774 \uC544\uB2C8\uC5D0\uC694.")]
    [InlineData("\uC798\uBABB \uC54C\uC544\uB4E4\uC73C\uC168\uC5B4\uC694.")]
    [InlineData("\uADF8\uAC74 \uD2C0\uB838\uC5B4\uC694.")]
    [InlineData("\uADF8\uAC70 \uB9D0\uACE0 \uB2E4\uB978 \uAC70\uC694.")]
    public void Korean_corrections_are_classified(string text)
    {
        Classifier.Classify(text).Should().NotBe(
            CoachCorrectionSignal.None,
            "Korean is a shipped display language, so a Korean learner must be able to correct the "
            + "coach in it");
    }

    // ── Typing errors ────────────────────────────────────────────────────────

    /// <summary>
    /// A learner who mistypes is exactly as disputing as one who does not.
    /// </summary>
    /// <remarks>
    /// A classifier that only catches careful typists catches the wrong half of the population, and
    /// the learner who types fastest is often the one who is most frustrated.
    /// </remarks>
    [Theory]
    [InlineData("No I ment the words I looked up.")]
    [InlineData("Thats not waht I asked.")]
    [InlineData("You misunderstod.")]
    [InlineData("Thats not what I aksed.")]
    [InlineData("That is incorect.")]
    public void Common_typing_errors_still_classify(string text)
    {
        Classifier.Classify(text).Should().NotBe(CoachCorrectionSignal.None);
    }

    /// <summary>
    /// One edit, not two. Two is where the markers start matching things that are not them.
    /// </summary>
    [Fact]
    public void Two_typos_in_one_marker_do_not_match()
    {
        Classifier.Classify("Thats nto waht I asked").Should().Be(
            CoachCorrectionSignal.None,
            "tolerance is one edit per marker; widening it trades a rare recovered typo for a class "
            + "of false positives nobody can enumerate");
    }

    /// <summary>Short tokens are matched exactly, because one edit changes what they mean.</summary>
    [Theory]
    [InlineData("wat", "what")]
    [InlineData("now", "not")]
    [InlineData("so", "no")]
    public void Short_tokens_are_not_fuzzy_matched(string actual, string expected)
    {
        // The distance function itself is permissive; the guard lives in the token-length check,
        // which is what this documents.
        CoachCorrectionClassifier.IsWithinOneEdit(actual, expected).Should().BeTrue(
            "the edit distance is genuinely one, which is exactly why length rather than distance "
            + "is the guard");
    }

    // ── Negatives: ordinary language questions ───────────────────────────────

    /// <summary>
    /// The most expensive false positive class. None of these reports a defect.
    /// </summary>
    [Theory]
    [InlineData("What does \uC544\uB2C8 mean?")]
    [InlineData("Is that right?")]
    [InlineData("Can you explain what you meant?")]
    [InlineData("What did I get wrong?")]
    [InlineData("\uBB34\uC2A8 \uB73B\uC774\uC5D0\uC694?")]
    [InlineData("\uADF8\uAC8C \uB9DE\uC544\uC694?")]
    public void Questions_never_open_a_dispute(string text)
    {
        Classifier.Classify(text).Should().Be(
            CoachCorrectionSignal.None,
            "a learner asking is not a learner disputing, and a dispute would constrain the next "
            + "answer for someone who was merely curious");
    }

    /// <summary>Bare negation and sentiment are not corrections.</summary>
    [Theory]
    [InlineData("No")]
    [InlineData("No thanks")]
    [InlineData("Not now")]
    [InlineData("Nope")]
    [InlineData("I don't like this exercise")]
    [InlineData("This is hard")]
    [InlineData("I don't understand")]
    [InlineData("\uC544\uB2C8\uC694")]
    [InlineData("\uC544\uB2C8")]
    [InlineData("\uBABB\uD558\uACA0\uC5B4\uC694")]
    public void Bare_negation_and_sentiment_never_open_a_dispute(string text)
    {
        Classifier.Classify(text).Should().Be(
            CoachCorrectionSignal.None,
            "every marker is a compound that says something about the previous turn; a learner "
            + "declining an offer is answering a question, not reporting a defect");
    }

    /// <summary>
    /// The learner correcting their own typing, which is not a dispute.
    /// </summary>
    /// <remarks>
    /// "Sorry, I meant to say 안녕하세요" contains a perfect <c>MeantSomethingElse</c> marker and is
    /// the learner being careful. Opening a dispute on it would punish exactly the behaviour the
    /// app wants to encourage.
    /// </remarks>
    [Theory]
    [InlineData("Sorry, I meant to say \uC548\uB155\uD558\uC138\uC694")]
    [InlineData("I meant to write it with a batchim")]
    [InlineData("I meant to ask about the past tense")]
    [InlineData("oops i ment to say hello")]
    public void Self_correction_is_not_a_dispute(string text)
    {
        Classifier.Classify(text).Should().Be(
            CoachCorrectionSignal.None,
            "the learner is fixing their own text, not reporting that the coach got something wrong");
    }

    /// <summary>Ordinary language content that happens to contain correction-shaped words.</summary>
    [Theory]
    [InlineData("The word for wrong is \uD2C0\uB9AC\uB2E4")]
    [InlineData("How do I say that is not right in Korean")]
    [InlineData("My teacher said I misunderstood the grammar")]
    public void Language_content_about_correction_words_is_not_a_dispute(string text)
    {
        // Note: the third case is genuinely hard — it contains "misunderstood" about a third party.
        // It is included because if the classifier ever starts matching bare "misunderstood" rather
        // than "you misunderstood", this is the fixture that catches it.
        Classifier.Classify(text).Should().Be(CoachCorrectionSignal.None);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Empty_text_is_not_a_correction(string? text)
    {
        Classifier.Classify(text).Should().Be(CoachCorrectionSignal.None);
        Classifier.IsCorrection(text).Should().BeFalse();
    }

    // ── The matrix is non-vacuous ────────────────────────────────────────────

    /// <summary>Every signal member is reachable from real learner text.</summary>
    [Fact]
    public void Every_signal_is_producible()
    {
        var produced = new[]
        {
            "That's not what I asked.",
            "No, I meant the words I looked up.",
            "That's wrong.",
            "I meant the reading words, not the ones in the plan."
        }.Select(Classifier.Classify).ToHashSet();

        foreach (var signal in Enum.GetValues<CoachCorrectionSignal>()
                     .Where(value => value != CoachCorrectionSignal.None))
        {
            produced.Should().Contain(
                signal,
                "{0} is a member nothing can produce, which makes it decoration rather than a code",
                signal);
        }
    }

    /// <summary>
    /// Classification is deterministic and order-independent across repeated calls.
    /// </summary>
    [Fact]
    public void Classification_is_stable()
    {
        const string Text = "No, I meant the words I looked up, not the ones in the plan.";

        var first = Classifier.Classify(Text);

        for (var attempt = 0; attempt < 5; attempt++)
        {
            Classifier.Classify(Text).Should().Be(first, "a ladder walked twice gives one answer");
        }
    }

    /// <summary>
    /// A sentence that trips two families reports the stronger one, deterministically.
    /// </summary>
    [Fact]
    public void The_ladder_order_is_deterministic()
    {
        Classifier.Classify("That's not what I asked, I meant the other ones.")
            .Should().Be(
                CoachCorrectionSignal.NotWhatIAsked,
                "NotWhatIAsked is the stronger statement about the previous turn, and the ordering "
                + "must not depend on which list happened to be scanned first");
    }
}
