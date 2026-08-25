using SentenceStudio.Shared.Models;

namespace SentenceStudio.Api.Coach.Application;

/// <summary>
/// The canonical focus a learner phrase maps to, if any.
/// </summary>
/// <param name="FocusCode">Stable server-owned code, for example <c>grammar.action-verb</c>.</param>
/// <param name="DisplayLabel">Neutral English label for the focus.</param>
/// <param name="PartOfSpeech">The canonical filter handed to the resolver.</param>
public sealed record CoachVocabularyFocusAlias(
    string FocusCode,
    string DisplayLabel,
    VocabularyPartOfSpeech PartOfSpeech);

/// <summary>
/// The controlled vocabulary that turns a learner's words into a canonical focus.
/// </summary>
/// <remarks>
/// <para>
/// The model is allowed to say what the learner asked for and nothing else. It never names a part
/// of speech, so this registry — not the model — decides that "active verbs" means
/// <see cref="VocabularyPartOfSpeech.Verb"/>. That keeps the mapping reviewable, testable, and
/// identical between the baseline and harness agents.
/// </para>
/// <para>
/// It is deliberately small and refuses more than it accepts. A phrase that is not on the list
/// produces one clarifying question, never a guess: silently downgrading an unrecognized focus to
/// "some vocabulary" would hand the learner a plan that does not do what they asked while looking
/// like it did.
/// </para>
/// <para>
/// <b>"Active voice" is not on the list, deliberately.</b> It is a grammatical voice, not a word
/// class, and no part-of-speech filter expresses it. It is one letter away from "active verbs" and
/// means something quite different, so it must reach the learner as a question.
/// </para>
/// </remarks>
public static class CoachVocabularyFocusAliases
{
    /// <summary>Longest a focus description may be before it is refused.</summary>
    public const int MaxDescriptionLength = 80;

    /// <summary>Most words a focus description may contain before it is refused.</summary>
    public const int MaxDescriptionWords = 8;

    public const string ActionVerbCode = "grammar.action-verb";
    public const string DescriptiveWordCode = "grammar.descriptive-word";
    public const string NounCode = "grammar.noun";
    public const string AdverbCode = "grammar.adverb";
    public const string ExpressionCode = "grammar.expression";
    public const string CounterCode = "grammar.counter";

    private static readonly CoachVocabularyFocusAlias ActionVerb =
        new(ActionVerbCode, "action verbs", VocabularyPartOfSpeech.Verb);

    private static readonly CoachVocabularyFocusAlias DescriptiveWord =
        new(DescriptiveWordCode, "descriptive words", VocabularyPartOfSpeech.Adjective);

    private static readonly CoachVocabularyFocusAlias Noun =
        new(NounCode, "nouns", VocabularyPartOfSpeech.Noun);

    private static readonly CoachVocabularyFocusAlias Adverb =
        new(AdverbCode, "adverbs", VocabularyPartOfSpeech.Adverb);

    private static readonly CoachVocabularyFocusAlias Expression =
        new(ExpressionCode, "expressions", VocabularyPartOfSpeech.Expression);

    private static readonly CoachVocabularyFocusAlias Counter =
        new(CounterCode, "counters", VocabularyPartOfSpeech.Counter);

    /// <summary>
    /// Exact normalized phrases only. Matching is whole-phrase: a substring rule would make
    /// "not verbs" and "verbs" the same request.
    /// </summary>
    private static readonly Dictionary<string, CoachVocabularyFocusAlias> Registry =
        new(StringComparer.Ordinal)
        {
            // Verbs.
            ["active verbs"] = ActionVerb,
            ["action verbs"] = ActionVerb,
            ["actions verbs"] = ActionVerb,
            ["action verb"] = ActionVerb,
            ["active verb"] = ActionVerb,
            ["verbs"] = ActionVerb,
            ["verb"] = ActionVerb,
            ["\uB3D9\uC791 \uB3D9\uC0AC"] = ActionVerb,
            ["\uD589\uB3D9 \uB3D9\uC0AC"] = ActionVerb,
            ["\uB3D9\uC791\uB3D9\uC0AC"] = ActionVerb,
            ["\uD589\uB3D9\uB3D9\uC0AC"] = ActionVerb,
            ["\uB3D9\uC0AC"] = ActionVerb,

            // Adjectives.
            ["adjectives"] = DescriptiveWord,
            ["adjective"] = DescriptiveWord,
            ["descriptive words"] = DescriptiveWord,
            ["describing words"] = DescriptiveWord,
            ["\uD615\uC6A9\uC0AC"] = DescriptiveWord,

            // Nouns.
            ["nouns"] = Noun,
            ["noun"] = Noun,
            ["\uBA85\uC0AC"] = Noun,

            // Adverbs.
            ["adverbs"] = Adverb,
            ["adverb"] = Adverb,
            ["\uBD80\uC0AC"] = Adverb,

            // Expressions.
            ["expressions"] = Expression,
            ["expression"] = Expression,
            ["phrases"] = Expression,
            ["\uD45C\uD604"] = Expression,

            // Counters.
            ["counters"] = Counter,
            ["counter"] = Counter,
            ["\uB2E8\uC704\uBA85\uC0AC"] = Counter
        };

    /// <summary>
    /// Leading words a learner adds that carry no meaning for the mapping. Stripped so
    /// "the action verbs" and "action verbs" are the same request.
    /// </summary>
    private static readonly string[] LeadingFiller =
        ["the", "some", "my", "more", "only", "just", "mostly", "on", "to"];

    /// <summary>
    /// Normalizes a learner description to the exact form used for lookup and for display.
    /// Returns null when the description is missing, blank, or outside the bounds.
    /// </summary>
    public static string? Normalize(string? description)
    {
        if (string.IsNullOrWhiteSpace(description) || description.Length > MaxDescriptionLength)
        {
            return null;
        }

        var words = description
            .Trim()
            .ToLowerInvariant()
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(w => w.Trim('.', ',', '!', '?', '"', '\'', '\u201C', '\u201D', ':', ';'))
            .Where(w => w.Length > 0)
            .ToList();

        if (words.Count == 0 || words.Count > MaxDescriptionWords)
        {
            return null;
        }

        while (words.Count > 1 && LeadingFiller.Contains(words[0], StringComparer.Ordinal))
        {
            words.RemoveAt(0);
        }

        return string.Join(' ', words);
    }

    /// <summary>
    /// Maps a learner description to a canonical focus. Returns false for anything the registry
    /// does not name exactly, including "active voice".
    /// </summary>
    public static bool TryMap(string? description, out CoachVocabularyFocusAlias alias)
    {
        alias = default!;

        var normalized = Normalize(description);
        if (normalized is null)
        {
            return false;
        }

        return Registry.TryGetValue(normalized, out alias!);
    }

    /// <summary>Looks a focus up by its canonical code, for reload and undo.</summary>
    public static bool TryFromCode(string? focusCode, out CoachVocabularyFocusAlias alias)
    {
        alias = default!;

        if (string.IsNullOrWhiteSpace(focusCode))
        {
            return false;
        }

        alias = Registry.Values.FirstOrDefault(a =>
            string.Equals(a.FocusCode, focusCode, StringComparison.Ordinal))!;

        return alias is not null;
    }

    /// <summary>Every canonical focus the registry can produce, for tests and diagnostics.</summary>
    public static IReadOnlyList<CoachVocabularyFocusAlias> All =>
        Registry.Values.DistinctBy(a => a.FocusCode).OrderBy(a => a.FocusCode, StringComparer.Ordinal).ToList();
}
