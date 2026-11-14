using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using SentenceStudio.Services.DTOs;

namespace SentenceStudio.Services;

/// <summary>What the AI decided a group is (docs/duplicate-merge-ai-plan.md).</summary>
public enum VocabularyDuplicateAiVerdict
{
    /// <summary>Every copy means the same thing: merge them.</summary>
    SameWord,
    /// <summary>Every copy means something different (a homonym or another sense): keep them apart.</summary>
    DifferentWords,
    /// <summary>Some copies mean the same thing, others do not: merge each set.</summary>
    Mixed,
}

/// <summary>One copy as the AI sees it: its fields and the clues to which sense it is.</summary>
/// <param name="Resources">Titles of the learner's resources that hold it (the best sense clue).</param>
public sealed record VocabularyDuplicateAiCopy(
    string Id, string Meaning, string? Lemma, string? PartOfSpeech, string Kind, string? Language,
    string? MemoryAid, string? Example, IReadOnlyList<string> Resources);

/// <summary>One duplicate group as the AI sees it: the learner's copies of one term.</summary>
public sealed record VocabularyDuplicateAiGroup(
    string GroupId, string Term, IReadOnlyList<VocabularyDuplicateAiCopy> Copies);

/// <summary>A set of copies (word IDs) that mean the same thing, with the meaning and language to keep.</summary>
/// <param name="Language">The target language when the AI fills an Unknown one, else null.</param>
public sealed record VocabularyDuplicateAiSet(
    IReadOnlyList<string> CopyIds, string Meaning, string? Language);

/// <summary>
/// The AI's call on one group after <see cref="VocabularyDuplicateResolver.Interpret"/> checked
/// it. <see cref="Problem"/> says why it cannot be used; such a call goes to the learner.
/// </summary>
public sealed record VocabularyDuplicateAiCall(
    string GroupId, VocabularyDuplicateAiVerdict Verdict, IReadOnlyList<VocabularyDuplicateAiSet> Sets,
    double Confidence, string Reason, string? Problem)
{
    public bool Usable => Problem is null;

    /// <summary>Sure enough to apply without asking (Captain 2026-11-14: confident calls apply).</summary>
    public bool IsSure => Usable && Confidence >= (Verdict == VocabularyDuplicateAiVerdict.Mixed
        ? VocabularyDuplicateResolver.MixedThreshold : VocabularyDuplicateResolver.AutoThreshold);

    internal static VocabularyDuplicateAiCall Unusable(string groupId, string problem) =>
        new(groupId, VocabularyDuplicateAiVerdict.SameWord, [], 0, string.Empty, problem);
}

/// <summary>
/// The final call on one group and which tier made it. <see cref="FastCall"/> is the first
/// answer when the group was asked again on the reasoning tier.
/// </summary>
public sealed record VocabularyDuplicateAiResult(
    VocabularyDuplicateAiCall Call, AiTier DecidedBy, VocabularyDuplicateAiCall? FastCall = null);

/// <summary>Sends one prompt on a tier and returns the AI's typed answer (null when none).</summary>
public delegate Task<DuplicateResolutionResponse?> VocabularyDuplicateAiAsk(
    string prompt, AiTier tier, CancellationToken cancellationToken);

/// <summary>
/// Asks the AI which duplicate copies mean the same thing (docs/duplicate-merge-ai-plan.md: by meaning,
/// so a homonym or another sense of one word stays apart; when unsure, apart). The
/// prompt every client sends (the web through <see cref="IAiService"/>, the native heads through
/// their owner-bound gateway) and the checks every answer passes before anything acts on it.
/// The prompt names groups and copies by short aliases ("g1", "g1c2") that
/// <see cref="Interpret"/> maps back, so the AI never copies a word ID. Platform-neutral: no
/// database, no transport.
/// </summary>
public static class VocabularyDuplicateResolver
{
    /// <summary>Confidence at which a SameWord or DifferentWords call applies without asking.</summary>
    public const double AutoThreshold = 0.85;

    /// <summary>Confidence at which a Mixed call applies without asking.</summary>
    public const double MixedThreshold = 0.9;

    /// <summary>Groups per AI call.</summary>
    public const int BatchSize = 25;

    /// <summary>The longest meaning the AI may write.</summary>
    public const int MaxMeaningLength = 120;

    private const int MaxReasonLength = 200;

    private static readonly JsonSerializerOptions PromptJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>The groups in batches of <see cref="BatchSize"/>, one AI call each.</summary>
    public static IEnumerable<IReadOnlyList<VocabularyDuplicateAiGroup>> Batches(
        IEnumerable<VocabularyDuplicateAiGroup> groups) => groups.Chunk(BatchSize);

    /// <summary>The prompt for one batch: the rules, then the groups as JSON.</summary>
    public static string BuildPrompt(IReadOnlyList<VocabularyDuplicateAiGroup> groups,
        string nativeLanguage, string targetLanguage)
    {
        var native = LanguageOr(nativeLanguage, "English");
        var target = LanguageOr(targetLanguage, "Korean");
        var prompt = new StringBuilder();
        prompt.AppendLine($"""
            You help a {native}-speaking learner of {target} clean up duplicate vocabulary entries.
            Each group below holds the learner's copies of one {target} term. Decide which copies mean
            the same thing (they will be merged into one entry, keeping all practice history) and
            which mean different things (they stay separate entries, so the learner studies each
            meaning on its own).

            Decide by meaning, not by headword. Copies whose glosses say the same thing are the same
            word. Copies whose glosses name different meanings are different words, whether they are
            homonyms or different senses of one word. When you cannot tell whether two glosses mean
            the same thing, keep them apart.

            Verdicts:
            - SameWord: every copy means the same thing: synonyms, rewording, a fuller or shorter
              gloss, one gloss that holds the other, or the gloss in another form of the same part of
              speech: "mom" and "mother"; "sea" and "sea; ocean"; "hot" and "to be hot"; "health"
              and "healthy" for a noun.
            - DifferentWords: every copy means something different: homonyms such as "eye" and
              "snow" (눈) or "to burn" and "to ride" (타다), and different senses of one word such as
              "to open" and "to hold (an event)" (열다).
            - Mixed: some copies mean the same thing and others do not.

            Sets: every copy id of a group appears in exactly one set, copied unchanged. SameWord has
            one set with every copy. DifferentWords has one set per copy. Mixed has a set for each
            meaning, at least one of them with two or more copies.

            Meaning (one per set), in {native}: a short dictionary-style gloss, never the {target}
            term itself. When two glosses are both right, join them with "; " ("sea; ocean"); drop
            a gloss that does not fit the word's part of speech (a noun keeps its noun glosses).
            Every gloss of a verb starts with "to" ("to see; to watch"); {target} descriptive
            verbs and adjectives use the "to be ..." form ("to be fast").
            When the sets of a group could be confused, add a short clarifier in parentheses
            ("eye (body part)", "snow (weather)").

            Language (one per set): "{target}" when the term is clearly {target} and a copy's language
            is Unknown, empty or wrong (another language, "Other", "{target} (auto-generated)");
            otherwise leave it empty.

            Confidence: 0.9 or higher only when a fluent teacher of {target} would agree without
            hesitation; lower it when the copies give too little to tell. Use the resources a copy
            came from, its example and its memory aid as clues to which sense it is.

            Reason: at most 20 words, in {native}.
            """);
        prompt.AppendLine();
        prompt.AppendLine("GROUPS:");
        prompt.Append(JsonSerializer.Serialize(groups.Select((group, index) => new PromptGroup(
            GroupAlias(index), group.Term.Trim(), group.Copies.Select((copy, copyIndex) => new PromptCopy(
                CopyAlias(index, copyIndex), copy.Meaning, Blank(copy.Lemma), Blank(copy.PartOfSpeech), copy.Kind,
                Blank(copy.Language), Blank(copy.MemoryAid), Blank(copy.Example),
                copy.Resources.Count == 0 ? null : copy.Resources)).ToArray())).ToArray(), PromptJson));
        return prompt.ToString();
    }

    /// <summary>
    /// Every group's call: the fast tier first, in batches; a group it is not sure of is asked once
    /// more on the reasoning tier, whose usable answer decides (the fast one stays when the second
    /// is unusable). A batch the AI does not answer leaves its groups unusable, for the learner, and
    /// the others still resolve. Cancellation stops between batches and throws.
    /// </summary>
    public static async Task<IReadOnlyList<VocabularyDuplicateAiResult>> ResolveAsync(
        IReadOnlyList<VocabularyDuplicateAiGroup> groups, string nativeLanguage, string targetLanguage,
        VocabularyDuplicateAiAsk ask, ILogger? logger, CancellationToken cancellationToken)
    {
        var fast = await AskAsync(groups, AiTier.Fast);
        var unsure = groups.Where(group => !fast[group.GroupId].IsSure).ToList();
        var second = unsure.Count == 0
            ? new Dictionary<string, VocabularyDuplicateAiCall>(StringComparer.Ordinal)
            : await AskAsync(unsure, AiTier.Reasoning);
        return groups.Select(group =>
            {
                var first = fast[group.GroupId];
                if (!second.TryGetValue(group.GroupId, out var again))
                    return new VocabularyDuplicateAiResult(first, AiTier.Fast);
                return again.Usable || !first.Usable
                    ? new VocabularyDuplicateAiResult(again, AiTier.Reasoning, first)
                    : new VocabularyDuplicateAiResult(first, AiTier.Fast, first);
            })
            .ToArray();

        async Task<Dictionary<string, VocabularyDuplicateAiCall>> AskAsync(
            IReadOnlyList<VocabularyDuplicateAiGroup> asked, AiTier tier)
        {
            var calls = new Dictionary<string, VocabularyDuplicateAiCall>(StringComparer.Ordinal);
            foreach (var batch in Batches(asked))
            {
                cancellationToken.ThrowIfCancellationRequested();
                DuplicateResolutionResponse? response;
                try
                {
                    response = await ask(BuildPrompt(batch, nativeLanguage, targetLanguage), tier, cancellationToken);
                }
                catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
                {
                    logger?.LogError(ex, "Duplicate resolution: the {Tier} AI did not answer a batch of {Count} groups.",
                        tier, batch.Count);
                    foreach (var group in batch)
                        calls[group.GroupId] = VocabularyDuplicateAiCall.Unusable(group.GroupId, "the AI did not answer");
                    continue;
                }
                foreach (var call in Interpret(batch, response, targetLanguage))
                    calls[call.GroupId] = call;
            }
            return calls;
        }
    }

    /// <summary>
    /// Checks the AI's answer for each group of the batch <paramref name="groups"/> (the same list,
    /// in the same order, that <see cref="BuildPrompt"/> was given). A call is usable only when it
    /// answers that group once with a known verdict, its sets hold every copy exactly once and match
    /// the verdict, every meaning is a short gloss that is not the term itself (Learning Value Gate:
    /// no target-language-only meaning), and its confidence is between 0 and 1. A language other
    /// than <paramref name="targetLanguage"/> is ignored.
    /// </summary>
    public static IReadOnlyList<VocabularyDuplicateAiCall> Interpret(
        IReadOnlyList<VocabularyDuplicateAiGroup> groups, DuplicateResolutionResponse? response,
        string targetLanguage)
    {
        var answers = (response?.Groups ?? [])
            .Where(answer => answer is not null && !string.IsNullOrWhiteSpace(answer.GroupId))
            .GroupBy(answer => answer.GroupId.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.OrdinalIgnoreCase);
        var target = LanguageOr(targetLanguage, "Korean");
        return groups.Select((group, index) => answers.TryGetValue(GroupAlias(index), out var found)
                ? found.Count == 1 ? Check(group, index, found[0], target)
                    : VocabularyDuplicateAiCall.Unusable(group.GroupId, "answered more than once")
                : VocabularyDuplicateAiCall.Unusable(group.GroupId, "no answer"))
            .ToArray();
    }

    private static VocabularyDuplicateAiCall Check(VocabularyDuplicateAiGroup group, int index,
        DuplicateGroupDecision answer, string target)
    {
        var verdictName = (answer.Verdict ?? string.Empty).Replace(" ", string.Empty);
        if (!Enum.GetNames<VocabularyDuplicateAiVerdict>()
                .Contains(verdictName, StringComparer.OrdinalIgnoreCase))
            return VocabularyDuplicateAiCall.Unusable(group.GroupId, $"unknown verdict '{answer.Verdict}'");
        var verdict = Enum.Parse<VocabularyDuplicateAiVerdict>(verdictName, ignoreCase: true);

        var copyIds = group.Copies
            .Select((copy, copyIndex) => (Alias: CopyAlias(index, copyIndex), copy.Id))
            .ToDictionary(item => item.Alias, item => item.Id, StringComparer.OrdinalIgnoreCase);
        var term = group.Term.Trim();
        string? problem = null;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var sets = new List<VocabularyDuplicateAiSet>();
        foreach (var set in answer.Sets ?? [])
        {
            var ids = new List<string>();
            foreach (var alias in set?.CopyIds ?? [])
            {
                if (!copyIds.TryGetValue(alias?.Trim() ?? string.Empty, out var id))
                    problem ??= $"unknown copy '{alias}'";
                else if (!seen.Add(id))
                    problem ??= $"copy '{alias}' listed twice";
                else
                    ids.Add(id);
            }
            if (ids.Count == 0)
                problem ??= "an empty set";
            var meaning = (set?.Meaning ?? string.Empty).Trim();
            if (meaning.Length == 0)
                problem ??= "a set without a meaning";
            else if (meaning.Length > MaxMeaningLength)
                problem ??= "a meaning that is too long";
            else if (term.Length > 0 && meaning.Contains(term, StringComparison.OrdinalIgnoreCase))
                problem ??= "a meaning that holds the term";
            var language = set?.Language?.Trim();
            sets.Add(new VocabularyDuplicateAiSet(ids, meaning,
                string.Equals(language, target, StringComparison.OrdinalIgnoreCase) ? target : null));
        }
        if (seen.Count != copyIds.Count)
            problem ??= "copies left out of every set";
        problem ??= verdict switch
        {
            VocabularyDuplicateAiVerdict.SameWord when sets.Count != 1 => "SameWord needs one set",
            VocabularyDuplicateAiVerdict.DifferentWords when sets.Any(set => set.CopyIds.Count != 1)
                => "DifferentWords needs one set per copy",
            VocabularyDuplicateAiVerdict.Mixed when sets.Count < 2 || sets.All(set => set.CopyIds.Count < 2)
                => "Mixed needs two sets and one with two copies",
            _ => null
        };
        if (double.IsNaN(answer.Confidence) || answer.Confidence is < 0 or > 1)
            problem ??= "a confidence outside 0 to 1";
        var reason = (answer.Reason ?? string.Empty).Trim();
        if (reason.Length > MaxReasonLength)
            reason = reason[..MaxReasonLength];
        return new VocabularyDuplicateAiCall(group.GroupId, verdict, sets, answer.Confidence, reason, problem);
    }

    private static string GroupAlias(int index) => $"g{index + 1}";

    private static string CopyAlias(int index, int copyIndex) => $"g{index + 1}c{copyIndex + 1}";

    private static string LanguageOr(string? language, string fallback) =>
        string.IsNullOrWhiteSpace(language) ? fallback : language.Trim();

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private sealed record PromptGroup(string Id, string Term, IReadOnlyList<PromptCopy> Copies);

    private sealed record PromptCopy(string Id, string Meaning, string? Lemma, string? PartOfSpeech,
        string Kind, string? Language, string? MemoryAid, string? Example, IReadOnlyList<string>? Resources);
}
