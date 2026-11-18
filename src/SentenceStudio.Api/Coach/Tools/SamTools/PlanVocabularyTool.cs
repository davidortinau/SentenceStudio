using Microsoft.Extensions.Logging;
using SentenceStudio.Api.Coach.Operations;
using SentenceStudio.Application.Vocabulary;
using SentenceStudio.Contracts.Coach;
using SentenceStudio.Services.Plans;

namespace SentenceStudio.Api.Coach.Tools.SamTools;

/// <summary>
/// Lists the words today's plan works on, with their meaning and mastery: "preview today's
/// vocabulary", "which plan words am I worst at".
/// </summary>
/// <remarks>
/// <para>
/// The learner asked to see these words, so unlike the browse in <c>list_user_vocabularies</c> the
/// due words are named (Captain, 2026-11-17: Sam lists plan and review words when asked). The one
/// exception is a word whose answer is hidden on the learner's screen right now, in the middle of a
/// quiz: it is left out entirely and only counted.
/// </para>
/// <para>
/// The answer is also handed to the turn (<see cref="CoachWriteTurnScope.ListedWords"/>), so the
/// app shows the list under Sam's reply as rows that open each word, built by the server rather
/// than typed by the model.
/// </para>
/// </remarks>
public sealed class PlanVocabularyTool : CoachToolBase
{
    /// <summary>The longest list this read returns, whatever the caller asks for.</summary>
    public const int MaxResults = 40;
    private const int DefaultResults = 20;
    private const int MaxTermLength = 80;

    private readonly CoachTodayPlanReader _plans;
    private readonly IVocabularyQueries _vocabulary;
    private readonly CoachWriteTurnScope _turn;
    private readonly IPlanDateContext _dates;
    private readonly ILogger<PlanVocabularyTool> _logger;

    public PlanVocabularyTool(
        IUserScopeProvider userScope,
        CoachTodayPlanReader plans,
        IVocabularyQueries vocabulary,
        CoachWriteTurnScope turn,
        IPlanDateContext dates,
        ILogger<PlanVocabularyTool> logger)
        : base(userScope)
    {
        _plans = plans;
        _vocabulary = vocabulary;
        _turn = turn;
        _dates = dates;
        _logger = logger;
    }

    public override string ToolName => CoachToolNames.GetPlanVocabulary;

    /// <param name="order"><c>plan</c> (the plan's own order) or <c>weakest</c> (lowest mastery first).</param>
    public async Task<PlanVocabularyResult> GetAsync(string? order = null, int? maxResults = null, CancellationToken ct = default)
    {
        var userId = RequireUserProfileId();
        var weakest = IsWeakestFirst(order);
        var limit = Math.Clamp(maxResults ?? DefaultResults, 1, MaxResults);

        try
        {
            var plan = await _plans.GetTodayAsync(ct);
            if (plan is null)
                return new PlanVocabularyResult(false, 0, [], 0, Scope(weakest, limit, 0, 0, 0, 0));

            var planIds = CurrentPlanSummaryTool.PlanWordIds(plan);
            var hidden = HiddenOnScreen(_turn.ClientScreen);
            var visibleIds = planIds.Where(id => !hidden.Contains(id)).ToList();
            var withheld = planIds.Count - visibleIds.Count;

            var words = await _vocabulary.GetOwnedWordsAsync(userId, visibleIds, ct);
            var position = visibleIds.Select((id, index) => (id, index))
                .ToDictionary(p => p.id, p => p.index, StringComparer.Ordinal);

            var ordered = weakest
                // Practised words by mastery, weakest first; never-practised ones have no score, so last.
                ? words.OrderBy(w => w.HasProgress ? 0 : 1).ThenBy(w => w.MasteryScore).ThenBy(w => position[w.WordId])
                : words.OrderBy(w => position[w.WordId]);

            var entries = ordered
                .Take(limit)
                .Select(w => new PlanVocabularyEntry(
                    WordId: w.WordId,
                    TargetTerm: SanitizeMetadata(w.TargetLanguageTerm, MaxTermLength),
                    NativeTerm: SanitizeMetadata(w.NativeLanguageTerm, MaxTermLength),
                    MasteryPct: w.HasProgress ? (int)Math.Round(w.MasteryScore * 100) : null,
                    DaysSinceLastPractice: w.HasProgress && w.LastPracticedAt is { } last
                        ? DaysSince(last)
                        : null))
                .ToList();

            // Several calls in one turn add up, so the list and the leak scan cover every word the
            // model was shown; the last call's order is the one the reply describes.
            var earlier = _turn.ListedWords?.Words ?? [];
            _turn.ListedWords = new CoachListedWords(weakest,
                entries.Concat(earlier.Where(e => entries.All(n => n.WordId != e.WordId))).ToList());

            return new PlanVocabularyResult(
                HasPlan: true,
                TotalCount: planIds.Count,
                Words: entries,
                WithheldOnScreen: withheld,
                Scope: Scope(weakest, limit, entries.Count, words.Count, planIds.Count, withheld));
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { throw DataAccessFailure(ex); }
    }

    internal static bool IsWeakestFirst(string? order) =>
        order?.Trim().ToLowerInvariant() is "weakest" or "mastery" or "worst" or "lowest";

    /// <summary>
    /// The vocabulary on a screen that is hiding an answer. Every one is left out: which side of a
    /// word is hidden depends on the prompt, and the rest of a running quiz is still to come.
    /// </summary>
    internal static HashSet<string> HiddenOnScreen(CoachClientScreenContext? screen) =>
        screen is { AnswerHidden: true, Items: { } items }
            ? items.Where(item => item is not null
                    && item.Kind == CoachClientScreenItemKinds.VocabularyWord
                    && !string.IsNullOrWhiteSpace(item.Id))
                .Select(item => item.Id!.Trim())
                .ToHashSet(StringComparer.Ordinal)
            : new HashSet<string>(StringComparer.Ordinal);

    /// <summary>Whole days between the learner's local day of practice and their local today.</summary>
    private int DaysSince(DateTime lastUtc)
    {
        var local = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(lastUtc, DateTimeKind.Utc), _dates.TimeZone);
        return Math.Max(0, _dates.UserLocalDate.DayNumber - DateOnly.FromDateTime(local).DayNumber);
    }

    /// <param name="matched">The plan's words this learner owns and may see now.</param>
    /// <param name="eligible">Every word the plan lists.</param>
    /// <param name="hiddenOnScreen">Words left out because the learner's screen is hiding their answer.</param>
    private CoachResultScope Scope(bool weakest, int requested, int returned, int matched, int eligible, int hiddenOnScreen)
    {
        var limited = matched > returned;
        // A word hidden on screen is withheld as a due-review answer is: it exists and the model
        // may not have it. Named ahead of the result limit, as the vocabulary search does.
        var withheldReason = hiddenOnScreen > 0 ? CoachScopeWithheldReason.DueReviewEmbargo
            : limited ? CoachScopeWithheldReason.ResultLimit
            : CoachScopeWithheldReason.None;
        var day = _dates.UserLocalDate;
        return new CoachResultScope
        {
            Coverage = limited || hiddenOnScreen > 0 ? CoachScopeCoverage.PageOfOwnedSet : CoachScopeCoverage.CompleteOwnedSet,
            Order = weakest ? CoachScopeOrder.MasteryAscending : CoachScopeOrder.PriorityAscending,
            OrderHonored = true,
            Filters = CoachScopeFilters.OwnerScoped | CoachScopeFilters.CalendarDay,
            AsOfUtc = _dates.UtcNow,
            WindowStartDate = day,
            WindowEndDate = day,
            RequestedCount = requested,
            ReturnedCount = returned,
            MatchedCount = matched,
            EligiblePopulationCount = eligible,
            Truncated = limited,
            WithheldReason = withheldReason,
            WithheldCount = Math.Max(0, matched - returned) + hiddenOnScreen,
            DefinitionCode = CoachScopeDefinition.PlanVocabulary,
            MinimumEvidence = CoachScopeMinimumEvidence.None,
            TieBreak = CoachScopeTieBreak.None,
            ClockBasis = CoachScopeClockBasis.LearnerLocalDay,
            ReferenceMode = CoachScopeReferenceMode.CalendarDay
        };
    }
}

/// <summary>The words a list tool showed this turn, for the list under the reply.</summary>
public sealed record CoachListedWords(bool WeakestFirst, IReadOnlyList<PlanVocabularyEntry> Words);
