using System.Globalization;
using System.Text;

namespace SentenceStudio.Api.Coach.Application;

/// <summary>
/// Decides whether a free-text turn is allowed to change Today's Plan on its own.
/// </summary>
/// <remarks>
/// <para>
/// Once the coach also answers language questions, "the learner typed something and the model
/// called it a direct change" stops being enough. A message like
/// <c>"What's the difference between 좋아하다 and 좋다? Also make today shorter."</c> is a
/// question with a request attached, and the plan half of it must be offered, not applied.
/// </para>
/// <para>
/// So a typed direct write now needs the whole message to be a plan command: no question mark,
/// no quoted material, no target-language lexical question, no second clause carrying anything
/// else. Anything short of that is downgraded to a suggestion the learner explicitly accepts, or
/// to a clarification. Buttons and structured constraint actions are unaffected — a tap is
/// already unambiguous.
/// </para>
/// <para>
/// This is a one-way gate: it can only ever <b>reduce</b> authority. It never turns a
/// suggestion into a write.
/// </para>
/// </remarks>
public sealed class CoachWriteAuthority
{
    /// <summary>Longest message that can still read as a bare plan command.</summary>
    public const int MaxCommandLength = 160;

    /// <summary>
    /// Words that mean the learner is asking about the language rather than instructing the
    /// planner. Shared with the acceptance classifier so the two decisions read the same
    /// vocabulary.
    /// </summary>
    private static IReadOnlyList<string> QuestionMarkers => CoachQuestionMarkers.Words;

    /// <summary>
    /// Words that mean the learner is instructing the planner. At least one must be present.
    /// </summary>
    private static readonly string[] CommandMarkers =
    [
        "make", "set", "change", "cut", "shorten", "lengthen", "limit", "keep", "use", "skip",
        "no", "without", "only", "give", "plan", "today", "minute", "minutes", "min", "audio",
        "listening", "speaking", "typing", "writing", "reading", "vocabulary", "energy", "tired",
        // Focus commands. These are imperatives about the plan, not questions, and the question
        // and second-request markers are still checked first, so "stop focusing on verbs" writes
        // while "should I stop focusing on verbs?" and "...and what does 좋다 mean" do not.
        "focus", "focusing", "clear", "stop", "verbs", "nouns", "adjectives", "adverbs",
        "집중", "초점", "위주", "동사", "명사", "형용사",
        "분", "오늘", "계획", "바꿔", "줄여", "늘려", "없이", "빼", "말고", "설정", "해줘", "만들어"
    ];

    /// <summary>
    /// Connectors that introduce a second, different request. "Make it 10 minutes and no audio"
    /// is one command; "make it 10 minutes and what does this mean" is not, and that is caught by
    /// the question markers rather than here.
    /// </summary>
    private static readonly string[] SecondRequestMarkers =
    [
        "also", "additionally", "by the way", "btw", "and also", "then tell", "then explain",
        "그리고 또", "또한", "그런데", "참고로"
    ];

    /// <summary>Why a typed turn may not write.</summary>
    public enum Denial
    {
        /// <summary>The message is a plan command and nothing else.</summary>
        None = 0,

        /// <summary>The message asks something.</summary>
        AsksAQuestion,

        /// <summary>The message quotes text, so part of it is material to discuss.</summary>
        QuotesText,

        /// <summary>The message carries a second, different request.</summary>
        CarriesASecondRequest,

        /// <summary>The message names no plan constraint at all.</summary>
        NamesNoPlanChange,

        /// <summary>The message is long enough that it is prose, not a command.</summary>
        TooLongToBeACommand
    }

    /// <summary>
    /// True when the whole message is a conservative, exclusive plan command and may therefore
    /// apply immediately.
    /// </summary>
    public Denial Evaluate(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return Denial.NamesNoPlanChange;
        }

        var trimmed = text.Trim();

        if (trimmed.Length > MaxCommandLength)
        {
            return Denial.TooLongToBeACommand;
        }

        // A question mark anywhere means part of the message is a question, whatever else it
        // also says.
        if (trimmed.Contains('?') || trimmed.Contains('？'))
        {
            return Denial.AsksAQuestion;
        }

        if (ContainsQuotation(trimmed))
        {
            return Denial.QuotesText;
        }

        var normalized = Normalize(trimmed);

        foreach (var marker in SecondRequestMarkers)
        {
            if (ContainsPhrase(normalized, Normalize(marker)))
            {
                return Denial.CarriesASecondRequest;
            }
        }

        foreach (var marker in QuestionMarkers)
        {
            if (ContainsPhrase(normalized, Normalize(marker)))
            {
                return Denial.AsksAQuestion;
            }
        }

        foreach (var marker in CommandMarkers)
        {
            if (ContainsPhrase(normalized, Normalize(marker)))
            {
                return Denial.None;
            }
        }

        return Denial.NamesNoPlanChange;
    }

    /// <summary>True when the message may apply a plan change on its own.</summary>
    public bool AllowsDirectWrite(string? text) => Evaluate(text) == Denial.None;

    /// <summary>
    /// Quotation marks in the forms a learner is likely to paste.
    /// </summary>
    /// <remarks>
    /// A single quote or a right single quotation mark sitting between two letters is an
    /// apostrophe, not a quotation: "make today's plan shorter" is a plain command and must
    /// stay one. Only a quote that opens or closes a run of text counts.
    /// </remarks>
    private static bool ContainsQuotation(string value)
    {
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];

            if (c is '"' or '\u201C' or '\u201D' or '\u300C' or '\u300D' or '\u300E' or '\u300F' or '`')
            {
                return true;
            }

            if (c is '\'' or '\u2018' or '\u2019' && !IsApostrophe(value, i))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>True when the mark at <paramref name="index"/> sits inside a word.</summary>
    private static bool IsApostrophe(string value, int index) =>
        index > 0
        && index < value.Length - 1
        && char.IsLetter(value[index - 1])
        && char.IsLetter(value[index + 1]);

    /// <summary>
    /// Whole-token containment for Latin markers, prefix containment for CJK, which does not
    /// separate words with spaces.
    /// </summary>
    private static bool ContainsPhrase(string normalized, string marker)
    {
        if (marker.Length == 0)
        {
            return false;
        }

        if (!char.IsAscii(marker[0]))
        {
            return normalized.Contains(marker, StringComparison.Ordinal);
        }

        if (marker.Contains(' ', StringComparison.Ordinal))
        {
            return normalized.Contains(marker, StringComparison.Ordinal);
        }

        foreach (var word in normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (string.Equals(word, marker, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static string Normalize(string value)
    {
        var builder = new StringBuilder(value.Length);
        var lastWasSpace = true;

        foreach (var rune in value.Normalize(NormalizationForm.FormC))
        {
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
