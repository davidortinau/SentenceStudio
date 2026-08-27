using System.Globalization;
using System.Text;
using SentenceStudio.Services;

namespace SentenceStudio.Api.Coach.Validation;

/// <summary>
/// One item the coach must not repeat, because it is due for review.
/// The application builds this list on the server. It never sends the list to the model.
/// </summary>
public sealed record CoachEmbargoedItem(
    string? TargetTerm,
    string? NativeTerm = null,
    string? Lemma = null,
    IReadOnlyList<string>? Examples = null);

/// <summary>
/// Finds a due word, a translation, or an example in coach text.
/// The check is literal and near-literal:
/// it ignores Korean spacing, it strips Korean particles, and it compares lemmas.
/// The validator never returns the value it found. It returns masked evidence.
/// </summary>
/// <remarks>
/// <para>
/// The reducer runs this over the two model-authored strings a learner can see —
/// <c>CoachMessage</c> and <c>ClarifyingQuestion</c> — before the conversation state is
/// persisted and before any message is surfaced. A hit is terminal: no re-prompt, no
/// message, no plan write.
/// </para>
/// <para>
/// The embargo set comes from <see cref="ICoachValidationDataSource"/>, which reads the due
/// terms server-side. The model never receives them, so a pass is evidence about the answer
/// rather than about the prompt.
/// </para>
/// </remarks>
public sealed class CoachDueItemLeakValidator
{
    private const int MinCjkSpacingMatchLength = 2;
    private const int MinLatinMatchLength = 4;
    private const int MinExampleFragmentLength = 8;

    /// <summary>Common Korean particles. The list is a fallback when no segmenter is available.</summary>
    private static readonly string[] FallbackKoreanParticles =
    [
        "은", "는", "이", "가", "을", "를", "에", "의", "도", "만", "로", "으로", "와", "과", "에서", "부터", "까지", "에게", "한테"
    ];

    private readonly IReadOnlyList<string> _koreanParticles;

    public CoachDueItemLeakValidator(IEnumerable<ILanguageSegmenter>? segmenters = null)
    {
        var korean = segmenters?.FirstOrDefault(s =>
            string.Equals(s.LanguageCode, "ko", StringComparison.OrdinalIgnoreCase));

        var particles = korean?.GetFunctionWords()?.ToList();
        _koreanParticles = particles is { Count: > 0 }
            ? particles.OrderByDescending(p => p.Length).ToList()
            : FallbackKoreanParticles.OrderByDescending(p => p.Length).ToList();
    }

    /// <summary>Checks one piece of coach text against the due items.</summary>
    /// <param name="text">The coach text to check.</param>
    /// <param name="dueItems">The items that are due for review.</param>
    /// <param name="allowedVocabulary">
    /// Words the coach may use even when they match a translation, for example a
    /// category tag the tools already returned. Keep this list short.
    /// </param>
    public CoachValidationResult Validate(
        string? text,
        IReadOnlyCollection<CoachEmbargoedItem> dueItems,
        IReadOnlyCollection<string>? allowedVocabulary = null)
    {
        ArgumentNullException.ThrowIfNull(dueItems);

        if (string.IsNullOrWhiteSpace(text) || dueItems.Count == 0)
        {
            return CoachValidationResult.Valid;
        }

        var normalized = text.Normalize(NormalizationForm.FormC);
        var spacingFree = RemoveSeparators(normalized);
        var latinText = normalized.ToLowerInvariant();
        var tokenStems = Tokenize(normalized).Select(StripKoreanParticles).ToHashSet(StringComparer.Ordinal);
        var allowed = BuildAllowedSet(allowedVocabulary);

        var violations = new List<CoachViolation>();

        foreach (var item in dueItems)
        {
            CheckTargetForm(item.TargetTerm, "due_term", spacingFree, tokenStems, violations);
            CheckTargetForm(item.Lemma, "due_lemma", spacingFree, tokenStems, violations);
            CheckNativeTerm(item.NativeTerm, latinText, spacingFree, tokenStems, allowed, violations);

            if (item.Examples is null)
            {
                continue;
            }

            foreach (var example in item.Examples)
            {
                CheckExample(example, spacingFree, latinText, violations);
            }
        }

        return CoachValidationResult.From(violations);
    }

    /// <summary>Checks several pieces of coach text in one pass.</summary>
    public CoachValidationResult ValidateMany(
        IEnumerable<string?> texts,
        IReadOnlyCollection<CoachEmbargoedItem> dueItems,
        IReadOnlyCollection<string>? allowedVocabulary = null)
    {
        ArgumentNullException.ThrowIfNull(texts);

        var violations = new List<CoachViolation>();
        foreach (var text in texts)
        {
            violations.AddRange(Validate(text, dueItems, allowedVocabulary).Violations);
        }
        return CoachValidationResult.From(violations);
    }

    private void CheckTargetForm(
        string? term,
        string code,
        string spacingFreeText,
        HashSet<string> tokenStems,
        List<CoachViolation> violations)
    {
        if (string.IsNullOrWhiteSpace(term))
        {
            return;
        }

        var normalized = term.Normalize(NormalizationForm.FormC).Trim();
        var stem = StripKoreanParticles(RemoveSeparators(normalized));
        if (stem.Length == 0)
        {
            return;
        }

        var matched = tokenStems.Contains(stem)
            || (stem.Length >= MinCjkSpacingMatchLength
                && spacingFreeText.Contains(stem, StringComparison.Ordinal));

        if (matched)
        {
            violations.Add(new CoachViolation(
                CoachViolationKind.AnswerLeak,
                code,
                "The answer repeats a word that is due for review.",
                CoachValidationResult.Mask(normalized)));
        }
    }

    private void CheckNativeTerm(
        string? nativeTerm,
        string latinText,
        string spacingFreeText,
        HashSet<string> tokenStems,
        HashSet<string> allowed,
        List<CoachViolation> violations)
    {
        if (string.IsNullOrWhiteSpace(nativeTerm))
        {
            return;
        }

        var normalized = nativeTerm.Normalize(NormalizationForm.FormC).Trim();
        var lowered = normalized.ToLowerInvariant();

        if (allowed.Contains(lowered))
        {
            return;
        }

        if (ContainsLetterOutsideLatin(normalized))
        {
            CheckTargetForm(normalized, "due_gloss", spacingFreeText, tokenStems, violations);
            return;
        }

        if (lowered.Length < MinLatinMatchLength || !ContainsWord(latinText, lowered))
        {
            return;
        }

        violations.Add(new CoachViolation(
            CoachViolationKind.AnswerLeak,
            "due_gloss",
            "The answer repeats the translation of a word that is due for review.",
            CoachValidationResult.Mask(normalized)));
    }

    private static void CheckExample(
        string? example,
        string spacingFreeText,
        string latinText,
        List<CoachViolation> violations)
    {
        if (string.IsNullOrWhiteSpace(example))
        {
            return;
        }

        var normalized = example.Normalize(NormalizationForm.FormC).Trim();
        var compact = RemoveSeparators(normalized);
        if (compact.Length < MinExampleFragmentLength)
        {
            return;
        }

        var fragment = compact[..MinExampleFragmentLength];
        var matched = spacingFreeText.Contains(fragment, StringComparison.Ordinal)
            || latinText.Contains(normalized.ToLowerInvariant(), StringComparison.Ordinal);

        if (matched)
        {
            violations.Add(new CoachViolation(
                CoachViolationKind.AnswerLeak,
                "due_example",
                "The answer repeats an example sentence for a word that is due for review.",
                CoachValidationResult.Mask(normalized)));
        }
    }

    private static HashSet<string> BuildAllowedSet(IReadOnlyCollection<string>? allowedVocabulary)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        if (allowedVocabulary is null)
        {
            return set;
        }

        foreach (var value in allowedVocabulary)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                set.Add(value.Normalize(NormalizationForm.FormC).Trim().ToLowerInvariant());
            }
        }
        return set;
    }

    /// <summary>Removes every character that is not a letter or a digit.</summary>
    private static string RemoveSeparators(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var c in value)
        {
            if (char.IsLetterOrDigit(c))
            {
                builder.Append(c);
            }
        }
        return builder.ToString();
    }

    private static IEnumerable<string> Tokenize(string value)
    {
        var builder = new StringBuilder();
        foreach (var c in value)
        {
            if (char.IsLetterOrDigit(c))
            {
                builder.Append(c);
            }
            else if (builder.Length > 0)
            {
                yield return builder.ToString();
                builder.Clear();
            }
        }

        if (builder.Length > 0)
        {
            yield return builder.ToString();
        }
    }

    /// <summary>Removes the Korean particles at the end of a token.</summary>
    private string StripKoreanParticles(string token)
    {
        var current = token;
        var changed = true;

        while (changed && current.Length > 1)
        {
            changed = false;
            foreach (var particle in _koreanParticles)
            {
                if (particle.Length < current.Length && current.EndsWith(particle, StringComparison.Ordinal))
                {
                    current = current[..^particle.Length];
                    changed = true;
                    break;
                }
            }
        }

        return current;
    }

    private static bool ContainsLetterOutsideLatin(string value)
    {
        foreach (var c in value)
        {
            if (char.IsLetter(c) && c > 0x024F)
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>True when the text holds the value as a whole word.</summary>
    private static bool ContainsWord(string text, string value)
    {
        var index = 0;
        while (index <= text.Length - value.Length)
        {
            var found = text.IndexOf(value, index, StringComparison.Ordinal);
            if (found < 0)
            {
                return false;
            }

            var beforeOk = found == 0 || !char.IsLetterOrDigit(text[found - 1]);
            var afterIndex = found + value.Length;
            var afterOk = afterIndex >= text.Length || !IsWordContinuation(text, afterIndex);

            if (beforeOk && afterOk)
            {
                return true;
            }

            index = found + 1;
        }

        return false;
    }

    /// <summary>
    /// True when the next characters continue the same word.
    /// A plural or a simple suffix still counts as the same word.
    /// </summary>
    private static bool IsWordContinuation(string text, int index)
    {
        if (!char.IsLetterOrDigit(text[index]))
        {
            return false;
        }

        var remaining = text.Length - index;
        var suffix = text.Substring(index, Math.Min(remaining, 3));
        return !(suffix.StartsWith("s", StringComparison.Ordinal)
                 && (remaining == 1 || !char.IsLetterOrDigit(text[index + 1])));
    }

    /// <summary>The culture the validator uses for case rules.</summary>
    internal static CultureInfo Culture => CultureInfo.InvariantCulture;
}
