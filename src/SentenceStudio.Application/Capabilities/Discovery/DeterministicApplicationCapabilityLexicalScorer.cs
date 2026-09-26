using System.Collections.ObjectModel;
using System.Text;
using SentenceStudio.Application.Capabilities;

namespace SentenceStudio.Application.Capabilities.Discovery;

public interface IApplicationCapabilityLexicalScorer
{
    ApplicationCapabilityLexicalScore Score(
        string query,
        ApplicationCapabilityDiscoveryDocument document);
}

public sealed class ApplicationCapabilityLexicalScore
{
    internal ApplicationCapabilityLexicalScore(
        double score,
        double negativePenalty,
        bool hardNegative,
        IReadOnlyList<ApplicationCapabilityMatchReasonCode> reasons)
    {
        Score = score;
        NegativePenalty = negativePenalty;
        HardNegative = hardNegative;
        Reasons = reasons;
    }

    public double Score { get; }

    public double NegativePenalty { get; }

    public bool HardNegative { get; }

    public IReadOnlyList<ApplicationCapabilityMatchReasonCode> Reasons { get; }
}

public sealed class DeterministicApplicationCapabilityLexicalScorer
    : IApplicationCapabilityLexicalScorer
{
    private static readonly HashSet<string> StopWords = new(StringComparer.Ordinal)
    {
        "a", "an", "and", "for", "i", "it", "me", "my", "of", "please", "the", "to"
    };

    private static readonly HashSet<string> Negations = new(StringComparer.Ordinal)
    {
        "dont", "never", "no", "not", "without"
    };

    public ApplicationCapabilityLexicalScore Score(
        string query,
        ApplicationCapabilityDiscoveryDocument document)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        ArgumentNullException.ThrowIfNull(document);

        var queryTerms = Tokenize(query);
        var normalizedQuery = Join(queryTerms);
        var reasons = new HashSet<ApplicationCapabilityMatchReasonCode>();
        var positiveScore = 0d;

        var codeTerms = Tokenize(document.Code);
        if (string.Equals(normalizedQuery, Join(codeTerms), StringComparison.Ordinal))
        {
            positiveScore = Math.Max(positiveScore, 1);
            reasons.Add(ApplicationCapabilityMatchReasonCode.ExactCode);
        }
        else
        {
            var codeOverlap = Coverage(Significant(codeTerms), Significant(queryTerms));
            if (codeOverlap > 0)
            {
                positiveScore = Math.Max(positiveScore, 0.25 * codeOverlap);
                reasons.Add(ApplicationCapabilityMatchReasonCode.NormalizedCodeTerms);
            }
        }

        foreach (var alias in document.Aliases)
        {
            if (string.Equals(normalizedQuery, Join(Tokenize(alias)), StringComparison.Ordinal))
            {
                positiveScore = Math.Max(positiveScore, 0.98);
                reasons.Add(ApplicationCapabilityMatchReasonCode.ExactAlias);
            }
        }

        if (string.Equals(
                normalizedQuery,
                Join(Tokenize(document.Family)),
                StringComparison.Ordinal))
        {
            positiveScore = Math.Max(positiveScore, 0.9);
            reasons.Add(ApplicationCapabilityMatchReasonCode.ExactFamily);
        }

        var descriptionOverlap = Coverage(
            Significant(Tokenize(document.SelectionDescription)),
            Significant(queryTerms));
        if (descriptionOverlap > 0)
        {
            positiveScore = Math.Max(positiveScore, 0.35 * descriptionOverlap);
            reasons.Add(ApplicationCapabilityMatchReasonCode.SelectionDescriptionTerms);
        }

        var negatedPositive = false;
        foreach (var example in document.PositiveExamples)
        {
            var exampleTerms = Tokenize(example);
            var exampleOverlap = Coverage(Significant(exampleTerms), Significant(queryTerms));
            if (string.Equals(normalizedQuery, Join(exampleTerms), StringComparison.Ordinal))
            {
                positiveScore = Math.Max(positiveScore, 0.85);
                reasons.Add(ApplicationCapabilityMatchReasonCode.PositiveExample);
            }
            else if (exampleOverlap >= 0.5)
            {
                positiveScore = Math.Max(positiveScore, 0.55 * exampleOverlap);
                reasons.Add(ApplicationCapabilityMatchReasonCode.PositiveExample);
            }

            if (exampleOverlap >= 0.75 && IsNegated(queryTerms, Significant(exampleTerms)))
            {
                negatedPositive = true;
            }
        }

        var negativePenalty = 0d;
        var hardNegative = false;
        foreach (var example in document.NegativeExamples)
        {
            var exampleTerms = Tokenize(example);
            var significantExampleTerms = Significant(exampleTerms);
            var overlap = Coverage(significantExampleTerms, Significant(queryTerms));
            if (string.Equals(normalizedQuery, Join(exampleTerms), StringComparison.Ordinal))
            {
                negativePenalty = 1;
                hardNegative = true;
                reasons.Add(ApplicationCapabilityMatchReasonCode.HardNegativeExample);
            }
            else if (overlap >= 0.5
                     && significantExampleTerms.Count > 0
                     && queryTerms.Contains(significantExampleTerms[0], StringComparer.Ordinal))
            {
                negativePenalty = Math.Max(negativePenalty, 0.8 * overlap);
                reasons.Add(ApplicationCapabilityMatchReasonCode.NegativeExamplePenalty);
            }
        }

        if (negatedPositive)
        {
            negativePenalty = 1;
            hardNegative = true;
            reasons.Add(ApplicationCapabilityMatchReasonCode.NegatedPositiveExample);
        }

        return new(
            Math.Round(Math.Clamp(positiveScore, 0, 1), 12, MidpointRounding.ToEven),
            Math.Round(Math.Clamp(negativePenalty, 0, 1), 12, MidpointRounding.ToEven),
            hardNegative,
            new ReadOnlyCollection<ApplicationCapabilityMatchReasonCode>(
                reasons.Order().ToArray()));
    }

    private static bool IsNegated(
        IReadOnlyList<string> queryTerms,
        IReadOnlyList<string> exampleTerms)
    {
        if (exampleTerms.Count == 0)
        {
            return false;
        }

        var firstTerm = exampleTerms[0];
        for (var index = 0; index < queryTerms.Count; index++)
        {
            if (!string.Equals(queryTerms[index], firstTerm, StringComparison.Ordinal))
            {
                continue;
            }

            var start = Math.Max(0, index - 3);
            for (var prior = start; prior < index; prior++)
            {
                if (Negations.Contains(queryTerms[prior]))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static double Coverage(
        IReadOnlyCollection<string> expected,
        IReadOnlyCollection<string> actual)
    {
        if (expected.Count == 0 || actual.Count == 0)
        {
            return 0;
        }

        var actualSet = actual.ToHashSet(StringComparer.Ordinal);
        return expected.Count(actualSet.Contains) / (double)expected.Count;
    }

    private static IReadOnlyList<string> Significant(IEnumerable<string> terms) =>
        terms.Where(term => !StopWords.Contains(term) && !Negations.Contains(term))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

    private static string Join(IEnumerable<string> terms) => string.Join(' ', terms);

    private static IReadOnlyList<string> Tokenize(string value)
    {
        var normalized = value.Normalize(NormalizationForm.FormKC).ToLowerInvariant();
        var tokens = new List<string>();
        var token = new StringBuilder();

        for (var index = 0; index < normalized.Length; index++)
        {
            var character = normalized[index];
            if (char.IsLetterOrDigit(character))
            {
                token.Append(character);
                continue;
            }

            if ((character == '\'' || character == '\u2019')
                && token.Length > 0
                && index + 1 < normalized.Length
                && char.IsLetterOrDigit(normalized[index + 1]))
            {
                continue;
            }

            FlushToken(token, tokens);
        }

        FlushToken(token, tokens);
        return tokens;
    }

    private static void FlushToken(StringBuilder token, ICollection<string> tokens)
    {
        if (token.Length == 0)
        {
            return;
        }

        tokens.Add(token.ToString());
        token.Clear();
    }
}
