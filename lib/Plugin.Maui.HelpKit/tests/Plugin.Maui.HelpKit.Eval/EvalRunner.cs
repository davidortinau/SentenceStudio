using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;
using Xunit;
using Xunit.Abstractions;

namespace Plugin.Maui.HelpKit.Eval;

/// <summary>
/// Eval harness for the HelpKit RAG pipeline.
///
/// Modes:
///   - Default / CI: HELPKIT_EVAL_LIVE != "1" → uses <see cref="FakeChatClient"/>. Deterministic.
///   - Live:         HELPKIT_EVAL_LIVE == "1" → requires OPENAI_API_KEY; uses a real IChatClient.
///
/// Contracts checked per item:
///   1. Keyword coverage — response must contain every expected_answer_keyword (case-insensitive).
///   2. Citation overlap — at least one of the cited paths in the response must appear in
///      required_citation_paths (unless must_refuse=true, in which case no citations are required).
///   3. Refusal compliance — when must_refuse=true, the response must contain a refusal marker.
///   4. No fabricated citations — every cited path must exist in the retrieval corpus. This is the
///      hard 0% gate; any violation fails the CI gate test.
///
/// The harness runs all items via [Theory] and records a <see cref="EvalVerdict"/> per item.
/// The <see cref="CiGate_MustPass"/> test aggregates those verdicts and enforces:
///   correct &gt;= 85% AND fabricated_citations == 0.
/// </summary>
public class EvalRunner
{
    private const double PassRateThreshold = 0.85;
    private const string LiveEnvVar = "HELPKIT_EVAL_LIVE";
    private const string ModelEnvVar = "HELPKIT_EVAL_MODEL";
    private const string ApiKeyEnvVar = "OPENAI_API_KEY";

    // Regex captures anything looking like a citation, e.g. [activities/cloze.md] or (activities/cloze.md).
    private static readonly Regex CitationPattern = new(
        @"[\[\(\<]\s*(?<path>[A-Za-z0-9_\-./]+\.md)\s*[\]\)\>]",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly string[] RefusalMarkers =
    {
        "don't have documentation",
        "do not have documentation",
        "outside my scope",
        "outside the scope",
        "can't help with that",
        "cannot help with that",
        "i can't answer",
        "i cannot answer",
        // Korean refusal phrasings — kept as plain strings so tests catch localized refusals too.
        "문서가 없",
        "범위 밖",
    };

    // Shared ledger of verdicts across [Theory] invocations — fed into the CI gate test.
    private static readonly ConcurrentBag<EvalVerdict> Verdicts = new();

    private readonly ITestOutputHelper _output;

    public EvalRunner(ITestOutputHelper output)
    {
        _output = output;
    }

    public static IEnumerable<object[]> AllGoldenItems()
    {
        foreach (var item in GoldenSet.Load())
        {
            yield return new object[] { item };
        }
    }

    [Theory]
    [MemberData(nameof(AllGoldenItems))]
    public async Task Item_meets_contract(GoldenQaItem item)
    {
        var chatClient = BuildChatClient();
        var corpusPaths = EnumerateCorpusPaths();

        // Placeholder retrieval: until IHelpKit is wired, we pass required_citation_paths as the
        // retrieval set. This keeps the harness exercisable before the full pipeline lands. When
        // IHelpKit is resolvable from DI, swap this for a real call that hands the retrieved
        // chunk paths and grounded context to the chat client.
        var retrievalPaths = item.MustRefuse
            ? Array.Empty<string>()
            : item.RequiredCitationPaths;

        var messages = BuildMessages(item, retrievalPaths);
        var response = await chatClient.GetResponseAsync(messages);
        var answer = response.Text ?? string.Empty;

        var verdict = Evaluate(item, answer, corpusPaths, retrievalPaths);
        Verdicts.Add(verdict);

        _output.WriteLine($"[{item.Id}] {(verdict.Passed ? "PASS" : "FAIL")} — {verdict.Reason}");
        _output.WriteLine($"    question : {item.Question}");
        _output.WriteLine($"    response : {Truncate(answer, 240)}");

        Assert.True(verdict.Passed, verdict.Reason);
    }

    [Fact]
    public void CiGate_MustPass()
    {
        // This test asserts the aggregate result. It depends on the theory above having run.
        // xUnit runs [Fact]s and [Theory]s in the same collection serially by default, so the
        // Verdicts bag is populated before this assertion runs — provided the class is not
        // parallelized across instances. Callers that parallelize should opt this test into a
        // separate test collection that runs last.

        var verdicts = Verdicts.ToArray();
        if (verdicts.Length == 0)
        {
            // Nothing ran (e.g., someone ran only this test). Treat as inconclusive, not silent pass.
            Assert.Fail("No eval verdicts recorded. Run the Item_meets_contract theory first.");
        }

        var total = verdicts.Length;
        var correct = verdicts.Count(v => v.Passed);
        var fabricated = verdicts.Count(v => v.FabricatedCitation);
        var rate = (double)correct / total;

        _output.WriteLine("==== HelpKit Eval Summary ====");
        _output.WriteLine($"Mode         : {(IsLive() ? "LIVE" : "FAKE")}");
        _output.WriteLine($"Items        : {total}");
        _output.WriteLine($"Correct      : {correct} ({rate:P1})");
        _output.WriteLine($"Fabricated   : {fabricated}");
        _output.WriteLine($"Threshold    : correct >= {PassRateThreshold:P0} AND fabricated == 0");

        Assert.True(fabricated == 0, $"CI gate FAIL: {fabricated} response(s) contained fabricated citations.");
        Assert.True(rate >= PassRateThreshold,
            $"CI gate FAIL: correct rate {rate:P1} is below threshold {PassRateThreshold:P0}.");
    }

    private static EvalVerdict Evaluate(
        GoldenQaItem item,
        string answer,
        HashSet<string> corpusPaths,
        IReadOnlyList<string> retrievalPaths)
    {
        var cited = CitationPattern.Matches(answer)
            .Select(m => m.Groups["path"].Value.Trim())
            .Where(p => p.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        // Fabricated citation: any cited path not present in the corpus. This is the 0% gate.
        var fabricated = cited.Where(c => !corpusPaths.Contains(c)).ToArray();
        if (fabricated.Length > 0)
        {
            return EvalVerdict.Fail(item.Id,
                $"fabricated citations: {string.Join(", ", fabricated)}",
                fabricatedCitation: true);
        }

        if (item.MustRefuse)
        {
            var lowered = answer.ToLowerInvariant();
            var refused = RefusalMarkers.Any(marker => lowered.Contains(marker));
            if (!refused)
            {
                return EvalVerdict.Fail(item.Id, "must_refuse item did not produce a refusal marker.");
            }
            return EvalVerdict.Pass(item.Id);
        }

        // Keyword coverage.
        var missingKeywords = item.ExpectedAnswerKeywords
            .Where(k => !ContainsIgnoreCase(answer, k))
            .ToArray();
        if (missingKeywords.Length > 0)
        {
            return EvalVerdict.Fail(item.Id,
                $"missing keyword(s): {string.Join(", ", missingKeywords)}");
        }

        // Citation overlap: at least one required path must appear among the cited paths.
        if (item.RequiredCitationPaths.Length > 0)
        {
            var overlap = cited.Intersect(item.RequiredCitationPaths, StringComparer.OrdinalIgnoreCase).Any();
            if (!overlap)
            {
                return EvalVerdict.Fail(item.Id,
                    $"no required citation was cited (expected one of: {string.Join(", ", item.RequiredCitationPaths)})");
            }
        }

        return EvalVerdict.Pass(item.Id);
    }

    private static bool ContainsIgnoreCase(string haystack, string needle)
    {
        if (string.IsNullOrEmpty(needle)) return true;
        return haystack.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static IEnumerable<ChatMessage> BuildMessages(GoldenQaItem item, IReadOnlyList<string> retrievalPaths)
    {
        var system = new ChatMessage(ChatRole.System,
            "You are SentenceStudio's help assistant. Ground every answer in the provided sources " +
            "and cite them using bracketed paths like [activities/cloze.md]. If you do not have " +
            "documentation to answer a question, say so plainly and do not fabricate a citation. " +
            "Mirror the user's language in your response.");

        var retrievalBlock = retrievalPaths.Count == 0
            ? "(no sources retrieved)"
            : string.Join("\n", retrievalPaths.Select(p => $"- [{p}]"));

        var user = new ChatMessage(ChatRole.User, item.Question);
        var context = new ChatMessage(ChatRole.User, $"Retrieved sources:\n{retrievalBlock}");

        return new[] { system, context, user };
    }

    private static IChatClient BuildChatClient()
    {
        if (!IsLive())
        {
            return new FakeChatClient();
        }

        var apiKey = Environment.GetEnvironmentVariable(ApiKeyEnvVar);
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new InvalidOperationException(
                $"{LiveEnvVar}=1 but {ApiKeyEnvVar} is not set. Set the key or unset {LiveEnvVar}.");
        }

        var modelId = Environment.GetEnvironmentVariable(ModelEnvVar);
        if (string.IsNullOrWhiteSpace(modelId))
        {
            modelId = "gpt-4o-mini";
        }

        // NOTE (to Zoe): wire the real OpenAI IChatClient here once the library project references
        // OpenAI.Extensions.AI or the equivalent. Example:
        //
        //   return new OpenAIClient(apiKey)
        //       .GetChatClient(modelId)
        //       .AsIChatClient();
        //
        // Until that reference exists, surface a clear error so the live mode can't silently fall
        // back to the fake.
        throw new NotImplementedException(
            $"Live mode requested (model={modelId}) but no live IChatClient is wired yet. " +
            "Add an OpenAI (or equivalent) IChatClient reference and instantiate it here.");
    }

    private static bool IsLive()
    {
        return string.Equals(
            Environment.GetEnvironmentVariable(LiveEnvVar),
            "1",
            StringComparison.Ordinal);
    }

    private static HashSet<string> EnumerateCorpusPaths()
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var root = LocateCorpusRoot();
        if (root is null)
        {
            return result;
        }

        foreach (var file in Directory.EnumerateFiles(root, "*.md", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            result.Add(relative);
        }

        return result;
    }

    private static string? LocateCorpusRoot()
    {
        var directory = Path.GetDirectoryName(typeof(EvalRunner).Assembly.Location);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory, "test-corpus");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }

            var parent = Directory.GetParent(directory);
            if (parent is null)
            {
                break;
            }
            directory = parent.FullName;
        }
        return null;
    }

    private static string Truncate(string value, int max)
    {
        if (string.IsNullOrEmpty(value) || value.Length <= max)
        {
            return value;
        }
        return value.Substring(0, max) + "...";
    }
}

public sealed record EvalVerdict(string Id, bool Passed, string Reason, bool FabricatedCitation)
{
    public static EvalVerdict Pass(string id) => new(id, true, "ok", false);
    public static EvalVerdict Fail(string id, string reason, bool fabricatedCitation = false)
        => new(id, false, reason, fabricatedCitation);
}
