using System.Text.RegularExpressions;

namespace SentenceStudio.Api.Coach.Application;

/// <summary>
/// Server-owned classifier for latest-practice/study questions.
/// </summary>
/// <remarks>
/// <para>
/// Runs before the model, at the same pipeline level as the typed-decision shortcut.
/// Covers bounded English and Korean forms. The patterns are semantic groups —
/// not a single exact string — so variant phrasings work while generic language
/// examples and future-plan questions do not match.
/// </para>
/// <para>
/// A correction/dispute follow-up ("that's wrong, I practiced yesterday") is
/// classified as a <see cref="LatestStudyMatch.Correction"/> so the deterministic
/// route can produce a correction-aware re-read without planning or mutation.
/// </para>
/// </remarks>
public static class CoachLatestStudyClassifier
{
    /// <summary>The kind of latest-study match.</summary>
    public enum LatestStudyMatchKind
    {
        /// <summary>The learner is asking when they last practiced/studied.</summary>
        Query,

        /// <summary>The learner is challenging a prior latest-study answer.</summary>
        Correction
    }

    /// <summary>A successful classification result.</summary>
    public sealed record LatestStudyMatch(LatestStudyMatchKind Kind);

    // ── English patterns ────────────────────────────────────────────────

    // "when did I last study/practice", "last time I studied/practiced",
    // "when was my last study/practice", "most recent practice/study",
    // "when did I last practice", "my last practice date"
    private static readonly Regex EnLatestQuery = new(
        @"(?:when\s+did\s+I\s+last\s+(?:stud(?:y|ied)|practic(?:e|ed)))" +
        @"|(?:last\s+time\s+I\s+(?:stud(?:y|ied)|practic(?:e|ed)))" +
        @"|(?:when\s+was\s+(?:my\s+)?last\s+(?:study|practice))" +
        @"|(?:(?:my\s+)?most\s+recent\s+(?:study|practice)(?:\s+(?:date|session|time))?)" +
        @"|(?:(?:my\s+)?last\s+(?:study|practice)\s+(?:date|session|time))" +
        @"|(?:how\s+long\s+(?:since|ago)\s+(?:I\s+)?(?:last\s+)?(?:stud(?:y|ied)|practic(?:e|ed)))",
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(50));

    // Challenge follow-ups that reference practice dates or correctness
    // "that's wrong, I practiced yesterday", "no, I studied on Monday",
    // "that can't be right, I just practiced"
    private static readonly Regex EnCorrection = new(
        @"(?:(?:that(?:'s|\s+is)\s+(?:wrong|incorrect|not\s+right))" +
        @"|(?:no\s*,?\s+I\s+(?:stud(?:y|ied)|practic(?:e|ed))))" +
        @".*?(?:stud(?:y|ied)|practic(?:e|ed)|yesterday|last\s+(?:night|week)|(?:on\s+)?(?:monday|tuesday|wednesday|thursday|friday|saturday|sunday))",
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(50));

    // ── Korean patterns ─────────────────────────────────────────────────

    // 마지막으로 공부/연습 언제, 언제 마지막으로 공부/연습,
    // 최근 공부/연습 날짜, 마지막 공부/연습 언제
    private static readonly Regex KoLatestQuery = new(
        @"(?:마지막(?:으로)?\s*(?:공부|연습|학습|복습)\s*(?:한\s*(?:게|것|날)?\s*)?(?:언제|날짜))" +
        @"|(?:언제\s*마지막(?:으로)?\s*(?:공부|연습|학습|복습))" +
        @"|(?:최근(?:에?)?\s*(?:공부|연습|학습|복습)\s*(?:한\s*)?(?:날짜|언제|날|때))" +
        @"|(?:(?:공부|연습|학습|복습)\s*(?:마지막|최근)(?:으로)?\s*(?:한\s*(?:게|것|날)?\s*)?(?:언제|날짜))" +
        @"|(?:(?:공부|연습|학습|복습)\s*(?:언제\s*)?(?:마지막|최근))" +
        @"|(?:얼마\s*(?:만에|전에)\s*(?:공부|연습|학습|복습))",
        RegexOptions.Compiled | RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(50));

    // Korean correction follow-ups
    private static readonly Regex KoCorrection = new(
        @"(?:(?:아니|틀렸|잘못).*?(?:공부|연습|학습|복습))" +
        @"|(?:(?:공부|연습|학습|복습).*?(?:했는데|했어|했거든|했다고).*?(?:아니|틀렸|잘못|왜))",
        RegexOptions.Compiled | RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(50));

    /// <summary>
    /// Classify the learner's input. Returns <c>null</c> when the input does not match
    /// a latest-study question or correction.
    /// </summary>
    /// <remarks>
    /// The match is deliberately narrow. Prompts about future study plans ("when should I
    /// study next"), generic language examples, and general questions about study habits
    /// do not match. Only prompts that ask about the learner's own most-recent practice
    /// or challenge a prior latest-study answer are classified.
    /// </remarks>
    public static LatestStudyMatch? Classify(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return null;
        }

        var trimmed = input.Trim();

        // Correction patterns first: a follow-up like "that's wrong, I practiced yesterday"
        // contains both a challenge and a practice reference. Matching correction first
        // ensures the re-read path is taken.
        try
        {
            if (EnCorrection.IsMatch(trimmed) || KoCorrection.IsMatch(trimmed))
            {
                return new LatestStudyMatch(LatestStudyMatchKind.Correction);
            }

            if (EnLatestQuery.IsMatch(trimmed) || KoLatestQuery.IsMatch(trimmed))
            {
                return new LatestStudyMatch(LatestStudyMatchKind.Query);
            }
        }
        catch (RegexMatchTimeoutException)
        {
            // A timeout is a classification miss, not a crash. The model path handles
            // the prompt instead, which is the correct degradation.
            return null;
        }

        return null;
    }
}
