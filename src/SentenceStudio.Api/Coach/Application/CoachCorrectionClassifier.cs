using System.Globalization;
using System.Text;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace SentenceStudio.Api.Coach.Application;

/// <summary>
/// What kind of correction the learner made, if any.
/// </summary>
/// <remarks>
/// Closed, because a dispute is a state the next turn is judged against and a state needs a code a
/// metric can count and a test can name. The four members are the four ways a learner tells the
/// coach it got the previous turn wrong.
/// </remarks>
/// <remarks>
/// String-serialized because this value is <b>persisted</b> in the protected turn outcome and read
/// back by a later turn — possibly by a later build. An ordinal is coupled to declaration order, so
/// inserting a member would silently reinterpret every stored dispute as a different correction.
/// </remarks>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum CoachCorrectionSignal
{
    /// <summary>Not a correction. The overwhelming majority of turns.</summary>
    None = 0,

    /// <summary>"No, I meant…" — the learner restates what they were asking for.</summary>
    MeantSomethingElse = 1,

    /// <summary>"That's not what I asked" — the learner rejects the reading of their question.</summary>
    NotWhatIAsked = 2,

    /// <summary>"That's wrong" — the learner rejects the content of the answer.</summary>
    WrongClaim = 3,

    /// <summary>
    /// "I meant the words I looked up, not the ones in the plan" — the learner names a different
    /// cohort or parameter than the one the answer used.
    /// </summary>
    DifferentCohort = 4
}

/// <summary>
/// Decides — deterministically, without the model — whether typed text corrects the previous turn.
/// </summary>
/// <remarks>
/// <para>
/// A sibling of <see cref="CoachExplicitAcceptanceClassifier"/> and built on the same rules: closed
/// phrase lists, English and Korean because those are the display languages that ship, and anything
/// the classifier has not been taught is <see cref="CoachCorrectionSignal.None"/>. Adding a language
/// means adding phrases, never loosening the matcher.
/// </para>
/// <para>
/// <b>One structural difference from its sibling, and it matters.</b> Acceptance is a short bare
/// message — the acceptance classifier can require the <em>whole</em> message to be decisive and
/// cap it at forty characters. A correction is the opposite shape: "No — I meant the words I looked
/// up, not the ones in the plan" is long, and the content after the marker is the entire point. So
/// this classifier matches a marker <em>inside</em> a longer message, which removes the safety net
/// its sibling relies on and puts the whole burden on how narrow the markers are.
/// </para>
/// <para>
/// <b>Hence: no bare negation, ever.</b> "No" opens no dispute. "No thanks" opens no dispute. Every
/// marker is a compound that says something about the previous turn — "no I meant", "that's not
/// what I asked", "그게 아니라". A learner saying no to an offer is answering a question; a learner
/// saying "that's not what I asked" is reporting a defect, and only the second is a dispute.
/// </para>
/// <para>
/// <b>The false positive that costs the most</b> is an ordinary language question. A dispute
/// suppresses the next answer's freedom to repeat itself, so opening one against a learner who was
/// merely curious degrades the next turn for no reason. Questions are excluded before any marker is
/// considered.
/// </para>
/// <para>
/// <b>Typing errors are in scope</b> because a learner mistyping "I ment" is exactly as disputing as
/// one who spells it correctly, and a classifier that only catches careful typists catches the
/// wrong half of the population. Tolerance is one edit per marker and only on tokens long enough
/// that an edit cannot turn one real word into another.
/// </para>
/// </remarks>
public sealed class CoachCorrectionClassifier
{
    /// <summary>
    /// Below this length a token is matched exactly.
    /// </summary>
    /// <remarks>
    /// One edit turns "not" into "now", "no" into "so", and "말" into a different word entirely.
    /// Fuzzy matching short tokens does not tolerate typos, it invents matches.
    /// </remarks>
    private const int MinimumFuzzyTokenLength = 4;

    /// <summary>
    /// Markers that reject the coach's reading of the question.
    /// </summary>
    /// <remarks>
    /// Every one of these is a statement <em>about the previous turn</em>. None of them is a
    /// negation, a sentiment, or a disagreement with a fact about the language.
    /// </remarks>
    private static readonly string[] NotWhatIAskedMarkers =
    [
        // English
        "thats not what i asked", "that is not what i asked", "not what i asked",
        "thats not what i said", "that is not what i said", "not what i said",
        "i didnt ask that", "i did not ask that", "i didnt ask for that",
        "i did not ask for that", "thats not my question", "that is not my question",
        "you misunderstood", "you misread", "you missed my point",
        "youre answering the wrong question", "you are answering the wrong question",
        // Korean
        "제가 물어본 건 그게 아니", "내가 물어본 건 그게 아니", "그걸 물어본 게 아니",
        "그거 물어본 거 아니", "잘못 알아들", "잘못 이해", "질문을 잘못",
        "제 질문은 그게 아니", "내 질문은 그게 아니"
    ];

    /// <summary>Markers that restate the intent.</summary>
    private static readonly string[] MeantSomethingElseMarkers =
    [
        // English
        "no i meant", "no i ment", "i meant", "what i meant was", "what i meant is",
        "thats not what i meant", "that is not what i meant", "not what i meant",
        "i was asking about", "i was asking for", "my point was",
        // Korean
        "그게 아니라", "그런 뜻이 아니", "제 말뜻은", "내 말뜻은",
        "그런 의미가 아니", "그 뜻이 아니"

        // "제 말은" and "내 말은" are removed. They are ordinary discourse framing — "제 말은,
        // 이게 더 자연스러운가요?" is a learner elaborating their own question, not disputing an
        // answer. The review named the polite form; the plain form is the same construction with a
        // different humility register, and keeping one while dropping the other would make the
        // classifier's behaviour depend on how formally the learner addresses the coach.
    ];

    /// <summary>Markers that reject the content of the answer.</summary>
    /// <remarks>
    /// Narrow on purpose. "Wrong" on its own is a word a learner uses about their own answer far
    /// more often than about the coach's, so it never appears here unaccompanied.
    /// </remarks>
    private static readonly string[] WrongClaimMarkers =
    [
        // English
        "thats wrong", "that is wrong", "thats incorrect", "that is incorrect",
        "thats not right", "that is not right", "thats not true", "that is not true",
        "youre wrong", "you are wrong", "you got that wrong", "that isnt right",
        // Korean. Anchored to a demonstrative, so the learner is naming what the coach said.
        //
        // "틀린 것 같" and "맞지 않아" are removed. Both are unanchored and both are what a learner
        // says about their <em>own</em> attempt — "제가 쓴 게 틀린 것 같아요", "이 문장이 맞지
        // 않아요?" — which is a question about their sentence, not a complaint about the answer.
        "그건 틀렸", "그거 틀렸", "사실이 아니", "맞지 않습니다"
    ];

    /// <summary>
    /// Markers that name a different cohort or parameter than the answer used.
    /// </summary>
    /// <remarks>
    /// The S14 shape: "I meant the words I looked up, <b>not the ones in the plan</b>". These need a
    /// contrast, because the distinguishing feature is that the learner named both the thing they
    /// wanted and the thing they got.
    /// </remarks>
    private static readonly string[] CohortContrastMarkers =
    [
        // English. Each names both halves of the contrast; a bare negation does not appear.
        //
        // Removed after the language review: "not those", "instead of the", "i said the". All three
        // are ordinary English a learner uses about their own material — "not those, the ones on
        // page two", "instead of the formal ending", "I said the wrong particle" — and none is a
        // statement about what the coach did.
        "not the ones", "not the ones in", "rather than the",
        "not from the", "the other ones",

        // Korean. Anchored redirects only: a demonstrative plus 말고 names the thing the coach used
        // and asks for a different one, which is the S14 shape.
        //
        // "가 아니라" and "이 아니라" are removed and must not come back. They are the core
        // N이/가 아니라 B construction — "이건 사과가 아니라 배예요", "저는 학생이 아니라
        // 선생님이에요" — which is one of the first contrastive patterns a Korean learner meets and
        // one of the most common things they will type at a tutor. Matching it opened a dispute on
        // a learner practising the grammar they came to practise, and then suppressed the next
        // answer's freedom to teach it.
        "그거 말고", "그게 말고", "그것 말고", "그 말고", "이거 말고", "저거 말고"
    ];

    /// <summary>
    /// Phrases that look like a correction and are not.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>"I meant to say" is the important one.</b> A learner writing "sorry, I meant to say
    /// 안녕하세요" is correcting <em>their own</em> typing, not disputing the coach. It contains a
    /// perfect <c>MeantSomethingElse</c> marker and opening a dispute on it would punish a learner
    /// for being careful.
    /// </para>
    /// <para>
    /// Checked before markers, so an exclusion always wins.
    /// </para>
    /// </remarks>
    private static readonly string[] SelfCorrectionMarkers =
    [
        "i meant to say", "i meant to write", "i meant to type", "i meant to ask",
        "i ment to say", "i ment to write",

        // Broadened from "sorry i meant to" to the bare apology form. "Sorry, I meant 안녕하세요"
        // has no infinitive after it and was falling straight through to the "i meant" marker —
        // the apology was doing all the work of signalling self-correction and none of it was
        // being read.
        "sorry i meant", "sorry i ment", "sorry wrong", "oops i meant",

        "제가 쓰려던 건", "제가 말하려던 건", "쓰려고 했던 건", "제가 잘못 썼",
        "제가 잘못 말했", "아 죄송", "죄송해요 제가"
    ];

    /// <summary>
    /// The learner's own work as the <em>subject</em> of a wrongness predicate.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why this is a shape and not a word list.</b> The predecessor was a list of first-person
    /// possessives — "my answer", "my sentence", "제 문장" — matched anywhere in the message, and it
    /// discarded S14 wholesale. "No I meant <em>my words</em> from the lookup list, not the ones in
    /// the plan" is the correction this workstream exists to catch, and naming your own material is
    /// how you say <em>which</em> material the coach used the wrong one of. A learner cannot
    /// disambiguate a cohort without a possessive.
    /// </para>
    /// <para>
    /// What actually needs suppressing is self-<em>assessment</em>: the learner's own answer,
    /// pronunciation, word or sentence standing as the subject of "is wrong" / "틀렸". "My answer is
    /// wrong" is a learner grading themselves; "my answer had 40 words" is a learner producing
    /// evidence. The difference is the predicate, so the predicate is what is matched.
    /// </para>
    /// <para>
    /// Three shapes, because self-assessment has three common word orders: predicative ("my
    /// pronunciation of that is not right"), appositive ("that is not right, my answer I mean"),
    /// and the Korean topic-comment form ("제 발음 그건 틀렸어요"). The intervening-token windows are
    /// deliberately short — three tokens in English, three eojeol in Korean — because a longer
    /// window reaches across a clause boundary and starts suppressing the disputes again.
    /// </para>
    /// </remarks>
    private const string SelfNouns =
        "answers?|anwsers?|pronunciations?|pronounciations?|words?|sentences?|spelling|writing"
        + "|translations?|attempts?|grammar";

    private const string Wrongness = "wrong|incorrect|off|bad|mistaken|not\\s+right|not\\s+correct|not\\s+true";

    private static readonly Regex LearnerOwnWorkIsTheSubject = new(
        // Predicative: "my answer is wrong", "my pronunciation of that is not right".
        $@"\bmy\s+(?:{SelfNouns})\b(?:\s+\w+){{0,3}}\s+(?:is|was|isnt|wasnt|arent|sounds|sounded"
        + $@"|looks|looked|seems|seemed)\s+(?:{Wrongness})\b"
        // Appositive: "that is not right, my answer I mean" / "I mean my answer".
        + $@"|\bmy\s+(?:{SelfNouns})\s+i\s+mean\b"
        + $@"|\bi\s+mean\s+my\s+(?:{SelfNouns})\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture);

    /// <summary>The Korean topic-comment form of the same self-assessment.</summary>
    /// <remarks>
    /// Korean is agglutinative, so the noun carries its particle in the same token — 발음 becomes
    /// 발음이 — and the wrongness verb carries its ending — 틀렸어요. Both are matched as prefixes
    /// inside a token rather than as whole words, which is the same accommodation the Korean marker
    /// lists already make.
    /// </remarks>
    private static readonly Regex LearnerOwnWorkIsTheSubjectKorean = new(
        @"(?:제|내)\s*(?:답|발음|문장|말|글|번역|철자)\S*(?:\s+\S+){0,2}\s+\S*(?:틀렸|틀린|틀려|맞지\s*않)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture);

    /// <summary>
    /// Signals in the order they are tested.
    /// </summary>
    /// <remarks>
    /// Most specific first. A message that is both "not what I asked" and a cohort re-specification
    /// is reported as the former, because that is the stronger statement about the previous turn
    /// and the ordering must be deterministic rather than dependent on dictionary iteration.
    /// </remarks>
    private static readonly (CoachCorrectionSignal Signal, string[] Markers)[] Ladder =
    [
        (CoachCorrectionSignal.NotWhatIAsked, NotWhatIAskedMarkers),
        (CoachCorrectionSignal.WrongClaim, WrongClaimMarkers),
        (CoachCorrectionSignal.DifferentCohort, CohortContrastMarkers),
        (CoachCorrectionSignal.MeantSomethingElse, MeantSomethingElseMarkers)
    ];

    /// <summary>Classifies typed learner text as a correction of the previous turn, or not.</summary>
    public CoachCorrectionSignal Classify(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return CoachCorrectionSignal.None;
        }

        // A question is a question. "What did you mean?" and "그게 무슨 뜻이에요?" both contain
        // correction-shaped words and neither reports a defect — the learner is asking, not
        // disputing, and a dispute would constrain the next answer for no reason.
        if (IsQuestionOrQuotedMaterial(text))
        {
            return CoachCorrectionSignal.None;
        }

        var normalized = Normalize(text);

        if (normalized.Length == 0)
        {
            return CoachCorrectionSignal.None;
        }

        if (OpensWithAnInterrogative(normalized))
        {
            return CoachCorrectionSignal.None;
        }

        // The imperative twin of the interrogative gate. "Tell me how to say that is not right" is
        // the same request as "How do I say that is not right" with the question word removed.
        if (IsATeachingOrTranslationRequest(normalized))
        {
            return CoachCorrectionSignal.None;
        }

        // Exclusions before markers, always. A learner fixing their own typing, or grading their
        // own answer, is not disputing anything the coach said.
        foreach (var exclusion in SelfCorrectionMarkers)
        {
            if (ContainsMarker(normalized, Normalize(exclusion)))
            {
                return CoachCorrectionSignal.None;
            }
        }

        if (LearnerOwnWorkIsTheSubject.IsMatch(normalized)
            || LearnerOwnWorkIsTheSubjectKorean.IsMatch(normalized))
        {
            return CoachCorrectionSignal.None;
        }

        foreach (var (signal, markers) in Ladder)
        {
            foreach (var marker in markers)
            {
                if (ContainsMarker(normalized, Normalize(marker)))
                {
                    return signal;
                }
            }
        }

        return CoachCorrectionSignal.None;
    }

    /// <summary>
    /// Interrogatives that make a whole sentence a question when they lead it.
    /// </summary>
    /// <remarks>
    /// Some correction markers begin with one of these — "what I meant was" is the obvious one — so
    /// the gate below cannot be a bare first-token test. It defers to a marker that starts the
    /// message.
    /// </remarks>
    private static readonly string[] LeadingInterrogatives =
    [
        "what", "whats", "how", "why", "when", "which", "who", "whose", "where",
        "can", "could", "would", "should", "do", "does", "did", "is", "are", "was", "were"
    ];

    /// <summary>
    /// True when the message opens with an interrogative, making it a question with no question mark.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The case that forced this: <c>"How do I say that is not right in Korean"</c>. It contains a
    /// perfect <c>WrongClaim</c> marker, it has no question mark, and it is a learner asking how to
    /// say something. Opening a dispute on it would constrain the next answer because the learner
    /// wanted a translation.
    /// </para>
    /// <para>
    /// Position is the whole signal, and it is why this is not the shared question-word list. "That's
    /// not <b>what</b> I asked" contains an interrogative in the middle and is a correction; the same
    /// word at the front makes it a question. A gate that read anywhere in the sentence would reject
    /// the two most common corrections in English.
    /// </para>
    /// <para>
    /// English only, deliberately. Korean puts its interrogatives mid-sentence, so a leading-token
    /// rule would not fire on a Korean question and a positional rule for Korean would need a
    /// different shape. Korean questions here are caught by the question mark, and inventing an
    /// unvalidated Korean gate would be guessing at a case no fixture has produced.
    /// </para>
    /// </remarks>
    private static bool OpensWithAnInterrogative(string normalized)
    {
        var head = SkipLeadingDiscourse(normalized);

        var firstSpace = head.IndexOf(' ', StringComparison.Ordinal);
        var firstToken = firstSpace < 0 ? head : head[..firstSpace];

        var interrogative = Array.Exists(
            LeadingInterrogatives,
            candidate => string.Equals(firstToken, candidate, StringComparison.Ordinal));

        if (!interrogative)
        {
            return false;
        }

        // A marker that starts the message wins. "What I meant was the ones from yesterday" opens
        // with an interrogative and is unambiguously a correction, because the interrogative is
        // part of the marker rather than the head of a question.
        //
        // Derived from the marker lists rather than hard-coded, so adding "how did you get that"
        // later cannot silently fall into the question gate — the exception maintains itself.
        //
        // Tested against the original text, not the trimmed head: a marker that opens the message
        // opens it, and a discourse word in front of a marker does not make it one.
        return !StartsWithAnyMarker(normalized);
    }

    /// <summary>
    /// Verbs that can head a request for material rather than a statement about the last turn.
    /// </summary>
    private static readonly string[] RequestVerbs =
        ["tell", "show", "teach", "explain", "help", "give", "translate", "let"];

    /// <summary>
    /// Frames that make the request a request about <em>language</em> rather than about the coach.
    /// </summary>
    /// <remarks>
    /// Every entry names a grammatical or translation task: how to say something, a sentence built
    /// around something, rendering something into another language. None of them can describe a
    /// defect in the previous answer, which is what keeps the gate from swallowing real disputes.
    /// </remarks>
    private static readonly string[] LanguageRequestFrames =
    [
        "how to say", "how to write", "how to translate",
        "how do i say", "how would i say", "how do you say", "how you say",
        "help me say", "help me write", "help me translate",
        "sentence using", "sentence with",
        "into korean", "into english"
    ];

    /// <summary>
    /// True when the message is an imperative request to be taught or given a phrase.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The class the interrogative gate could not see.</b> "How do I say that is not right" is
    /// caught by its question word. "Tell me how to say that is not right" is the identical request
    /// with the question word deleted, and it was opening a <c>WrongClaim</c> dispute on a learner
    /// asking for a translation — then constraining the answer that would have taught it to them.
    /// Correction language is ordinary vocabulary in a tutoring app.
    /// </para>
    /// <para>
    /// <b>Both halves are required, and that is the whole design.</b> A leading request verb alone
    /// would blanket-ignore every tell/show/explain message, including "Tell me why you said 12
    /// words, that is wrong" — the most direct way a learner has of pushing back. A frame alone
    /// would fire mid-sentence on "the sentence with the particle is wrong". Only a request verb at
    /// the head of the message <em>and</em> a grammatical or translation frame somewhere in it
    /// describes a learner asking to be taught a phrase.
    /// </para>
    /// <para>
    /// Reads the same trimmed head the interrogative gate reads, so "Please tell me…" and "Ok, show
    /// me…" behave like their unprefixed forms. There is no marker-start exception here because no
    /// marker in any list begins with a request verb; adding one would need this gate revisited,
    /// which is why the verbs are a closed list rather than a pattern.
    /// </para>
    /// </remarks>
    private static bool IsATeachingOrTranslationRequest(string normalized)
    {
        var head = SkipLeadingDiscourse(normalized);

        var firstSpace = head.IndexOf(' ', StringComparison.Ordinal);

        if (firstSpace < 0)
        {
            return false;
        }

        var firstToken = head[..firstSpace];

        if (!Array.Exists(
                RequestVerbs,
                candidate => string.Equals(firstToken, candidate, StringComparison.Ordinal)))
        {
            return false;
        }

        return Array.Exists(
            LanguageRequestFrames,
            frame => head.Contains(frame, StringComparison.Ordinal));
    }

    /// <summary>
    /// Politeness and discourse words that carry no meaning about the previous turn.
    /// </summary>
    /// <remarks>
    /// Every one of these can precede a question without changing that it is a question. They are
    /// stripped only from the front, only for the interrogative and request-frame tests, and never
    /// from the text the markers are matched against.
    /// </remarks>
    private static readonly string[] LeadingDiscourseMarkers =
    [
        // English
        "so", "also", "please", "hmm", "hmmm", "um", "uh", "ok", "okay", "and", "but",
        "well", "actually", "hey", "oh",

        // Korean. Restricted to words that are unambiguously discourse fillers or politeness in
        // initial position. Anything that could be a content word is left in place, because
        // stripping it would change what the rest of the sentence is about.
        "그럼", "그러면", "그런데", "근데", "혹시", "저기", "음", "아"
    ];

    /// <summary>
    /// The message with leading discourse and politeness words removed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The case that forced this: <c>"So how do I say that is not right in Korean"</c>. The
    /// interrogative gate read the first token, found "so", and let the message through to the
    /// markers, where "that is not right" opened a dispute against a learner asking for a
    /// translation. One filler word was the whole difference between a question and a complaint.
    /// </para>
    /// <para>
    /// Bounded to three leading words. A message that opens with four discourse markers in a row is
    /// not a shape any fixture has produced, and an unbounded loop here would let a long run of
    /// stripped tokens reach an interrogative deep inside a sentence that is not a question.
    /// </para>
    /// </remarks>
    private static string SkipLeadingDiscourse(string normalized)
    {
        var head = normalized;

        for (var stripped = 0; stripped < 3; stripped++)
        {
            var space = head.IndexOf(' ', StringComparison.Ordinal);
            if (space < 0)
            {
                return head;
            }

            var token = head[..space];

            if (!Array.Exists(
                    LeadingDiscourseMarkers,
                    candidate => string.Equals(token, candidate, StringComparison.Ordinal)))
            {
                return head;
            }

            head = head[(space + 1)..];
        }

        return head;
    }

    /// <summary>True when a correction marker begins the message.</summary>
    private static bool StartsWithAnyMarker(string normalized)
    {
        foreach (var (_, markers) in Ladder)
        {
            foreach (var marker in markers)
            {
                if (normalized.StartsWith(Normalize(marker), StringComparison.Ordinal))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// True when the text is a question or is quoting material rather than reporting a defect.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Deliberately not <c>CoachQuestionMarkers.HasQuestionOrQuotationMark</c>.</b> That helper
    /// treats the apostrophe as a quotation mark, which is right for its callers — a learner typing
    /// <c>"yes"</c> in quotes is discussing the word, not accepting an offer — and fatal here.
    /// Almost every English correction is a contraction: "that's not what I asked", "I didn't ask
    /// that", "you're wrong". Sharing that helper silently classified all of them as quoted material
    /// and the classifier matched nothing at all in English.
    /// </para>
    /// <para>
    /// So an apostrophe between two letters is a contraction and passes; anywhere else it is a
    /// quote and does not. Paired quotation marks always stop the scan.
    /// </para>
    /// <para>
    /// <b>The question-<em>word</em> gate is deliberately absent too.</b> "That's not what I asked"
    /// contains "what", and "what I meant was" contains it twice. Both are corrections. The markers
    /// here are compounds anchored on a person and a verb, which is narrow enough on its own — the
    /// word gate exists for classifiers whose vocabulary is single words, and applying it here would
    /// reject the two most common corrections in the language.
    /// </para>
    /// </remarks>
    internal static bool IsQuestionOrQuotedMaterial(string text)
    {
        for (var index = 0; index < text.Length; index++)
        {
            var character = text[index];

            if (character is '?' or '\uFF1F')
            {
                return true;
            }

            if (character is '"' or '`' or '\u201C' or '\u201D'
                or '\u300C' or '\u300D' or '\u300E' or '\u300F')
            {
                return true;
            }

            if (character is not ('\'' or '\u2018' or '\u2019'))
            {
                continue;
            }

            var isContraction = index > 0
                && index + 1 < text.Length
                && char.IsLetter(text[index - 1])
                && char.IsLetter(text[index + 1]);

            if (!isContraction)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>True when the text corrects the previous turn in any way.</summary>
    public bool IsCorrection(string? text) => Classify(text) != CoachCorrectionSignal.None;

    /// <summary>
    /// Marker match: exact substring, or a token sequence with at most one typo.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The exact path is tried first and covers Korean, where the markers are contiguous character
    /// runs rather than whitespace-delimited words and an edit-distance pass over them would be
    /// both meaningless and dangerous.
    /// </para>
    /// <para>
    /// The fuzzy path is whitespace-token based and allows one edit across the whole marker. Two
    /// edits is where "not what i asked" starts matching things that are not it.
    /// </para>
    /// </remarks>
    private static bool ContainsMarker(string normalizedText, string normalizedMarker)
    {
        if (normalizedMarker.Length == 0)
        {
            return false;
        }

        if (normalizedText.Contains(normalizedMarker, StringComparison.Ordinal))
        {
            return true;
        }

        var markerTokens = normalizedMarker.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        if (markerTokens.Length == 0)
        {
            return false;
        }

        var textTokens = normalizedText.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        if (textTokens.Length < markerTokens.Length)
        {
            return false;
        }

        for (var start = 0; start <= textTokens.Length - markerTokens.Length; start++)
        {
            if (MatchesWithOneTypo(textTokens, start, markerTokens))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>True when the window matches the marker with at most one mistyped token.</summary>
    private static bool MatchesWithOneTypo(string[] textTokens, int start, string[] markerTokens)
    {
        var typos = 0;

        for (var offset = 0; offset < markerTokens.Length; offset++)
        {
            var actual = textTokens[start + offset];
            var expected = markerTokens[offset];

            if (string.Equals(actual, expected, StringComparison.Ordinal))
            {
                continue;
            }

            // Short tokens are matched exactly. One edit turns "not" into "now" and "no" into
            // "so", so fuzzing them would invent matches rather than tolerate typos.
            if (expected.Length < MinimumFuzzyTokenLength || actual.Length < MinimumFuzzyTokenLength)
            {
                return false;
            }

            if (typos == 1 || !IsWithinOneEdit(actual, expected))
            {
                return false;
            }

            typos++;
        }

        return true;
    }

    /// <summary>
    /// Damerau-Levenshtein distance of one or less.
    /// </summary>
    /// <remarks>
    /// Transposition is included because it is the single most common typing error — "waht" for
    /// "what", "teh" for "the" — and plain Levenshtein scores it as two edits, which would put the
    /// most frequent real-world typo outside the tolerance.
    /// </remarks>
    internal static bool IsWithinOneEdit(string actual, string expected)
    {
        if (Math.Abs(actual.Length - expected.Length) > 1)
        {
            return false;
        }

        if (actual.Length == expected.Length)
        {
            var differences = 0;
            var firstDifference = -1;

            for (var index = 0; index < actual.Length; index++)
            {
                if (actual[index] == expected[index])
                {
                    continue;
                }

                differences++;

                if (differences == 1)
                {
                    firstDifference = index;
                    continue;
                }

                if (differences > 2)
                {
                    return false;
                }
            }

            if (differences <= 1)
            {
                return true;
            }

            // Exactly two differences: a transposition of adjacent characters.
            return firstDifference >= 0
                && firstDifference + 1 < actual.Length
                && actual[firstDifference] == expected[firstDifference + 1]
                && actual[firstDifference + 1] == expected[firstDifference]
                && IsIdenticalAfter(actual, expected, firstDifference + 2);
        }

        var longer = actual.Length > expected.Length ? actual : expected;
        var shorter = actual.Length > expected.Length ? expected : actual;

        var longerIndex = 0;
        var shorterIndex = 0;
        var skipped = false;

        while (longerIndex < longer.Length && shorterIndex < shorter.Length)
        {
            if (longer[longerIndex] == shorter[shorterIndex])
            {
                longerIndex++;
                shorterIndex++;
                continue;
            }

            if (skipped)
            {
                return false;
            }

            skipped = true;
            longerIndex++;
        }

        return true;
    }

    private static bool IsIdenticalAfter(string actual, string expected, int index)
    {
        for (; index < actual.Length; index++)
        {
            if (actual[index] != expected[index])
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Lower-cases, strips punctuation and emphasis, and collapses whitespace.
    /// </summary>
    /// <remarks>
    /// The same normalisation the acceptance classifier uses, and shared behaviour matters: an
    /// apostrophe stripped here and kept there would mean "that's" matched one classifier's phrase
    /// list and not the other's. Korean characters pass through unchanged; only separators go.
    /// </remarks>
    internal static string Normalize(string value)
    {
        var builder = new StringBuilder(value.Length);
        var lastWasSpace = true;

        foreach (var rune in value.Normalize(NormalizationForm.FormC))
        {
            // Apostrophes are elided, not separated. "That's" must become "thats" so it matches a
            // marker written without punctuation; treating the apostrophe as a separator produces
            // "that s", and every contraction in the marker lists — "that's not what I asked",
            // "I didn't ask", "you're wrong" — silently stops matching. Both the ASCII apostrophe
            // and the typographic one, because iOS and macOS substitute the latter as you type.
            if (rune is '\'' or '\u2019')
            {
                continue;
            }

            if (char.IsLetterOrDigit(rune))
            {
                builder.Append(char.ToLower(rune, CultureInfo.InvariantCulture));
                lastWasSpace = false;
                continue;
            }

            if (!lastWasSpace)
            {
                builder.Append(' ');
                lastWasSpace = true;
            }
        }

        return builder.ToString().Trim();
    }
}
