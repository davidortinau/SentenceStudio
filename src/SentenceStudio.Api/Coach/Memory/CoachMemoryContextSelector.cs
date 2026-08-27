using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SentenceStudio.Contracts.LearnerMemory;

namespace SentenceStudio.Api.Coach.Memory;

/// <summary>
/// The deterministic implementation of <see cref="ICoachMemoryContextSelector"/>.
/// </summary>
/// <remarks>
/// <para>
/// Selection is a sort and a cap, nothing more. Ranking is: confirmed facts first, then a fixed
/// per-category kind priority, then recency, then id. The last tie-break exists so two facts with
/// identical timestamps still produce a stable order.
/// </para>
/// <para>
/// The budget is enforced on the exact lines the formatter will emit, and an item that does not fit
/// ends the selection rather than being shortened. A truncated preference is a different
/// preference.
/// </para>
/// </remarks>
public sealed class CoachMemoryContextSelector : ICoachMemoryContextSelector
{
    private readonly ICoachMemoryStore _store;
    private readonly IOptions<CoachMemoryOptions> _options;
    private readonly ILogger<CoachMemoryContextSelector> _logger;

    /// <summary>Creates the selector.</summary>
    public CoachMemoryContextSelector(
        ICoachMemoryStore store,
        IOptions<CoachMemoryOptions> options,
        ILogger<CoachMemoryContextSelector> logger)
    {
        _store = store;
        _options = options;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<CoachMemoryContextResult> SelectAsync(
        CoachMemoryContextRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var options = _options.Value;

        if (!options.Enabled)
        {
            return CoachMemoryContextResult.Empty(CoachMemoryContextOutcome.Disabled);
        }

        if (options.SelectionPaused)
        {
            return CoachMemoryContextResult.Empty(CoachMemoryContextOutcome.Paused);
        }

        if (request.Owner.IsEmpty)
        {
            _logger.LogWarning("[Coach] Memory selection called with no active user id — returning no memory.");
            return CoachMemoryContextResult.Empty(CoachMemoryContextOutcome.NoOwner);
        }

        IReadOnlyList<CoachMemoryFactRecord> eligible;
        try
        {
            eligible = await _store.ListEligibleForContextAsync(request.Owner, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A memory outage degrades the turn, it does not fail it. The log carries the turn
            // category and the exception type only.
            _logger.LogWarning(
                "[Coach] Memory selection unavailable. Category={Category} Error={Error}",
                request.Category,
                ex.GetType().Name);
            return CoachMemoryContextResult.Empty(CoachMemoryContextOutcome.StoreUnavailable);
        }

        var excluded = request.ExcludedKinds is { Count: > 0 }
            ? new HashSet<CoachMemoryKind>(request.ExcludedKinds)
            : null;

        var priorities = KindPriority(request.Category);

        var ranked = eligible
            .Where(f => f.Status == CoachMemoryStatus.Active)
            .Where(f => IsInScope(f, request.TargetLanguageCode))
            .Where(f => excluded is null || !excluded.Contains(f.Kind))
            .Where(f => priorities.ContainsKey(f.Kind))
            .Where(f => !string.IsNullOrEmpty(f.Value.DisplayText))
            .OrderBy(f => f.Provenance == CoachMemoryProvenance.UserConfirmed ? 0 : 1)
            .ThenBy(f => priorities[f.Kind])
            .ThenByDescending(f => f.ConfirmedAt ?? f.UpdatedAt)
            .ThenBy(f => f.Id, StringComparer.Ordinal)
            .ToList();

        var maxFacts = Math.Min(options.MaxContextFacts, CoachMemoryLimits.ContextFactsMax);
        var maxTokens = Math.Min(options.MaxContextTokens, CoachMemoryLimits.ContextTokensMax);

        var items = new List<CoachMemoryContextItem>(Math.Min(maxFacts, ranked.Count));
        var used = CoachMemoryPromptFormatter.HeaderTokens;

        foreach (var fact in ranked)
        {
            if (items.Count >= maxFacts)
            {
                break;
            }

            // Re-screen at the boundary. A row that was legal when it was saved is not necessarily
            // legal now, and this is the last place to catch that before a prompt.
            if (CoachMemoryPromptFormatter.IsSafeForPrompt(fact.Value) != CoachMemoryValueRejection.None)
            {
                _logger.LogWarning("[Coach] Memory fact omitted from prompt by content policy. Kind={Kind}", fact.Kind);
                continue;
            }

            var estimate = CoachMemoryPromptFormatter.EstimateItemTokens(fact.Kind, fact.Scope, fact.TargetLanguageCode, fact.Value.DisplayText);
            if (used + estimate > maxTokens)
            {
                break;
            }

            used += estimate;
            items.Add(new CoachMemoryContextItem(
                fact.Id,
                fact.Kind,
                fact.Scope,
                fact.TargetLanguageCode,
                fact.Value.DisplayText,
                fact.Provenance,
                estimate));
        }

        if (items.Count == 0)
        {
            return CoachMemoryContextResult.Empty(CoachMemoryContextOutcome.Empty);
        }

        try
        {
            await _store.MarkUsedAsync(request.Owner, items.Select(i => i.FactId).ToArray(), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The used-stamp is bookkeeping. Losing it must not lose the turn.
            _logger.LogDebug("[Coach] Memory used-stamp failed. Error={Error}", ex.GetType().Name);
        }

        _logger.LogDebug(
            "[Coach] Memory selected. Category={Category} Count={Count} Tokens={Tokens}",
            request.Category,
            items.Count,
            used);

        return new CoachMemoryContextResult(items, used, CoachMemoryContextOutcome.Selected);
    }

    private static bool IsInScope(CoachMemoryFactRecord fact, string? activeLanguage)
    {
        if (fact.Scope == CoachMemoryScope.Global)
        {
            return true;
        }

        return !string.IsNullOrEmpty(activeLanguage)
            && string.Equals(fact.TargetLanguageCode, activeLanguage, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The fixed relevance table. Lower is more relevant.
    /// </summary>
    /// <remarks>
    /// A kind missing from a category's table is not eligible for that category at all. That is why
    /// <see cref="CoachMemoryTurnCategory.Unspecified"/> omits the free-text goal: when the caller
    /// cannot say what the turn is about, only the closed-value preferences are carried.
    /// </remarks>
    internal static IReadOnlyDictionary<CoachMemoryKind, int> KindPriority(CoachMemoryTurnCategory category) =>
        category switch
        {
            CoachMemoryTurnCategory.GrammarExplanation => Table(
                CoachMemoryKind.ExplanationDepth,
                CoachMemoryKind.CorrectionTiming,
                CoachMemoryKind.ExampleRegister,
                CoachMemoryKind.PersistentStudyGoal),

            CoachMemoryTurnCategory.VocabularyHelp => Table(
                CoachMemoryKind.PersistentStudyGoal,
                CoachMemoryKind.ExplanationDepth,
                CoachMemoryKind.ExampleRegister,
                CoachMemoryKind.CorrectionTiming),

            CoachMemoryTurnCategory.ExampleRequest => Table(
                CoachMemoryKind.ExampleRegister,
                CoachMemoryKind.ExplanationDepth,
                CoachMemoryKind.PersistentStudyGoal,
                CoachMemoryKind.CorrectionTiming),

            CoachMemoryTurnCategory.CorrectionFeedback => Table(
                CoachMemoryKind.CorrectionTiming,
                CoachMemoryKind.ExplanationDepth,
                CoachMemoryKind.ExampleRegister,
                CoachMemoryKind.PersistentStudyGoal),

            CoachMemoryTurnCategory.StudyPlanning => Table(
                CoachMemoryKind.PersistentStudyGoal,
                CoachMemoryKind.ExplanationDepth,
                CoachMemoryKind.CorrectionTiming,
                CoachMemoryKind.ExampleRegister),

            CoachMemoryTurnCategory.GeneralConversation => Table(
                CoachMemoryKind.ExplanationDepth,
                CoachMemoryKind.CorrectionTiming,
                CoachMemoryKind.ExampleRegister,
                CoachMemoryKind.PersistentStudyGoal),

            _ => Table(
                CoachMemoryKind.ExplanationDepth,
                CoachMemoryKind.CorrectionTiming,
                CoachMemoryKind.ExampleRegister)
        };

    private static IReadOnlyDictionary<CoachMemoryKind, int> Table(params CoachMemoryKind[] order)
    {
        var table = new Dictionary<CoachMemoryKind, int>(order.Length);
        for (var i = 0; i < order.Length; i++)
        {
            table[order[i]] = i;
        }

        return table;
    }
}
