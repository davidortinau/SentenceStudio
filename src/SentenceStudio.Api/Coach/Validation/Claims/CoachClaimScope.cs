using System.Text.RegularExpressions;
using SentenceStudio.Contracts.Coach;

namespace SentenceStudio.Api.Coach.Validation.Claims;

/// <summary>
/// Which parts of an answer the honesty rules read, and which they must not.
/// </summary>
/// <remarks>
/// <para>
/// <b>Display spans only, outside Example, Form and Contrast.</b> Every rule here is about a claim
/// the coach makes; a worked example, a form breakdown and a two-form contrast are teaching
/// material, and applying claim rules to them is how a grounding layer starts deleting the lesson.
/// </para>
/// <para>
/// The concrete failure this prevents: an <c>Example</c> block containing "you don't have any
/// brothers" as a sample sentence is an unbounded negative about the learner by every syntactic
/// measure, and it is not a claim at all. So is a <c>Contrast</c> block explaining that one form
/// means "I have none". Excluding by block kind is coarser than parsing intent and is the only
/// version that cannot be wrong in the dangerous direction.
/// </para>
/// <para>
/// <b>Language role matters as much as block kind.</b> A <c>Target</c> span is text in the language
/// being studied — the thing the learner is here to read. A <c>Native</c> span is a gloss. Neither
/// asserts anything about the learner's data, and scanning them would apply English claim
/// heuristics to Korean text, which produces nonsense in both directions.
/// </para>
/// </remarks>
public static class CoachClaimScope
{
    /// <summary>
    /// The block kinds a claim rule never reads.
    /// </summary>
    /// <remarks>
    /// Named as an inclusion list of exclusions rather than derived from anything, so adding a
    /// block kind later forces a decision instead of silently inheriting a default. The census test
    /// pins this set.
    /// </remarks>
    public static readonly IReadOnlySet<CoachAnswerBlockKind> ExcludedBlockKinds =
        new HashSet<CoachAnswerBlockKind>
        {
            CoachAnswerBlockKind.Example,
            CoachAnswerBlockKind.Form,
            CoachAnswerBlockKind.Contrast
        };

    /// <summary>True when this block's spans are in scope for the claim rules.</summary>
    public static bool IsScannable(CoachAnswerBlockKind kind) => !ExcludedBlockKinds.Contains(kind);

    /// <summary>True when this span is in scope: display language, in a scannable block.</summary>
    public static bool IsScannable(CoachAnswerBlockKind kind, CoachLanguageRole language) =>
        IsScannable(kind) && language == CoachLanguageRole.Display;

    /// <summary>
    /// Every span a claim rule may read, with the coordinates a finding reports.
    /// </summary>
    public static IReadOnlyList<CoachClaimSpan> Scannable(CoachAnswerDto? answer)
    {
        if (answer is null)
        {
            return [];
        }

        var spans = new List<CoachClaimSpan>();

        for (var blockIndex = 0; blockIndex < answer.Blocks.Count; blockIndex++)
        {
            var block = answer.Blocks[blockIndex];

            if (!IsScannable(block.Kind))
            {
                continue;
            }

            for (var spanIndex = 0; spanIndex < block.Spans.Count; spanIndex++)
            {
                var span = block.Spans[spanIndex];

                if (span.Language != CoachLanguageRole.Display || string.IsNullOrWhiteSpace(span.Text))
                {
                    continue;
                }

                spans.Add(new CoachClaimSpan(blockIndex, spanIndex, block.Kind, span.Text));
            }
        }

        return spans;
    }
}

/// <summary>One display span, located.</summary>
/// <param name="BlockIndex">Zero-based block position.</param>
/// <param name="SpanIndex">Zero-based span position within the block.</param>
/// <param name="BlockKind">The enclosing block's role.</param>
/// <param name="Text">The span text. Never copied into a finding.</param>
public sealed record CoachClaimSpan(int BlockIndex, int SpanIndex, CoachAnswerBlockKind BlockKind, string Text);

/// <summary>
/// Whether a span says something about <em>the learner</em>, as opposed to about the language.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is not a digit ban.</b> Plan B6 says so directly, and the reason is that the obvious
/// implementation — flag any sentence with a number in it — bans the coach from teaching. "Korean
/// has 7 speech levels" is a fact about Korean. "You've reviewed 7 words" is a claim about the
/// learner that needs a read behind it. A digit ban cannot tell them apart and would suppress the
/// first while the second walks past whenever the model spells the number out.
/// </para>
/// <para>
/// Two detectors instead, both anchored on the referent rather than on the number.
/// </para>
/// <para>
/// <b>1. Pronoun plus state verb.</b> "You have", "your words are", "you've been". The second-person
/// referent establishes it is about the learner; the state verb establishes it is an assertion
/// about how things stand rather than instruction ("you should try", "you can practise"). Modals
/// are deliberately absent from the verb set: advice is not a claim.
/// </para>
/// <para>
/// <b>2. Unbounded negative with no digit.</b> "You don't have any", "none of your words", "you
/// haven't practised". These are the most dangerous claims the coach can make and the hardest to
/// support, because a negative is only true across the whole population — and a paged read never
/// establishes one. The no-digit condition is what makes this a separate detector: "you have 0
/// words due" is a counted claim the count rule handles, while "you have nothing due" is an
/// absolute that needs complete coverage.
/// </para>
/// </remarks>
public static class CoachLearnerStateReferent
{
    private const RegexOptions Options =
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture;

    /// <summary>
    /// Second person plus a verb of state. Advice and instruction are excluded by omitting modals.
    /// </summary>
    /// <remarks>
    /// <c>your</c> is followed by a noun phrase, so the verb may be several words away — hence the
    /// bounded gap rather than adjacency. The bound is small on purpose: an unbounded gap matches
    /// across sentence boundaries and turns "your vocabulary. Korean has" into a learner claim.
    /// </remarks>
    private static readonly Regex PronounStateVerb = new(
        @"\b(you|your|you're|you've)\b[^.!?]{0,40}?\b(have|has|had|are|is|was|were|been|" +
        @"reviewed|practised|practiced|studied|logged|completed|finished|learned|learnt|" +
        @"know|knows|own|owns|track|tracks|tracking)\b",
        Options);

    /// <summary>
    /// An absolute negative about the learner, with no number anywhere in the span.
    /// </summary>
    /// <remarks>
    /// The digit test runs over the whole span rather than the match, because a counted claim
    /// anywhere in the sentence means the count rule owns it and a double finding on one span would
    /// double-count the metric.
    /// </remarks>
    private static readonly Regex UnboundedNegative = new(
        @"\b(no|none|not|never|nothing|n['\u2019]t)\b[^.!?]{0,40}?\b(of\s+your|your|you)\b" +
        @"|\b(you|your)\b[^.!?]{0,40}?\b(no|none|never|nothing|don['\u2019]?t|doesn['\u2019]?t|" +
        @"haven['\u2019]?t|hasn['\u2019]?t|didn['\u2019]?t|aren['\u2019]?t|isn['\u2019]?t)\b",
        Options);

    private static readonly Regex AnyDigit = new(@"\d", RegexOptions.CultureInvariant);

    /// <summary>True when the span asserts something about the learner's own state.</summary>
    public static bool IsLearnerStateClaim(string text) =>
        !string.IsNullOrWhiteSpace(text)
        && (PronounStateVerb.IsMatch(text) || IsUnboundedNegative(text));

    /// <summary>
    /// True for an absolute negative about the learner carrying no number.
    /// </summary>
    /// <remarks>
    /// Exposed separately because <see cref="CoachClaimRuleCode.NegativeClaimWithoutCoverage"/>
    /// needs exactly this shape and nothing weaker. A negative is the one claim a page of rows can
    /// never support, so it is held to complete coverage while an ordinary state claim is not.
    /// </remarks>
    public static bool IsUnboundedNegative(string text) =>
        !string.IsNullOrWhiteSpace(text)
        && !AnyDigit.IsMatch(text)
        && UnboundedNegative.IsMatch(text);

    /// <summary>True when the span states a number.</summary>
    public static bool StatesADigit(string text) => !string.IsNullOrEmpty(text) && AnyDigit.IsMatch(text);

    /// <summary>Every integer the span states, in order.</summary>
    /// <remarks>
    /// Bounded to six digits. A longer run is a year, an identifier, or a model hallucinating a
    /// serial number, and none of those is a count the evidence could confirm.
    /// </remarks>
    public static IReadOnlyList<int> StatedCounts(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return [];
        }

        var counts = new List<int>();

        foreach (Match match in Regex.Matches(text, @"\b\d{1,6}\b", RegexOptions.CultureInvariant))
        {
            if (int.TryParse(match.Value, out var value))
            {
                counts.Add(value);
            }
        }

        return counts;
    }
}
