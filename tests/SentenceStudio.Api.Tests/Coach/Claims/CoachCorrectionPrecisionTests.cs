using FluentAssertions;
using SentenceStudio.Api.Coach.Application;
using SentenceStudio.Api.Coach.Persistence.History;
using SentenceStudio.Api.Coach.Tools;
using SentenceStudio.Api.Coach.Tools.Observation;
using SentenceStudio.Contracts.Coach;
using SentenceStudio.Api.Coach.Validation.Claims;
using Xunit;

namespace SentenceStudio.Api.Tests.Coach.Claims;

/// <summary>
/// The language review's precision fixtures: what must not open a dispute, and what must not close one.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why precision is the whole game here.</b> A dispute is a constraint on the next answer. A
/// false positive takes a learner who was practising grammar or asking a translation question and
/// silently narrows what the coach may say to them next; a false close releases a constraint the
/// learner earned by pushing back. Both are invisible to the learner and both make the coach worse
/// at exactly the moment it is being watched.
/// </para>
/// <para>
/// <b>The Korean cases are not exotic.</b> <c>N이/가 아니라 B</c> is one of the first contrastive
/// patterns a Korean learner meets, and it was in the cohort marker list. Every Korean learner
/// practising it was opening a dispute against a coach that had done nothing wrong.
/// </para>
/// </remarks>
public sealed class CoachCorrectionPrecisionTests
{
    private static readonly CoachCorrectionClassifier Classifier = new();

    private static readonly DateTime Now = new(2026, 8, 22, 3, 0, 0, DateTimeKind.Utc);

    // ─────────────────────────────────────────────────────────────────────────
    // False positives: twenty-five messages that must open nothing.
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>Core Korean grammar the learner came to practise.</summary>
    /// <remarks>
    /// <c>N이/가 아니라 B</c> — "not A but B". Removing it from the cohort markers is the single
    /// highest-value precision fix in this set, because it fires on the target language itself.
    /// </remarks>
    [Theory]
    // 이건 사과가 아니라 배예요. — "This isn't an apple, it's a pear."
    [InlineData("\uC774\uAC74 \uC0AC\uACFC\uAC00 \uC544\uB2C8\uB77C \uBC30\uC608\uC694.")]
    // 저는 학생이 아니라 선생님이에요. — "I'm not a student, I'm a teacher."
    [InlineData("\uC800\uB294 \uD559\uC0DD\uC774 \uC544\uB2C8\uB77C \uC120\uC0DD\uB2D8\uC774\uC5D0\uC694.")]
    // 이게 맞지 않아요? — "Isn't this right?" A question about the learner's own sentence.
    [InlineData("\uC774\uAC8C \uB9DE\uC9C0 \uC54A\uC544\uC694?")]
    // 제가 쓴 게 틀린 것 같아요. — "I think what I wrote is wrong."
    [InlineData("\uC81C\uAC00 \uC4F4 \uAC8C \uD2C0\uB9B0 \uAC83 \uAC19\uC544\uC694.")]
    // 제 발음이 틀렸어요? — "Is my pronunciation wrong?"
    [InlineData("\uC81C \uBC1C\uC74C\uC774 \uD2C0\uB838\uC5B4\uC694?")]
    // 제 답이 맞나요? — "Is my answer right?"
    [InlineData("\uC81C \uBC1C\uC74C \uADF8\uAC74 \uD2C0\uB838\uC5B4\uC694")]
    // 제 말은, 이게 더 자연스러운가요? — "What I mean is, is this more natural?"
    [InlineData("\uC81C \uB9D0\uC740, \uC774\uAC8C \uB354 \uC790\uC5F0\uC2A4\uB7EC\uC6B4\uAC00\uC694?")]
    public void Korean_that_is_grammar_or_self_assessment_opens_nothing(string text)
    {
        Classifier.Classify(text).Should().Be(
            CoachCorrectionSignal.None,
            "a learner practising the target language, or asking about their own attempt, has "
            + "corrected nobody");
    }

    /// <summary>Translation and how-do-I-say questions, with and without a leading discourse word.</summary>
    [Theory]
    [InlineData("How do I say that is not right in Korean")]
    [InlineData("So how do I say that is not right in Korean")]
    [InlineData("Also, how would I say you are wrong politely")]
    [InlineData("Please can you tell me how to say that's incorrect")]
    [InlineData("Hmm what does it mean when someone says that's not true")]
    [InlineData("Ok so is my answer wrong")]
    public void A_question_behind_a_discourse_marker_is_still_a_question(string text)
    {
        Classifier.Classify(text).Should().Be(
            CoachCorrectionSignal.None,
            "one filler word was the whole difference between a question and a complaint, and the "
            + "gate read only the first token");
    }

    /// <summary>
    /// Teaching and translation requests that quote a correction phrase as the thing to be taught.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The whole class the interrogative gate could not reach.</b> "How do I say that is not
    /// right" opens with an interrogative and was already safe. "Tell me how to say that is not
    /// right" is the same request in the imperative, and the imperative has no question word at the
    /// front for the gate to find — so every one of these was opening a <c>WrongClaim</c> dispute
    /// against a learner asking for a translation, and then constraining the very answer that would
    /// have taught them the phrase.
    /// </para>
    /// <para>
    /// The material being requested is <em>correction language</em>, which is ordinary vocabulary
    /// in a tutoring app: a learner has to be able to ask how to disagree in Korean without the
    /// asking being read as disagreement.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData("Please tell me how to say that is not right in Korean")]
    [InlineData("Tell me how to say that is wrong in Korean")]
    [InlineData("Show me how to say you are wrong politely")]
    [InlineData("Teach me how to say that is not true")]
    [InlineData("Explain how to say that is incorrect in Korean")]
    [InlineData("Help me say that is not right in Korean")]
    [InlineData("Give me a sentence using that is wrong")]
    [InlineData("Translate that is not true into Korean")]
    [InlineData("Let me know how to say you are wrong")]
    public void An_imperative_teaching_request_opens_nothing(string text)
    {
        Classifier.Classify(text).Should().Be(
            CoachCorrectionSignal.None,
            "the correction phrase is the material the learner asked about, not a report that the "
            + "coach was defective; opening a dispute here suppresses the lesson that was requested");
    }

    /// <summary>
    /// The control for the request-frame gate: a request verb is not a licence to ignore a dispute.
    /// </summary>
    /// <remarks>
    /// The gate must read the <em>frame</em>, not the verb. "Tell me" in front of a challenge to
    /// the coach's own count is still a challenge, and a gate that blanket-ignored every
    /// tell/show/explain message would silently delete the most direct way a learner has of pushing
    /// back.
    /// </remarks>
    [Theory]
    [InlineData("Tell me why you said 12 words, that is wrong", CoachCorrectionSignal.WrongClaim)]
    [InlineData("Explain yourself, that is not what I asked", CoachCorrectionSignal.NotWhatIAsked)]
    [InlineData("Show me the reading words, not the ones in the plan", CoachCorrectionSignal.DifferentCohort)]
    [InlineData("Tell me again, you are wrong about the count", CoachCorrectionSignal.WrongClaim)]
    public void A_request_verb_in_front_of_a_real_challenge_still_opens(
        string text,
        CoachCorrectionSignal expected)
    {
        Classifier.Classify(text).Should().Be(
            expected,
            "there is no grammatical or translation frame here — the learner is disputing the "
            + "previous answer and happened to begin with an imperative");
    }

    /// <summary>The learner correcting their own typing or their own attempt.</summary>
    [Theory]
    // 안녕하세요 — the bare apology form, with no infinitive for the old marker to catch.
    [InlineData("Sorry, I meant \uC548\uB155\uD558\uC138\uC694")]
    [InlineData("My pronunciation of that is not right")]
    [InlineData("That is not right, my answer I mean")]
    public void A_learner_correcting_themselves_opens_nothing(string text)
    {
        Classifier.Classify(text).Should().Be(
            CoachCorrectionSignal.None,
            "punishing a learner for being careful about their own writing is the opposite of what "
            + "a correction signal is for");
    }

    /// <summary>
    /// A first-person possessive is not by itself a reason to discard a correction.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>S14 is written in the first person.</b> "I meant <em>my</em> words from the lookup list"
    /// is the scenario this workstream exists for, and a whole-message exclusion on "my words"
    /// discarded it — the learner naming their own material is how they say <em>which</em> material
    /// the coach used the wrong one of.
    /// </para>
    /// <para>
    /// The suppression that replaces it is subject-specific: it fires only when the learner's own
    /// answer, pronunciation, word or sentence is the <em>subject</em> of a wrongness predicate
    /// ("my answer is wrong"), which is self-assessment. "My answer had 40 words" is evidence for a
    /// dispute, not an admission, and the difference is the verb.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData("No I meant my words from the lookup list, not the ones in the plan",
        CoachCorrectionSignal.DifferentCohort)]
    [InlineData("That's not what I asked, I asked about my sentence",
        CoachCorrectionSignal.NotWhatIAsked)]
    // 제 문장 말고 제가 찾아본 단어요. 그게 아니라요 — "Not my sentence, the words I looked up. That's not it."
    [InlineData("\uC81C \uBB38\uC7A5 \uB9D0\uACE0 \uC81C\uAC00 \uCC3E\uC544\uBCF8 \uB2E8\uC5B4\uC694. \uADF8\uAC8C \uC544\uB2C8\uB77C\uC694",
        CoachCorrectionSignal.MeantSomethingElse)]
    [InlineData("That is wrong, my answer had 40 words", CoachCorrectionSignal.WrongClaim)]
    public void A_correction_that_names_the_learners_own_material_still_opens(
        string text,
        CoachCorrectionSignal expected)
    {
        Classifier.Classify(text).Should().Be(
            expected,
            "S14 names the learner's own material by construction; a whole-message exclusion on "
            + "first-person possessives threw away the correction the workstream was built for");
    }

    /// <summary>A fresh request that names a cohort without contradicting anything.</summary>
    [Theory]
    [InlineData("Let's do the reading words instead of the plan ones today")]
    [InlineData("Not those, the ones on page two please")]
    public void A_fresh_cohort_request_opens_nothing(string text)
    {
        Classifier.Classify(text).Should().Be(
            CoachCorrectionSignal.None,
            "choosing what to study next is not a report that the last answer was defective");
    }

    [Fact]
    public void The_false_positive_corpus_is_the_size_the_review_specified()
    {
        // Non-vacuity, and a guard against a future edit quietly deleting a case rather than fixing
        // the classifier. Twenty-five messages, every one of which must classify as None.
        var corpus = FalsePositiveCorpus();

        corpus.Should().HaveCount(25);
        corpus.Should().OnlyContain(text => Classifier.Classify(text) == CoachCorrectionSignal.None);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // True positives: the eight the review preserved, plus the typo cases.
    // ─────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("That's not what I asked.", CoachCorrectionSignal.NotWhatIAsked)]
    [InlineData("You misunderstood my question.", CoachCorrectionSignal.NotWhatIAsked)]
    [InlineData("That's wrong.", CoachCorrectionSignal.WrongClaim)]
    [InlineData("You are wrong about that.", CoachCorrectionSignal.WrongClaim)]
    [InlineData("No, I meant the words I looked up.", CoachCorrectionSignal.MeantSomethingElse)]
    [InlineData("What I meant was the reading list.", CoachCorrectionSignal.MeantSomethingElse)]
    // DifferentCohort, not MeantSomethingElse: the ladder is most-specific-first, and a message
    // that names both the thing the learner wanted and the thing they got is the cohort shape.
    [InlineData("I meant the reading words, not the ones in the plan.", CoachCorrectionSignal.DifferentCohort)]
    // 그거 말고 다른 거요. — "Not that one, a different one." An anchored redirect.
    [InlineData("\uADF8\uAC70 \uB9D0\uACE0 \uB2E4\uB978 \uAC70\uC694.", CoachCorrectionSignal.DifferentCohort)]
    public void The_preserved_true_positives_still_classify(string text, CoachCorrectionSignal expected)
    {
        Classifier.Classify(text).Should().Be(
            expected,
            "narrowing the markers must not cost the corrections that were the point of having them");
    }

    [Theory]
    [InlineData("No I ment the words I looked up.")]
    [InlineData("Thats not what i aksed")]
    public void Typing_errors_still_classify(string text)
    {
        Classifier.Classify(text).Should().NotBe(
            CoachCorrectionSignal.None,
            "a classifier that only catches careful typists catches the wrong half of the population");
    }

    [Fact]
    public void Every_signal_is_still_producible_after_the_narrowing()
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
            produced.Should().Contain(signal, "{0} must still be reachable", signal);
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // False closes: eight answers that must leave the dispute standing.
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>The one that motivated the narrowing.</summary>
    /// <remarks>
    /// "I counted 12 again" contains "I counted", names no error, and repeats the disputed number.
    /// It was closing the dispute that existed to stop it.
    /// </remarks>
    [Theory]
    [InlineData("I counted 12 again.")]
    [InlineData("I said 12 and that is what the data shows.")]
    [InlineData("I listed the same words as before.")]
    [InlineData("Correction: the list is below.")]
    [InlineData("Sorry about that. Here is the list again.")]
    public void A_restatement_or_a_label_does_not_close_a_dispute(string sentence)
    {
        ClassifyExit(sentence).Should().Be(
            CoachDisputeExit.None,
            "a coach repeating itself with an apology attached has not accepted the correction, and "
            + "releasing the constraint there is how the learner gets the same wrong answer twice");
    }

    /// <summary>
    /// Sam grading the learner's Korean is not Sam admitting Sam was wrong.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>"That was wrong" has two subjects and the bare phrase names neither.</b> In a language
    /// tutor the overwhelmingly common referent is the learner's last utterance — every correction
    /// Sam gives is some form of "that was wrong, here is the right form". Treating the bare phrase
    /// as an admission meant the act of teaching discharged the dispute the learner had opened, and
    /// it discharged it on the turn most likely to still be repeating the disputed claim.
    /// </para>
    /// <para>
    /// The narrowed rule requires a first-person anchor to the coach's <em>own</em> prior claim.
    /// "I was wrong", "my earlier answer was wrong", and "I said twelve earlier, and that was not
    /// right" all keep it; grading the learner does not.
    /// </para>
    /// </remarks>
    [Theory]
    // 을 / 를 — the object particle pair; Sam is correcting the learner's choice between them.
    [InlineData("That was wrong \u2014 the particle should be \uC744, not \uB97C.")]
    // 갔어요 — "went", the form Sam is steering the learner toward.
    [InlineData("That was not right; try \uAC14\uC5B4\uC694 instead.")]
    [InlineData("Your sentence: that was incorrect. Here is the fix.")]
    public void Grading_the_learners_answer_does_not_close_a_dispute(string sentence)
    {
        ClassifyExit(sentence).Should().Be(
            CoachDisputeExit.None,
            "the subject of \"that was wrong\" here is the learner's Korean, not the coach's earlier "
            + "claim; a tutor cannot be allowed to clear a dispute by doing its job");
    }

    [Fact]
    public void The_false_close_corpus_is_the_size_the_review_specified()
    {
        var corpus = new[]
        {
            "I counted 12 again.",
            "I said 12 and that is what the data shows.",
            "I listed the same words as before.",
            "Correction: the list is below.",
            "Sorry about that. Here is the list again.",
            "That was wrong \u2014 the particle should be \uC744, not \uB97C.",
            "That was not right; try \uAC14\uC5B4\uC694 instead.",
            "Your sentence: that was incorrect. Here is the fix."
        };

        corpus.Should().HaveCount(8);
        corpus.Should().OnlyContain(sentence => ClassifyExit(sentence) == CoachDisputeExit.None);
    }

    /// <summary>An explicit admission still closes it.</summary>
    [Theory]
    [InlineData("I was wrong about that count.")]
    [InlineData("I got that wrong earlier.")]
    [InlineData("I miscounted; here is the right number.")]
    [InlineData("I misread your list.")]
    [InlineData("My earlier answer was wrong.")]
    [InlineData("My previous count was incorrect.")]
    [InlineData("What I said before was wrong.")]
    // The anaphoric form survives only when it is anchored to the coach's own prior claim.
    [InlineData("I said twelve earlier, and that was not right.")]
    // 제가 잘못 봤어요. — "I read it wrong."
    [InlineData("\uC81C\uAC00 \uC798\uBABB \uBD24\uC5B4\uC694.")]
    public void An_explicit_admission_closes_the_dispute(string sentence)
    {
        ClassifyExit(sentence).Should().Be(
            CoachDisputeExit.NamedCorrection,
            "accepting the correction out loud is what the learner pushed back to get");
    }

    [Fact]
    public void The_exit_is_typed_and_never_inferred_from_prose_alone()
    {
        // A re-read exits on the trace, not on a sentence claiming one happened. This answer says
        // it looked somewhere new and the trace says otherwise; the typed fact wins.
        var context = Context("I looked at a different list this time.", DisputedTrace());

        CoachRepeatedDisputedClaimRule.ClassifyExit(context).Should().Be(
            CoachDisputeExit.None,
            "prose asserting a re-read is exactly the fabricated check the grounding layer exists "
            + "to catch; the exit reads the trace");
    }

    [Fact]
    public void A_materially_different_read_exits_on_the_trace_with_no_prose_at_all()
    {
        var context = Context(
            "Here is what I found.",
            TraceWith(CoachScopeDefinition.UndueVocabularySearch));

        CoachRepeatedDisputedClaimRule.ClassifyExit(context).Should().Be(
            CoachDisputeExit.ReRead,
            "the typed facts changed, which is the deterministic exit; the sentence is irrelevant");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // The limitation exit: typed, relevant, and closed. Never prose.
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A typed limitation whose code says the coach cannot produce the disputed claim clears it.
    /// </summary>
    /// <remarks>
    /// This is the honest close: the learner said the answer was wrong, and the coach's reply is
    /// not another answer but a bounded statement that this answer is not something it can give.
    /// The dispute has nowhere left to go, so holding the constraint open would only suppress the
    /// limitation itself on every following turn.
    /// </remarks>
    [Theory]
    [InlineData(CoachLimitationCode.NotBuilt)]
    [InlineData(CoachLimitationCode.AvailableOnAnotherSurface)]
    [InlineData(CoachLimitationCode.RefusedByDesign)]
    public void A_relevant_typed_limitation_closes_the_dispute(CoachLimitationCode code)
    {
        var context = Context("Here is what I can tell you.", DisputedTrace(), Limitation(code));

        CoachRepeatedDisputedClaimRule.ClassifyExit(context).Should().Be(
            CoachDisputeExit.Limitation,
            "{0} is a capability boundary on the disputed claim itself; the coach cannot answer the "
            + "correction because the answer is not something this build produces",
            code);
    }

    /// <summary>
    /// A limitation about a different request does not discharge this dispute.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A turn can carry a limitation and a disputed claim at the same time without the one being
    /// about the other. "I will not delete all 412 of your words" is a refusal of a bulk change; it
    /// says nothing about whether the count the learner disputed was right, and letting it close
    /// the dispute would hand the coach an unrelated escape hatch on any turn where the learner
    /// asked for two things.
    /// </para>
    /// <para>
    /// <see cref="CoachLimitationCode.Unknown"/> is excluded for the same reason it is excluded
    /// everywhere else: it is the documented unset value, and treating unset as sufficient is how a
    /// missing code becomes a close.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData(CoachLimitationCode.WouldRemoveLearningValue)]
    [InlineData(CoachLimitationCode.ExceedsSafeChangeScope)]
    [InlineData(CoachLimitationCode.Unknown)]
    public void An_unrelated_or_unset_typed_limitation_leaves_the_dispute_open(CoachLimitationCode code)
    {
        var context = Context("Here is what I can tell you.", DisputedTrace(), Limitation(code));

        CoachRepeatedDisputedClaimRule.ClassifyExit(context).Should().Be(
            CoachDisputeExit.None,
            "{0} refuses a different request; the disputed claim is untouched by it",
            code);
    }

    [Fact]
    public void No_limitation_at_all_leaves_the_dispute_open()
    {
        var context = Context("Here is what I can tell you.", DisputedTrace());

        CoachRepeatedDisputedClaimRule.ClassifyExit(context).Should().Be(
            CoachDisputeExit.None,
            "the absence of a limitation is the ordinary case and must not be read as one");
    }

    /// <summary>
    /// Prose is never a limitation, no matter how limitation-shaped it reads.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the same rule the re-read exit already lives under, applied to the last exit that
    /// was still reading sentences. A phrase list matching "I can't tell you that" could be
    /// produced by a model that had not consulted anything, had not declared a limitation, and was
    /// simply declining — which is precisely the answer a standing dispute exists to constrain.
    /// </para>
    /// <para>
    /// The typed limitation is produced by the projection from the turn's own findings, so it
    /// cannot be written by the answer text. That is the whole point of moving the exit onto it.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData("I can't tell you that from what I looked at.")]
    [InlineData("I only looked at part of your data.")]
    [InlineData("I didn't check that.")]
    [InlineData("I'm not sure, I can't do that.")]
    public void Prose_that_sounds_like_a_limitation_never_closes_a_dispute(string sentence)
    {
        ClassifyExit(sentence).Should().Be(
            CoachDisputeExit.None,
            "an unverified sentence claiming a boundary is exactly the fabrication the grounding "
            + "layer exists to catch; the exit reads the typed limitation or nothing");
    }

    // ─────────────────────────────────────────────────────────────────────────

    private static CoachLimitationDto Limitation(CoachLimitationCode code) =>
        new()
        {
            Code = code,
            Coverage = CoachEvidenceCoverage.CompleteOwnedSet
        };

    /// <summary>
    /// Every message the review ruled must open nothing, in one place.
    /// </summary>
    /// <remarks>
    /// The theories above assert the interesting cases individually so a failure names the shape it
    /// broke. This corpus exists so a future edit cannot quietly shrink the set: the count assertion
    /// fails before the behaviour assertion does.
    /// </remarks>
    private static string[] FalsePositiveCorpus() =>
    [
        "\uC774\uAC74 \uC0AC\uACFC\uAC00 \uC544\uB2C8\uB77C \uBC30\uC608\uC694.",
        "\uC800\uB294 \uD559\uC0DD\uC774 \uC544\uB2C8\uB77C \uC120\uC0DD\uB2D8\uC774\uC5D0\uC694.",
        "\uC774\uAC8C \uB9DE\uC9C0 \uC54A\uC544\uC694?",
        "\uC81C\uAC00 \uC4F4 \uAC8C \uD2C0\uB9B0 \uAC83 \uAC19\uC544\uC694.",
        "\uC81C \uBC1C\uC74C\uC774 \uD2C0\uB838\uC5B4\uC694?",
        "\uC81C \uBC1C\uC74C \uADF8\uAC74 \uD2C0\uB838\uC5B4\uC694",
        "\uC81C \uB9D0\uC740, \uC774\uAC8C \uB354 \uC790\uC5F0\uC2A4\uB7EC\uC6B4\uAC00\uC694?",
        "How do I say that is not right in Korean",
        "So how do I say that is not right in Korean",
        "Also, how would I say you are wrong politely",
        "Please can you tell me how to say that's incorrect",
        "Please tell me how to say that is not right in Korean",
        "Tell me how to say that is wrong in Korean",
        "Show me how to say you are wrong politely",
        "Teach me how to say that is not true",
        "Explain how to say that is incorrect in Korean",
        "Help me say that is not right in Korean",
        "Give me a sentence using that is wrong",
        "Translate that is not true into Korean",
        "Let me know how to say you are wrong",
        "Hmm what does it mean when someone says that's not true",
        "Ok so is my answer wrong",
        "Sorry, I meant \uC548\uB155\uD558\uC138\uC694",
        "My pronunciation of that is not right",
        "Let's do the reading words instead of the plan ones today"
    ];

    private static CoachDisputeExit ClassifyExit(string sentence) =>
        CoachRepeatedDisputedClaimRule.ClassifyExit(Context(sentence, DisputedTrace()));

    private static CoachClaimRuleContext Context(string sentence, CoachTurnTraceSummary trace) =>
        Context(sentence, trace, limitation: null);

    private static CoachClaimRuleContext Context(
        string sentence,
        CoachTurnTraceSummary trace,
        CoachLimitationDto? limitation) =>
        new()
        {
            Answer = ClaimFixture.Answer(sentence),
            Evidence = [ClaimFixture.Evidence(CoachEvidenceCoverage.CompleteOwnedSet)],
            Trace = trace,
            Limitation = limitation,
            Dispute = new CoachTurnDisputeState(
                CoachCorrectionSignal.WrongClaim,
                "msg-coach-1",
                Now,
                ResolvedAtUtc: null,
                CoachDisputeResolution.Open,
                [CoachScopeDefinition.TrackedVocabularyDueSummary])
        };

    private static CoachTurnTraceSummary DisputedTrace() =>
        TraceWith(CoachScopeDefinition.TrackedVocabularyDueSummary);

    /// <summary>A successful read of one definition, matching the lifecycle fixtures.</summary>
    private static CoachTurnTraceSummary TraceWith(CoachScopeDefinition definition) =>
        new(
            [
                new CoachTurnTraceEntry(
                    Ordinal: 1,
                    ToolName: "read",
                    Outcome: CoachToolCallOutcome.Succeeded,
                    FailureKind: null,
                    ArgumentMask: CoachToolArgumentMask.None,
                    ElapsedMs: 9,
                    Coverage: CoachScopeCoverage.CompleteOwnedSet,
                    DefinitionCode: definition,
                    WithheldReason: CoachScopeWithheldReason.None,
                    MatchedCount: 12,
                    ReturnedCount: 12,
                    WithheldCount: null,
                    Truncated: false)
            ],
            BudgetUsed: 1,
            BudgetLimit: 6);
}
