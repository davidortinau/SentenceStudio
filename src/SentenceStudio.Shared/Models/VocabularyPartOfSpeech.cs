namespace SentenceStudio.Shared.Models;

/// <summary>
/// Closed, canonical part-of-speech taxonomy for a <see cref="VocabularyWord"/>.
/// </summary>
/// <remarks>
/// <para>
/// The values are grounded in what the extraction pipeline already produces —
/// <c>VocabularyExtractionResponse.PartOfSpeech</c> documents exactly
/// "noun, verb, adjective, adverb, expression, counter, or particle". Until now
/// that value was computed and then dropped by <c>ToVocabularyWord()</c>, so no
/// persisted grounding for a request like "focus on active verbs" existed.
/// </para>
/// <para>
/// <b>Null vs Unknown vs Other.</b> The column is nullable and the three states
/// are distinct and load-bearing:
/// <list type="bullet">
///   <item><description><c>null</c> — never classified. Every row written before
///   this feature is null, and null must stay valid forever.</description></item>
///   <item><description><see cref="Unknown"/> — a classifier ran and could not
///   decide.</description></item>
///   <item><description><see cref="Other"/> — a real token arrived that this
///   taxonomy does not model (a future extractor value). Reading it never throws
///   and never produces an undefined enum value.</description></item>
/// </list>
/// </para>
/// </remarks>
public enum VocabularyPartOfSpeech
{
    /// <summary>A classifier ran but could not determine the part of speech.</summary>
    Unknown = 0,

    Noun = 1,
    Verb = 2,
    Adjective = 3,
    Adverb = 4,
    Expression = 5,
    Counter = 6,
    Particle = 7,

    /// <summary>A recognized token outside this taxonomy. Forward-compatibility escape hatch.</summary>
    Other = 99
}

/// <summary>
/// Canonical token &lt;-&gt; enum conversion for <see cref="VocabularyPartOfSpeech"/>.
/// </summary>
/// <remarks>
/// The database stores the canonical lowercase token, not the numeric value, so a
/// future value added by the extractor is human-readable in the column and can be
/// mapped later without a data migration. Parsing is total: an unrecognized
/// non-blank token becomes <see cref="VocabularyPartOfSpeech.Other"/> rather than
/// an unchecked cast to an undefined enum member.
/// </remarks>
public static class VocabularyPartOfSpeechTokens
{
    public const string Unknown = "unknown";
    public const string Noun = "noun";
    public const string Verb = "verb";
    public const string Adjective = "adjective";
    public const string Adverb = "adverb";
    public const string Expression = "expression";
    public const string Counter = "counter";
    public const string Particle = "particle";
    public const string Other = "other";

    /// <summary>Canonical storage token for a value. Never null.</summary>
    public static string ToToken(VocabularyPartOfSpeech value) => value switch
    {
        VocabularyPartOfSpeech.Noun => Noun,
        VocabularyPartOfSpeech.Verb => Verb,
        VocabularyPartOfSpeech.Adjective => Adjective,
        VocabularyPartOfSpeech.Adverb => Adverb,
        VocabularyPartOfSpeech.Expression => Expression,
        VocabularyPartOfSpeech.Counter => Counter,
        VocabularyPartOfSpeech.Particle => Particle,
        VocabularyPartOfSpeech.Other => Other,
        _ => Unknown
    };

    /// <summary>
    /// Total parse of a stored or extracted token. <c>null</c>/blank yields
    /// <c>null</c> (never classified); an unrecognized token yields
    /// <see cref="VocabularyPartOfSpeech.Other"/>.
    /// </summary>
    public static VocabularyPartOfSpeech? FromToken(string? token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return null;
        }

        return Normalize(token) switch
        {
            Noun or "nouns" => VocabularyPartOfSpeech.Noun,
            Verb or "verbs" => VocabularyPartOfSpeech.Verb,
            Adjective or "adjectives" or "adj" => VocabularyPartOfSpeech.Adjective,
            Adverb or "adverbs" or "adv" => VocabularyPartOfSpeech.Adverb,
            Expression or "expressions" or "phrase" => VocabularyPartOfSpeech.Expression,
            Counter or "counters" => VocabularyPartOfSpeech.Counter,
            Particle or "particles" => VocabularyPartOfSpeech.Particle,
            Unknown => VocabularyPartOfSpeech.Unknown,
            _ => VocabularyPartOfSpeech.Other
        };
    }

    /// <summary>
    /// Strict parse for caller-supplied focus input: only a canonical token of a
    /// modelled part of speech is accepted. Blank, <c>unknown</c>, <c>other</c>,
    /// and unrecognized tokens all fail, so a focus request can never resolve to
    /// "everything" or to an unusable bucket.
    /// </summary>
    public static bool TryParseFocusToken(string? token, out VocabularyPartOfSpeech value)
    {
        value = VocabularyPartOfSpeech.Unknown;

        var parsed = FromToken(token);
        if (parsed is null or VocabularyPartOfSpeech.Unknown or VocabularyPartOfSpeech.Other)
        {
            return false;
        }

        value = parsed.Value;
        return true;
    }

    private static string Normalize(string token) => token.Trim().ToLowerInvariant();
}
