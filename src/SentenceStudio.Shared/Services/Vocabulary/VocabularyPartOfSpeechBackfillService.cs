using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SentenceStudio.Data;
using SentenceStudio.Shared.Models;

namespace SentenceStudio.Services.Vocabulary;

/// <summary>
/// Why a run stopped, or why it never started.
/// </summary>
public enum VocabularyPartOfSpeechBackfillOutcome
{
    /// <summary>The feature flag is off. No query was issued.</summary>
    Disabled = 0,

    /// <summary>No trusted profile was configured. No query was issued.</summary>
    NoScope,

    /// <summary>Nothing left to classify for the configured profiles.</summary>
    NothingToDo,

    /// <summary>The run finished within its budget.</summary>
    Completed,

    /// <summary>The run stopped because it reached <c>MaxWords</c>. Re-run to continue.</summary>
    BudgetReached,

    /// <summary>The run was cancelled. Committed batches stand; the in-flight batch was rolled back.</summary>
    Cancelled
}

/// <summary>
/// The result of one backfill run. Counts only — no ids, terms, or user identifiers.
/// </summary>
public sealed record VocabularyPartOfSpeechBackfillReport
{
    public required VocabularyPartOfSpeechBackfillOutcome Outcome { get; init; }

    /// <summary>Profiles actually processed.</summary>
    public int ProfilesProcessed { get; init; }

    /// <summary>Words sent to the classifier.</summary>
    public int WordsAttempted { get; init; }

    /// <summary>Rows whose <c>PartOfSpeech</c> went from null to a value.</summary>
    public int WordsUpdated { get; init; }

    /// <summary>Batches whose response was accepted and committed.</summary>
    public int BatchesCommitted { get; init; }

    /// <summary>Batches rejected by response validation. Nothing from a rejected batch is written.</summary>
    public int BatchesRejected { get; init; }

    /// <summary>Batches abandoned because the model call itself failed.</summary>
    public int BatchesFailed { get; init; }

    /// <summary>Prompt tokens reported by the provider, when it reports usage.</summary>
    public long InputTokens { get; init; }

    /// <summary>Completion tokens reported by the provider, when it reports usage.</summary>
    public long OutputTokens { get; init; }

    public static VocabularyPartOfSpeechBackfillReport Empty(VocabularyPartOfSpeechBackfillOutcome outcome) =>
        new() { Outcome = outcome };
}

/// <summary>
/// Opt-in, profile-scoped backfill that fills <c>VocabularyWord.PartOfSpeech</c> for rows written
/// before the column existed.
/// </summary>
/// <remarks>
/// <para>
/// <b>Scope.</b> The service refuses to run without an explicit allowlist, and every query is
/// filtered by one trusted profile id at a time. Ownership is derived exactly as
/// <c>VocabularyFocusResolver</c> derives it — a <c>VocabularyProgress</c> row for the learner, or
/// a mapping to one of their <c>LearningResource</c> rows — so the backfill can never classify a
/// word the learner does not own, and there is no code path that reaches vocabulary without a user
/// filter.
/// </para>
/// <para>
/// <b>What leaves the process.</b> Only <see cref="VocabularyPartOfSpeechRequestItem"/>: the opaque
/// word id, the target-language term, its lemma, its language, and its lexical unit type. Never the
/// native-language gloss, mnemonics, example sentences, tags, transcripts, resource text, or any
/// user, profile, or tenant identifier.
/// </para>
/// <para>
/// <b>What it writes.</b> Only <c>PartOfSpeech</c>, and only where it is currently null. An
/// existing classification is never overwritten, no other column is touched, and each batch commits
/// in its own transaction. A run is therefore idempotent and resumable: re-running picks up exactly
/// the rows still null.
/// </para>
/// </remarks>
public sealed class VocabularyPartOfSpeechBackfillService
{
    private const string Instructions =
        """
        You label vocabulary entries with a part of speech for a language-learning app.

        For every item in the request, return exactly one classification whose id is the item's id,
        copied unchanged. Return no other ids, no duplicates, and omit nothing.

        Choose exactly one token from this closed set:
        noun, verb, adjective, adverb, expression, counter, particle, unknown.

        Judge the target-language term in its own language. Use 'expression' for a multi-word phrase
        or set expression that works as a unit. Use 'counter' for a counting/measure word. Use
        'particle' for a grammatical particle or postposition. Use 'unknown' when you genuinely
        cannot decide — never guess and never invent a token outside the set.
        """;

    private readonly ApplicationDbContext _db;
    private readonly IChatClient _chatClient;
    private readonly IOptions<VocabularyPartOfSpeechBackfillOptions> _options;
    private readonly ILogger<VocabularyPartOfSpeechBackfillService> _logger;

    public VocabularyPartOfSpeechBackfillService(
        ApplicationDbContext db,
        IChatClient chatClient,
        IOptions<VocabularyPartOfSpeechBackfillOptions> options,
        ILogger<VocabularyPartOfSpeechBackfillService> logger)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _chatClient = chatClient ?? throw new ArgumentNullException(nameof(chatClient));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>Runs one backfill pass over the configured profiles.</summary>
    public async Task<VocabularyPartOfSpeechBackfillReport> RunAsync(CancellationToken cancellationToken = default)
    {
        var options = _options.Value;

        if (!options.Enabled)
        {
            _logger.LogInformation("Part-of-speech backfill is disabled. No query was issued.");
            return VocabularyPartOfSpeechBackfillReport.Empty(VocabularyPartOfSpeechBackfillOutcome.Disabled);
        }

        var profiles = options.NormalizedUserProfileIds();
        if (profiles.Count == 0)
        {
            // Fail closed. An enabled backfill with no allowlist must never mean "every tenant".
            _logger.LogWarning(
                "Part-of-speech backfill is enabled but no user profile is allowlisted. Refusing to run; no query was issued.");
            return VocabularyPartOfSpeechBackfillReport.Empty(VocabularyPartOfSpeechBackfillOutcome.NoScope);
        }

        var batchSize = options.EffectiveBatchSize;
        var budget = options.EffectiveMaxWords;

        _logger.LogInformation(
            "Part-of-speech backfill starting for {ProfileCount} profile(s). BatchSize={BatchSize} MaxWords={MaxWords}",
            profiles.Count, batchSize, budget);

        var attempted = 0;
        var updated = 0;
        var committed = 0;
        var rejected = 0;
        var failed = 0;
        long inputTokens = 0;
        long outputTokens = 0;
        var profilesProcessed = 0;
        var cancelled = false;

        foreach (var userProfileId in profiles)
        {
            if (budget - attempted <= 0)
            {
                break;
            }

            if (cancellationToken.IsCancellationRequested)
            {
                cancelled = true;
                break;
            }

            profilesProcessed++;

            // Cursor over the owned, still-unclassified rows. Ordering by id makes batching
            // deterministic, and advancing past a rejected batch stops a bad response from
            // becoming an infinite retry loop within the run.
            var cursor = string.Empty;

            while (budget - attempted > 0)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    cancelled = true;
                    break;
                }

                var take = Math.Min(batchSize, budget - attempted);
                List<VocabularyWord> batch;

                try
                {
                    batch = await QueryCandidatesAsync(userProfileId, cursor, take, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    cancelled = true;
                    break;
                }

                if (batch.Count == 0)
                {
                    break;
                }

                cursor = batch[^1].Id;
                attempted += batch.Count;

                var requested = batch.Select(w => w.Id).ToList();
                VocabularyPartOfSpeechClassificationResponse? response;

                try
                {
                    var (parsed, usage) = await ClassifyAsync(batch, cancellationToken).ConfigureAwait(false);
                    response = parsed;
                    inputTokens += usage.Input;
                    outputTokens += usage.Output;
                }
                catch (OperationCanceledException)
                {
                    cancelled = true;
                    break;
                }
                catch (Exception ex)
                {
                    failed++;
                    // Shape only: the exception type and the batch size, never the prompt or the
                    // model's raw output.
                    _logger.LogWarning(
                        "Part-of-speech classification call failed for a batch of {BatchSize}: {ExceptionType}",
                        batch.Count, ex.GetType().Name);
                    continue;
                }

                if (!TryMapResponse(response, requested, out var byId))
                {
                    rejected++;
                    _logger.LogWarning(
                        "Part-of-speech batch rejected by response validation. BatchSize={BatchSize}", batch.Count);
                    continue;
                }

                try
                {
                    var batchUpdated = await CommitBatchAsync(batch, byId, cancellationToken).ConfigureAwait(false);
                    updated += batchUpdated;
                    committed++;
                }
                catch (OperationCanceledException)
                {
                    // The transaction is disposed without commit, so this batch leaves no partial
                    // write. Everything committed before it stands and the next run resumes here.
                    cancelled = true;
                    break;
                }
            }

            if (cancelled)
            {
                break;
            }
        }

        var outcome = cancelled
            ? VocabularyPartOfSpeechBackfillOutcome.Cancelled
            : attempted == 0
                ? VocabularyPartOfSpeechBackfillOutcome.NothingToDo
                : attempted >= budget
                    ? VocabularyPartOfSpeechBackfillOutcome.BudgetReached
                    : VocabularyPartOfSpeechBackfillOutcome.Completed;

        _logger.LogInformation(
            "Part-of-speech backfill finished. Outcome={Outcome} Profiles={Profiles} Attempted={Attempted} " +
            "Updated={Updated} Committed={Committed} Rejected={Rejected} Failed={Failed} " +
            "InputTokens={InputTokens} OutputTokens={OutputTokens}",
            outcome, profilesProcessed, attempted, updated, committed, rejected, failed, inputTokens, outputTokens);

        return new VocabularyPartOfSpeechBackfillReport
        {
            Outcome = outcome,
            ProfilesProcessed = profilesProcessed,
            WordsAttempted = attempted,
            WordsUpdated = updated,
            BatchesCommitted = committed,
            BatchesRejected = rejected,
            BatchesFailed = failed,
            InputTokens = inputTokens,
            OutputTokens = outputTokens
        };
    }

    /// <summary>
    /// Owned, still-unclassified words after <paramref name="cursor"/>, ordered by id.
    /// </summary>
    /// <remarks>
    /// The ownership union runs in the database as a subquery rather than as a materialized id
    /// list, so a learner with thousands of words never turns into a parameter explosion, and the
    /// user filter is part of the same statement that reads vocabulary.
    /// </remarks>
    private Task<List<VocabularyWord>> QueryCandidatesAsync(
        string userProfileId,
        string cursor,
        int take,
        CancellationToken cancellationToken)
    {
        // Defensive: the caller already dropped blanks, but an empty id here would match rows the
        // learner does not own, so refuse rather than query.
        ArgumentException.ThrowIfNullOrWhiteSpace(userProfileId);

        var progressOwned = _db.VocabularyProgresses
            .Where(p => p.UserId == userProfileId)
            .Select(p => p.VocabularyWordId);

        var resourceOwned = _db.ResourceVocabularyMappings
            .Where(m => _db.LearningResources
                .Any(r => r.Id == m.ResourceId && r.UserProfileId == userProfileId))
            .Select(m => m.VocabularyWordId);

        var ownedIds = progressOwned.Union(resourceOwned);

        return _db.VocabularyWords
            .Where(w => w.PartOfSpeech == null
                        && ownedIds.Contains(w.Id)
                        && string.Compare(w.Id, cursor) > 0)
            .OrderBy(w => w.Id)
            .Take(take)
            .ToListAsync(cancellationToken);
    }

    /// <summary>Sends one batch to the configured chat client using structured output.</summary>
    private async Task<(VocabularyPartOfSpeechClassificationResponse? Response, (long Input, long Output) Usage)> ClassifyAsync(
        IReadOnlyList<VocabularyWord> batch,
        CancellationToken cancellationToken)
    {
        var items = batch
            .Select(w => new VocabularyPartOfSpeechRequestItem(
                w.Id,
                w.TargetLanguageTerm,
                w.Lemma,
                w.Language,
                w.LexicalUnitType.ToString()))
            .ToList();

        var payload = JsonSerializer.Serialize(items);

        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, Instructions),
            new(ChatRole.User, payload)
        };

        var response = await _chatClient
            .GetResponseAsync<VocabularyPartOfSpeechClassificationResponse>(messages, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        var usage = (
            Input: response.Usage?.InputTokenCount ?? 0,
            Output: response.Usage?.OutputTokenCount ?? 0);

        return (response.TryGetResult(out var parsed) ? parsed : null, usage);
    }

    /// <summary>
    /// Validates a response against the requested batch and indexes it by id.
    /// </summary>
    /// <remarks>
    /// The returned set must equal the requested set exactly. A duplicate, an unrequested id, a
    /// blank id, or a missing one rejects the whole batch — a partially trustworthy response is not
    /// worth writing, and silently accepting a subset would let a truncated answer look like
    /// progress.
    /// </remarks>
    private static bool TryMapResponse(
        VocabularyPartOfSpeechClassificationResponse? response,
        IReadOnlyList<string> requestedIds,
        out Dictionary<string, VocabularyPartOfSpeech> byId)
    {
        byId = new Dictionary<string, VocabularyPartOfSpeech>(StringComparer.Ordinal);

        if (response?.Classifications is not { Count: > 0 } classifications)
        {
            return false;
        }

        var requested = new HashSet<string>(requestedIds, StringComparer.Ordinal);

        foreach (var classification in classifications)
        {
            var id = classification?.Id?.Trim();

            if (string.IsNullOrEmpty(id) || !requested.Contains(id))
            {
                // Blank, unknown, or hallucinated id.
                byId.Clear();
                return false;
            }

            if (!byId.TryAdd(id, VocabularyPartOfSpeechTokens.FromToken(classification!.PartOfSpeech)
                    ?? VocabularyPartOfSpeech.Unknown))
            {
                // Duplicate id.
                byId.Clear();
                return false;
            }
        }

        if (byId.Count != requested.Count)
        {
            // Missing at least one requested id.
            byId.Clear();
            return false;
        }

        return true;
    }

    /// <summary>
    /// Applies one validated batch inside its own transaction.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The null re-check is not redundant with the query: another process could have classified a
    /// row between the read and the write, and this feature never overwrites an existing
    /// classification. It happens before the retry delegate so a retry cannot mistake its own
    /// first attempt for someone else's write and skip the row.
    /// </para>
    /// <para>
    /// The transaction runs through <c>CreateExecutionStrategy()</c> because the Aspire Npgsql
    /// registration installs <c>NpgsqlRetryingExecutionStrategy</c>, which refuses a user-initiated
    /// transaction it does not own. Without this the batch throws at commit time on PostgreSQL
    /// while passing on SQLite, which has no retrying strategy.
    /// </para>
    /// </remarks>
    private async Task<int> CommitBatchAsync(
        IReadOnlyList<VocabularyWord> batch,
        IReadOnlyDictionary<string, VocabularyPartOfSpeech> byId,
        CancellationToken cancellationToken)
    {
        var pending = batch
            .Where(w => w.PartOfSpeech is null && byId.ContainsKey(w.Id))
            .ToList();

        if (pending.Count == 0)
        {
            return 0;
        }

        var strategy = _db.Database.CreateExecutionStrategy();

        return await strategy.ExecuteAsync(
            pending,
            async (context, state, ct) =>
            {
                await using var transaction = await context.Database.BeginTransactionAsync(ct).ConfigureAwait(false);

                foreach (var word in state)
                {
                    word.PartOfSpeech = byId[word.Id];
                }

                await context.SaveChangesAsync(ct).ConfigureAwait(false);
                await transaction.CommitAsync(ct).ConfigureAwait(false);

                return state.Count;
            },
            verifySucceeded: null,
            cancellationToken).ConfigureAwait(false);
    }
}
