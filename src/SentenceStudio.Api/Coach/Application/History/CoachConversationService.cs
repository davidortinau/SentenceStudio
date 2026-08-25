using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using SentenceStudio.Api.Coach.Agents;
using SentenceStudio.Api.Coach.Persistence;
using SentenceStudio.Api.Coach.Persistence.History;
using SentenceStudio.Api.Coach.Validation.Claims;
using SentenceStudio.Api.Coach.Runtime;
using SentenceStudio.Api.Coach.Telemetry;
using SentenceStudio.Contracts.Coach;
using SentenceStudio.Services.Plans;

namespace SentenceStudio.Api.Coach.Application.History;

/// <summary>
/// The durable coach conversation service. See <see cref="ICoachConversationService"/>.
/// </summary>
/// <remarks>
/// <para>
/// The turn path here is deliberately ordered so that no failure can produce a success-shaped
/// gap: claim the operation before the model runs, append the learner message before the model
/// runs, run the existing reducer, append its validated visible output, stamp the checkpoint,
/// then complete the operation with the exact public response. A crash between any two of those
/// leaves a durable record that says what already happened, so a retry with the same key
/// reconstructs the same answer instead of charging the learner twice.
/// </para>
/// </remarks>
public sealed class CoachConversationService : ICoachConversationService
{
    /// <summary>
    /// The schema version stamped on a stored turn outcome. A stored outcome written under a
    /// version this build cannot read is treated as absent rather than misparsed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Version 2 adds the tool trace.</b> Version 1 stored the answer at the root; version 2
    /// wraps it in <see cref="CoachStoredTurnOutcome"/> so a nullable, content-free trace can sit
    /// beside it. The change is additive and the reader branches on the stored version, so a
    /// version-1 row still yields its answer with a null trace.
    /// </para>
    /// <para>
    /// That branch is the load-bearing part. Bumping the version without it would make every
    /// version-1 row fail the equality check in the reader and read back as no answer at all — the
    /// learner's completed turn silently becoming an empty one. <see cref="ReadableOutcomeVersions"/>
    /// is what stops that, and a test holds both versions to it.
    /// </para>
    /// </remarks>
    private const int OutcomeSchemaVersion = 3;

    // W9 R0 adds a fourth section, `grounding`, and deliberately does NOT bump this.
    //
    // A named section is invisible to a reader that does not look for it — sections are read by
    // name and an absent one answers null — so a pre-W9 build reads a payload containing grounding
    // and returns exactly the answer, trace and dispute it returned before. A bump to 4 would be
    // strictly worse: during a rolling deployment an older replica reading a v4 row falls into the
    // unknown-version arm below and reports NO ANSWER AT ALL. That is the failure the version-2
    // comment above warns about, arriving through the mechanism meant to prevent it.
    //
    // Version 4 therefore stays unreadable, and a frozen pre-W9 reader emulation in the tests holds
    // the compatibility claim rather than leaving it to this comment.

    /// <summary>
    /// The wrapped version that predates the dispute section. Still written by no build, still read.
    /// </summary>
    /// <remarks>
    /// W8 raised the version from 2 to 3 to add a dispute beside the answer and the trace. Version 2
    /// is read by exactly the same section-scoped parser: a v2 payload simply has no dispute
    /// section, and <c>TryGetSection</c> already answers null for an absent one. That is why the
    /// bump costs one enum arm rather than a second parser — the tolerance was built in at v2 and
    /// this is the first time it has been collected.
    /// </remarks>
    private const int WrappedOutcomeSchemaVersionWithoutDispute = 2;

    /// <summary>
    /// The version this build writes, exposed so a test can pin it.
    /// </summary>
    /// <remarks>
    /// W9 R0's central claim is that a fourth section arrives without a bump. That claim is only
    /// checkable if the version is observable, and a test that read the constant by reflection would
    /// keep passing after somebody renamed it.
    /// </remarks>
    internal static int CurrentOutcomeSchemaVersion => OutcomeSchemaVersion;

    /// <summary>The outcome versions this build can read.</summary>
    /// <remarks>
    /// Named rather than inlined so adding version 3 is one edit in one place, and so the reader's
    /// tolerance is a fact a reviewer can find without reading the parser.
    /// </remarks>
    private const int LegacyOutcomeSchemaVersion = 1;

    /// <summary>How long one worker holds the single-writer slot before another may take over.</summary>
    /// <remarks>
    /// Short on purpose: it is the delay a learner waits after a genuine crash before the retry
    /// can proceed, so a long lease turns a dead worker into a stuck conversation. It is safe to
    /// keep it short precisely because a live worker renews it — see
    /// <see cref="CoachTurnLeaseHeartbeat"/>. Without renewal this value would also be the
    /// maximum length of a turn, and a turn that outran it would have its work taken over while
    /// it was still producing it.
    /// </remarks>
    private static readonly TimeSpan LeaseDuration = TimeSpan.FromMinutes(2);

    /// <summary>The newest visible messages considered when rebuilding a turn from the ledger.</summary>
    private const int RebuildMessageCap = 50;

    /// <summary>
    /// The character budget for rebuilt history. A character cap is the honest bound here: the
    /// token count belongs to a tokenizer this layer does not own, and four characters per token
    /// is the conservative ratio the rest of the coach already assumes.
    /// </summary>
    private const int RebuildCharacterCap = 8_000;

    private static readonly JsonSerializerOptions OutcomeJson = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// This turn's tool observations, when a host registered the scoped buffer.
    /// </summary>
    /// <remarks>
    /// Optional so a host that has not opted into observation — and every test that builds this
    /// service by hand — writes exactly the outcome it wrote before, minus the trace. The version
    /// is still stamped 2, because the shape is the wrapper either way; only the trace is absent.
    /// </remarks>
    private readonly Tools.Observation.ICoachTurnObservationBuffer? _observations;

    /// <summary>
    /// This worker's lease identity. Machine plus process is enough: the lease only has to
    /// distinguish one live worker from another, and a restarted process must not inherit the
    /// lease its predecessor held.
    /// </summary>
    private static readonly string LeaseOwnerId = BuildLeaseOwnerId();

    private readonly IUserScopeProvider _userScope;
    private readonly ICoachConversationStore _conversations;
    private readonly ICoachMessageStore _messages;
    private readonly ICoachTurnOperationStore _operations;
    private readonly ICoachTurnLeaseRenewer _leases;
    private readonly ICoachHistoryExportReader _export;
    private readonly ICoachSessionService _sessions;
    private readonly CoachRunRegistry _runs;
    private readonly TimeProvider _clock;
    private readonly IOptionsMonitor<CoachOptions> _options;
    private readonly ILogger<CoachConversationService> _logger;
    private readonly CoachTelemetry _telemetry;

    /// <summary>
    /// The write ledger, when the write tools are wired. Read-only from here.
    /// </summary>
    /// <remarks>
    /// Optional so a host with the write tools switched off — and the hand-constructed tests that
    /// predate them — still build this service. When it is absent, history simply carries no
    /// proposal cards, which is the correct rendering for a deployment that cannot produce one.
    /// </remarks>
    private readonly Operations.CoachWriteOperationService? _writeLedger;

    /// <summary>
    /// The correction-state coordinator, when the feature is registered.
    /// </summary>
    /// <remarks>
    /// Optional for the same reason the write ledger is: a host that has not wired it — and the
    /// hand-constructed tests that predate it — must still build. Absent behaves exactly as the
    /// flag being off, which is the fail-safe reading.
    /// </remarks>
    private readonly CoachDisputeCoordinator? _disputes;

    public CoachConversationService(
        IUserScopeProvider userScope,
        ICoachConversationStore conversations,
        ICoachMessageStore messages,
        ICoachTurnOperationStore operations,
        ICoachTurnLeaseRenewer leases,
        ICoachHistoryExportReader export,
        ICoachSessionService sessions,
        CoachRunRegistry runs,
        TimeProvider clock,
        IOptionsMonitor<CoachOptions> options,
        ILogger<CoachConversationService> logger,
        CoachTelemetry telemetry,
        Operations.CoachWriteOperationService? writeLedger = null,
        Tools.Observation.ICoachTurnObservationBuffer? observations = null,
        CoachDisputeCoordinator? disputes = null)
    {
        _disputes = disputes;
        _writeLedger = writeLedger;
        _observations = observations;
        _userScope = userScope;
        _conversations = conversations;
        _messages = messages;
        _operations = operations;
        _leases = leases;
        _export = export;
        _sessions = sessions;
        _runs = runs;
        _clock = clock;
        _options = options;
        _logger = logger;
        _telemetry = telemetry;
    }

    public bool IsEnabled => _options.CurrentValue.IsDurableHistoryEnabled;

    public async Task<CoachOperationResult<CoachConversationDto>> CreateAsync(
        StartCoachConversationRequest request,
        CancellationToken cancellationToken = default)
    {
        if (Gate<CoachConversationDto>(out var owner) is { } denied)
        {
            return denied;
        }

        if (string.IsNullOrWhiteSpace(request.IdempotencyKey))
        {
            return CoachOperationResult<CoachConversationDto>.Problem(
                CoachOperationStatus.InvalidInput,
                CoachProblemTypes.InvalidTurnInput,
                "An idempotency key is required to create a conversation.");
        }

        // The id is derived from the key so a retried create returns the same conversation
        // instead of leaving a second empty thread behind. Deriving rather than storing the key
        // keeps the client's value out of the database.
        var conversationId = DeriveConversationId(owner, request.IdempotencyKey);

        var existing = await _conversations.GetAsync(owner, conversationId, cancellationToken).ConfigureAwait(false);
        if (existing.Status == CoachHistoryStatus.Success)
        {
            return Ok(existing.Conversation!, hasActiveCheckpoint: false);
        }

        var title = string.IsNullOrWhiteSpace(request.Title)
            ? FallbackTitle()
            : Clamp(request.Title.Trim(), CoachHistoryLimits.TitleMaxLength);

        var created = await _conversations.CreateAsync(
            owner,
            new CreateCoachConversationRequest(
                title,
                string.IsNullOrWhiteSpace(request.Title)
                    ? CoachConversationTitleSource.Generated
                    : CoachConversationTitleSource.Learner,
                NormalizeLanguage(request.TargetLanguageCode),
                conversationId),
            cancellationToken).ConfigureAwait(false);

        return created.Status switch
        {
            CoachHistoryStatus.Success => Ok(created.Conversation!, hasActiveCheckpoint: false),
            CoachHistoryStatus.InvalidRequest => CoachOperationResult<CoachConversationDto>.Problem(
                CoachOperationStatus.InvalidInput,
                CoachProblemTypes.InvalidTurnInput,
                "That conversation could not be created."),
            _ => Unavailable<CoachConversationDto>()
        };
    }

    public async Task<CoachOperationResult<CoachConversationPageDto>> ListAsync(
        int? pageSize,
        string? cursor,
        CancellationToken cancellationToken = default)
    {
        if (Gate<CoachConversationPageDto>(out var owner) is { } denied)
        {
            return denied;
        }

        var page = await _conversations
            .ListAsync(owner, pageSize, cursor, cancellationToken)
            .ConfigureAwait(false);

        if (page.Status == CoachHistoryStatus.InvalidCursor)
        {
            // A tampered or foreign cursor is a client bug or an attack, and both deserve the
            // same answer: nothing about whose cursor it was.
            return CoachOperationResult<CoachConversationPageDto>.Problem(
                CoachOperationStatus.InvalidInput,
                CoachProblemTypes.InvalidCursor,
                "That page reference is not valid.");
        }

        if (page.Status != CoachHistoryStatus.Success)
        {
            return Unavailable<CoachConversationPageDto>();
        }

        var items = page.Items
            .Select(record => CoachHistoryProjection.ToConversation(record, hasActiveCheckpoint: false))
            .ToArray();

        return CoachOperationResult<CoachConversationPageDto>.Ok(
            new CoachConversationPageDto { Items = items, NextCursor = page.NextCursor });
    }

    public async Task<CoachOperationResult<CoachConversationDto>> GetAsync(
        string conversationId,
        CancellationToken cancellationToken = default)
    {
        if (Gate<CoachConversationDto>(out var owner) is { } denied)
        {
            return denied;
        }

        var found = await _conversations.GetAsync(owner, conversationId, cancellationToken).ConfigureAwait(false);
        if (found.Status != CoachHistoryStatus.Success)
        {
            return NotFound<CoachConversationDto>();
        }

        var checkpoint = await HasActiveCheckpointAsync(conversationId, cancellationToken).ConfigureAwait(false);
        return Ok(found.Conversation!, checkpoint);
    }

    public async Task<CoachOperationResult<CoachMessagePageDto>> GetMessagesAsync(
        string conversationId,
        int? pageSize,
        string? before,
        CancellationToken cancellationToken = default)
    {
        if (Gate<CoachMessagePageDto>(out var owner) is { } denied)
        {
            return denied;
        }

        var page = string.IsNullOrWhiteSpace(before)
            ? await _messages.GetLatestAsync(owner, conversationId, pageSize, cancellationToken).ConfigureAwait(false)
            : await _messages.GetBeforeAsync(owner, conversationId, before, pageSize, cancellationToken).ConfigureAwait(false);

        return page.Status switch
        {
            CoachHistoryStatus.Success => CoachOperationResult<CoachMessagePageDto>.Ok(
                new CoachMessagePageDto
                {
                    ConversationId = conversationId,
                    Items = await ProjectWithWritesAsync(conversationId, page.Items, cancellationToken)
                        .ConfigureAwait(false),
                    PreviousCursor = page.PreviousCursor,
                    UnreadableCount = page.UnreadableCount
                }),
            CoachHistoryStatus.InvalidCursor => CoachOperationResult<CoachMessagePageDto>.Problem(
                CoachOperationStatus.InvalidInput,
                CoachProblemTypes.InvalidCursor,
                "That page reference is not valid."),
            CoachHistoryStatus.NotFound => NotFound<CoachMessagePageDto>(),
            _ => Unavailable<CoachMessagePageDto>()
        };
    }

    public async Task<CoachOperationResult<CoachConversationDto>> UpdateAsync(
        string conversationId,
        UpdateCoachConversationRequest request,
        CancellationToken cancellationToken = default)
    {
        if (Gate<CoachConversationDto>(out var owner) is { } denied)
        {
            return denied;
        }

        var current = await _conversations.GetAsync(owner, conversationId, cancellationToken).ConfigureAwait(false);
        if (current.Status != CoachHistoryStatus.Success)
        {
            return NotFound<CoachConversationDto>();
        }

        // The version check happens here as well as in the store. The store's check is the one
        // that is safe under concurrency; this one exists so a client that sent a stale version
        // is told so even when the rename would otherwise have been a no-op. Omitting the version
        // is an unconditional write — a deliberate choice for the single-device case, where
        // demanding a token the client never read would make rename impossible.
        if (request.ExpectedStateVersion is { } expected && current.Conversation!.Version != expected)
        {
            return Conflict<CoachConversationDto>();
        }

        var record = current.Conversation!;

        if (!string.IsNullOrWhiteSpace(request.Title))
        {
            var title = request.Title.Trim();
            if (title.Length > CoachHistoryLimits.TitleMaxLength)
            {
                // Refused, not truncated. A silently shortened title is a surprise the learner
                // only discovers later, in a list they cannot correct from.
                return CoachOperationResult<CoachConversationDto>.Problem(
                    CoachOperationStatus.InvalidInput,
                    CoachProblemTypes.InvalidTurnInput,
                    "The title is too long.");
            }

            var renamed = await _conversations
                .RenameAsync(owner, conversationId, title, cancellationToken)
                .ConfigureAwait(false);

            record = renamed.Status switch
            {
                CoachHistoryStatus.Success => renamed.Conversation!,
                CoachHistoryStatus.NotFound => null,
                CoachHistoryStatus.Conflict => null,
                _ => null
            };

            if (record is null)
            {
                return renamed.Status == CoachHistoryStatus.NotFound
                    ? NotFound<CoachConversationDto>()
                    : Conflict<CoachConversationDto>();
            }
        }

        var hasCheckpoint = await HasActiveCheckpointAsync(conversationId, cancellationToken).ConfigureAwait(false);

        if (request.Close is { } close)
        {
            // Closing is durable intent, and it also ends the runtime checkpoint. The ledger is
            // permanent by design, so closing hides nothing; it only refuses new turns until the
            // learner reopens the thread.
            var closed = await _conversations
                .SetClosedAsync(owner, conversationId, close, cancellationToken)
                .ConfigureAwait(false);

            if (closed.Status == CoachHistoryStatus.Conflict)
            {
                return Conflict<CoachConversationDto>();
            }

            if (closed.Status != CoachHistoryStatus.Success)
            {
                return NotFound<CoachConversationDto>();
            }

            record = closed.Conversation!;

            if (close)
            {
                await _sessions.DeleteSessionAsync(conversationId, cancellationToken).ConfigureAwait(false);
                hasCheckpoint = false;
            }
        }

        return Ok(record, hasCheckpoint);
    }

    public async Task<CoachOperationResult<CoachTurnOperationDto>> GetOperationAsync(
        string conversationId,
        string operationId,
        CancellationToken cancellationToken = default)
    {
        if (Gate<CoachTurnOperationDto>(out var owner) is { } denied)
        {
            return denied;
        }

        var operation = await _operations.GetAsync(owner, operationId, cancellationToken).ConfigureAwait(false);
        if (operation is null || !string.Equals(operation.ConversationId, conversationId, StringComparison.Ordinal))
        {
            return NotFound<CoachTurnOperationDto>();
        }

        var messages = await ReadTurnMessagesAsync(owner, operation, cancellationToken).ConfigureAwait(false);
        var outcome = await _operations.GetOutcomeAsync(owner, operationId, cancellationToken).ConfigureAwait(false);

        return CoachOperationResult<CoachTurnOperationDto>.Ok(
            ToOperationDto(operation, DeserializeOutcome(outcome?.Payload, outcome?.SchemaVersion), messages));
    }

    public async Task<CoachOperationResult<CoachTurnOperationDto>> CancelOperationAsync(
        string conversationId,
        string operationId,
        CancellationToken cancellationToken = default)
    {
        if (Gate<CoachTurnOperationDto>(out var owner) is { } denied)
        {
            return denied;
        }

        var result = await _operations.RequestCancelAsync(owner, operationId, cancellationToken).ConfigureAwait(false);
        if (result.Outcome == CoachTurnFinalizeOutcome.NotFound || result.Operation is null)
        {
            return NotFound<CoachTurnOperationDto>();
        }

        if (!string.Equals(result.Operation.ConversationId, conversationId, StringComparison.Ordinal))
        {
            return NotFound<CoachTurnOperationDto>();
        }

        // The durable flag is what a turn running on another replica will see. The registry call
        // is what stops a run on this one immediately. Both are needed: neither alone covers the
        // case the other handles.
        _runs.Cancel(owner.UserProfileId, conversationId);

        var messages = await ReadTurnMessagesAsync(owner, result.Operation, cancellationToken).ConfigureAwait(false);
        return CoachOperationResult<CoachTurnOperationDto>.Ok(ToOperationDto(result.Operation, result: null, messages));
    }

    public async Task<CoachOperationResult<bool>> CancelActiveTurnAsync(
        string conversationId,
        CancellationToken cancellationToken = default)
    {
        if (Gate<bool>(out var owner) is { } denied)
        {
            return denied;
        }

        // The registry stops a run on this replica now; the durable flag is what a run on another
        // replica sees at its next stage boundary. Signal the registry either way, because a turn
        // can be in flight locally in the moment between its claim and the row becoming visible.
        _runs.Cancel(owner.UserProfileId, conversationId);

        var active = await _operations.FindActiveAsync(owner, conversationId, cancellationToken)
            .ConfigureAwait(false);

        if (active is null)
        {
            return CoachOperationResult<bool>.Ok(false);
        }

        var result = await _operations.RequestCancelAsync(owner, active.Id, cancellationToken)
            .ConfigureAwait(false);

        // AlreadyTerminal means the turn finished between the lookup and the write. The learner
        // asked for it to stop and it stopped, so there is nothing to report as a failure.
        return CoachOperationResult<bool>.Ok(result.Outcome is CoachTurnFinalizeOutcome.Success);
    }

    public async Task<CoachOperationResult<bool>> DeleteAsync(
        string conversationId,
        CancellationToken cancellationToken = default)
    {
        // Deletion deliberately ignores the feature flag, matching session deletion: a learner
        // must be able to erase history even after durable history is switched off for them.
        if (!TryOwner(out var owner))
        {
            return Unavailable<bool>();
        }

        _runs.Cancel(owner.UserProfileId, conversationId);
        await _sessions.DeleteSessionAsync(conversationId, cancellationToken).ConfigureAwait(false);

        var hidden = await _conversations.SoftDeleteAsync(owner, conversationId, cancellationToken).ConfigureAwait(false);
        if (hidden == CoachHistoryStatus.NotFound)
        {
            // Already gone. Deleting twice is a success, not a 404, or a retried delete after a
            // dropped response would surface as an error the learner cannot act on.
            return CoachOperationResult<bool>.Ok(true);
        }

        if (hidden != CoachHistoryStatus.Success)
        {
            return Unavailable<bool>();
        }

        var purged = await _conversations.PurgeAsync(owner, conversationId, cancellationToken).ConfigureAwait(false);
        if (purged != CoachHistoryStatus.Success && purged != CoachHistoryStatus.NotFound)
        {
            // The row is already hidden from every read path, so the learner's request is
            // honoured. The purge is a contributor step and a background sweep can finish it.
            _logger.LogWarning("Coach conversation purge deferred with status {Status}.", purged);
        }

        return CoachOperationResult<bool>.Ok(true);
    }

    public async Task<CoachOperationResult<CoachConversationExport>> OpenExportAsync(
        string conversationId,
        CancellationToken cancellationToken = default)
    {
        if (!TryOwner(out var owner))
        {
            return Unavailable<CoachConversationExport>();
        }

        var found = await _conversations.GetAsync(owner, conversationId, cancellationToken).ConfigureAwait(false);
        if (found.Status != CoachHistoryStatus.Success)
        {
            return NotFound<CoachConversationExport>();
        }

        // The reader streams straight from the database. Nothing is buffered to a temp file and
        // no server-side export state is created, so an abandoned download leaves nothing behind.
        return CoachOperationResult<CoachConversationExport>.Ok(
            new CoachConversationExport(
                found.Conversation!,
                _export.StreamMessagesAsync(owner, conversationId, cancellationToken)));
    }

    public async Task<CoachOperationResult<CoachTurnOperationDto>> SubmitTurnAsync(
        string conversationId,
        CoachConversationTurnRequest request,
        CancellationToken cancellationToken = default)
    {
        if (Gate<CoachTurnOperationDto>(out var owner) is { } denied)
        {
            return denied;
        }

        if (string.IsNullOrWhiteSpace(request.IdempotencyKey))
        {
            return CoachOperationResult<CoachTurnOperationDto>.Problem(
                CoachOperationStatus.InvalidInput,
                CoachProblemTypes.InvalidTurnInput,
                "An idempotency key is required to submit a turn.");
        }

        // Required, because it is the only handle the caller keeps if the response is lost. The
        // idempotency key cannot serve that purpose: it is salted and hashed before storage, so
        // there is no way to look an operation up by it, and exposing a form that could be looked
        // up would leak the digest the replay check depends on.
        if (string.IsNullOrWhiteSpace(request.OperationId))
        {
            return CoachOperationResult<CoachTurnOperationDto>.Problem(
                CoachOperationStatus.InvalidInput,
                CoachProblemTypes.InvalidTurnInput,
                "An operation id is required to submit a turn.");
        }

        // The claim happens before anything the learner can be charged for. Everything after it
        // is recoverable from the durable row; everything before it has had no effect.
        var claim = await _operations.ClaimAsync(
            owner,
            new ClaimCoachTurnRequest(
                conversationId,
                request.IdempotencyKey.Trim(),
                CanonicalRequest(conversationId, request.Turn),
                LeaseOwnerId,
                LeaseDuration,
                request.OperationId.Trim()),
            cancellationToken).ConfigureAwait(false);

        switch (claim.Outcome)
        {
            case CoachTurnClaimOutcome.ConversationNotFound:
            {
                // The claim path only accepts an active conversation, so "not found" also covers
                // a closed one. Telling those apart costs one read on a path that already failed,
                // and a learner who closed a thread deserves a better answer than "gone".
                var existing = await _conversations.GetAsync(owner, conversationId, cancellationToken).ConfigureAwait(false);
                if (existing.Status == CoachHistoryStatus.Success
                    && existing.Conversation!.Status == CoachConversationStatus.Closed)
                {
                    return CoachOperationResult<CoachTurnOperationDto>.Problem(
                        CoachOperationStatus.PlanChangedElsewhere,
                        CoachProblemTypes.ConversationStateConflict,
                        "That conversation is closed. Reopen it before sending another message.");
                }

                return NotFound<CoachTurnOperationDto>();
            }

            case CoachTurnClaimOutcome.PayloadConflict:
                return CoachOperationResult<CoachTurnOperationDto>.Problem(
                    CoachOperationStatus.PlanChangedElsewhere,
                    CoachProblemTypes.IdempotencyConflict,
                    "That idempotency key was already used for a different request.");

            case CoachTurnClaimOutcome.InProgress:
            case CoachTurnClaimOutcome.ConversationBusy:
                return CoachOperationResult<CoachTurnOperationDto>.Problem(
                    CoachOperationStatus.RunInProgress,
                    CoachProblemTypes.RunInProgress,
                    "That conversation is already running a turn.");

            case CoachTurnClaimOutcome.ReplayCompleted:
            case CoachTurnClaimOutcome.ReplayTerminal:
            {
                // A retry after a dropped response returns exactly what the first attempt
                // produced. No model call, no ledger append, no plan write.
                var replayed = claim.Operation!;
                var messages = await ReadTurnMessagesAsync(owner, replayed, cancellationToken).ConfigureAwait(false);
                return CoachOperationResult<CoachTurnOperationDto>.Ok(
                    ToOperationDto(
                        replayed,
                        DeserializeOutcome(claim.StoredOutcome, claim.StoredOutcomeSchemaVersion),
                        messages));
            }

            case CoachTurnClaimOutcome.Claimed:
                break;

            default:
                return Unavailable<CoachTurnOperationDto>();
        }

        var operation = claim.Operation!;
        var fence = new CoachTurnFence(operation.Id, LeaseOwnerId, claim.FencingVersion);

        // Renewal starts before anything is written and stops only after the operation is
        // finalized, so the whole owned lifetime — reconciliation, the learner's line, the model
        // run, the reducer's plan write, the response append, the completion — happens under one
        // continuously-held lease rather than under a grant that quietly ran out mid-turn.
        await using var lease = CoachTurnLeaseHeartbeat.Start(
            _leases, owner, fence, LeaseDuration, _clock, _logger, cancellationToken);

        // A claim that hands back an existing Pending or Running row is a recovery, not a start:
        // the previous attempt died and its lease expired. Before running anything again, find
        // out how far that attempt actually got, because "run it again" is only safe if the plan
        // write did not already land.
        if (operation.AttemptCount > 1)
        {
            CoachOperationResult<CoachTurnOperationDto>? reconciled;

            try
            {
                reconciled = await ReconcileAsync(owner, conversationId, operation, fence, lease, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (CoachTurnLeaseLostException)
            {
                // Recovery writes a receipt, and that write is fenced like any other. Being
                // refused here means a third worker is already recovering this operation, so
                // this one steps back rather than narrating a recovery it is not performing.
                return LeaseLost<CoachTurnOperationDto>();
            }

            if (reconciled is not null)
            {
                return reconciled;
            }
        }

        try
        {
            // Coverage is measured against the ledger as it stood *before* this turn. That is the
            // conversation the checkpoint is supposed to remember; the message about to be written
            // is what this turn is for, so demanding the checkpoint already covered it would force
            // a rebuild on every single turn and throw away the agent's memory each time.
            var required = _sessions.CheckpointIdentity(
                conversationId,
                await LedgerHeadAsync(owner, conversationId, cancellationToken).ConfigureAwait(false));

            // The learner's own line lands before the model is called, and before the checkpoint is
            // consulted, so a crash cannot swallow what they typed. It therefore sits in the ledger
            // by the time a rebuild reads it, and its sequence is the boundary that keeps the
            // rebuild from replaying the very message this turn exists to answer.
            var learner = await AppendLearnerMessageAsync(
                owner, conversationId, operation, fence, request.Turn, cancellationToken).ConfigureAwait(false);

            var checkpoint = await _sessions
                .EnsureCheckpointAsync(conversationId, required, cancellationToken)
                .ConfigureAwait(false);

            // A missing, expired, version-mismatched, or rotated checkpoint rebuilds from the
            // ledger rather than starting blind. Losing the agent's own memory must not look to
            // the learner like the conversation never happened.
            var prior = checkpoint.Rebuilt
                ? await BuildPriorMessagesAsync(
                        owner,
                        conversationId,
                        // A chip tap or a constraint action writes no learner line, so there is no
                        // row to stop before and the whole ledger as it stood is history. That is
                        // the head measured above, and one past it is the exclusive bound.
                        learner ?? required.CoveredSequence + 1,
                        cancellationToken)
                    .ConfigureAwait(false)
                : Array.Empty<CoachPriorMessage>();

            if (await IsCancelRequestedAsync(owner, operation.Id, cancellationToken).ConfigureAwait(false))
            {
                return await CancelTurnAsync(owner, conversationId, operation, fence, lease, cancellationToken)
                    .ConfigureAwait(false);
            }

            // The correction state, read once before the model runs. It has to be here rather than
            // inside the session service: the dispute lives in the protected turn outcomes and this
            // is the only layer that decrypts them.
            var disputeContext = await LoadDisputeContextAsync(owner, conversationId, cancellationToken)
                .ConfigureAwait(false);

            var turn = await _sessions.SubmitTurnAsync(
                conversationId,
                // The durable operation id becomes the turn's client id, so any plan revision this
                // turn writes is stamped with it. That stamp is what recovery reads to decide
                // whether the plan already moved, and it must be the server's operation id rather
                // than whatever the caller happened to put in the field.
                WithOperationId(request.Turn, operation.Id),
                new CoachTurnExecutionContext
                {
                    PriorMessages = prior,
                    BypassProcessIdempotency = true,
                    // Losing the lease stops the turn for the same reason a cancel does, and at the
                    // same stage boundary: after the model has answered and before anything is
                    // applied. A superseded worker that ran on past this point would write a plan
                    // revision and a memory candidate for a turn another worker is also answering.
                    IsCancelRequested = ct => new ValueTask<bool>(
                        lease.IsLeaseLost
                            ? Task.FromResult(true)
                            : IsCancelRequestedAsync(owner, operation.Id, ct)),
                    ActiveDispute = disputeContext.ActiveDispute,
                    PriorCoachMessageId = disputeContext.PriorCoachMessageId,
                    PriorTrace = disputeContext.PriorTrace
                },
                // The lease token, not the request token: it cancels on either, so a run whose
                // conversation has been taken over unwinds instead of finishing work nobody will
                // accept.
                lease.Token).ConfigureAwait(false);

            // Bounded rebuild retry: if the agent session was malformed and no prior
            // messages were available, rebuild context from the ledger and retry once.
            // The second attempt uses the rebuilt PriorMessages and null AgentSessionJson.
            // A second RequiresRebuild is a hard failure — no loop.
            if (turn.RequiresRebuild)
            {
                _logger.LogWarning(
                    "[Coach] Conversation {ConversationId}: AgentSession deserialization failed. " +
                    "Rebuilding context from ledger and retrying once.",
                    conversationId);

                var rebuilt = await BuildPriorMessagesAsync(
                    owner,
                    conversationId,
                    learner ?? required.CoveredSequence + 1,
                    cancellationToken).ConfigureAwait(false);

                turn = await _sessions.SubmitTurnAsync(
                    conversationId,
                    WithOperationId(request.Turn, operation.Id),
                    new CoachTurnExecutionContext
                    {
                        PriorMessages = rebuilt,
                        BypassProcessIdempotency = true,
                        IsCancelRequested = ct => new ValueTask<bool>(
                            lease.IsLeaseLost
                                ? Task.FromResult(true)
                                : IsCancelRequestedAsync(owner, operation.Id, ct)),

                        // The retry is the same turn, so it carries the same correction state. A
                        // rebuild that dropped it would hand the learner a fresh, unconstrained
                        // answer precisely when the first attempt had already gone wrong.
                        ActiveDispute = disputeContext.ActiveDispute,
                        PriorCoachMessageId = disputeContext.PriorCoachMessageId,
                        PriorTrace = disputeContext.PriorTrace
                    },
                    lease.Token).ConfigureAwait(false);

                if (turn.RequiresRebuild)
                {
                    _telemetry.RecordSessionRestoration("deserialization_fallback");
                    return CoachOperationResult<CoachTurnOperationDto>.Problem(
                        CoachOperationStatus.InternalError,
                        CoachProblemTypes.Unavailable,
                        "Session state unrecoverable after rebuild retry.");
                }

                _telemetry.RecordSessionRestoration("deserialization_fallback");
            }

            if (turn.Status == CoachOperationStatus.RunCancelled)
            {
                // Cancelled after the model answered but before anything was applied. The learner
                // sees a notice; the plan sees nothing. A lease loss reaches the same probe, so
                // tell the two apart before writing a cancellation the learner never asked for.
                if (lease.IsLeaseLost)
                {
                    return LeaseLost<CoachTurnOperationDto>();
                }

                return await CancelTurnAsync(owner, conversationId, operation, fence, lease, cancellationToken)
                    .ConfigureAwait(false);
            }

            if (!turn.IsOk || turn.Value is null)
            {
                // The learner's message stays in the ledger. A failed turn that also swallowed
                // what the learner typed would be worse than the failure itself.
                await lease.QuiesceAsync().ConfigureAwait(false);

                var failed = await _operations.FailAsync(
                    owner,
                    operation.Id,
                    LeaseOwnerId,
                    claim.FencingVersion,
                    Clamp(turn.Status.ToString(), CoachHistoryLimits.ErrorCodeMaxLength),
                    cancellationToken).ConfigureAwait(false);

                WarnIfUnsettled(failed, operation.Id, nameof(ICoachTurnOperationStore.FailAsync));

                return CoachOperationResult<CoachTurnOperationDto>.Problem(
                    turn.Status,
                    turn.ProblemType ?? CoachProblemTypes.Unavailable,
                    turn.Detail ?? "That turn did not complete.");
            }

            var appended = await AppendResponseAsync(owner, conversationId, fence, turn.Value, cancellationToken)
                .ConfigureAwait(false);

            // From here on the ledger rows are the response. Aligning before the outcome is stored
            // means a replay of this operation returns the same identifiers as the original call.
            var answer = WithLedgerIdentity(turn.Value, appended.Messages);

            // Coverage advances only here, after the output is validated and committed. Stamping
            // earlier would let a rejected turn poison the checkpoint, and the next turn would
            // build on something the ledger never accepted.
            if (appended.Last is { } covered)
            {
                await _sessions
                    .StampCheckpointAsync(conversationId, required with { CoveredSequence = covered }, cancellationToken)
                    .ConfigureAwait(false);
            }

            // The heartbeat stops here, before the operation row is finalized. Completing reads the
            // row and then writes it, and a renewal that commits between those two steps moves the
            // row's concurrency token — the completion is refused, and the turn that answered
            // correctly is recorded as still running.
            await lease.QuiesceAsync().ConfigureAwait(false);

            var completed = await _operations.CompleteAsync(
                owner,
                operation.Id,
                LeaseOwnerId,
                claim.FencingVersion,
                SerializeOutcome(answer, _sessions.CurrentTurnDispute, _sessions.CurrentTurnGrounding),
                OutcomeSchemaVersion,
                appended.First,
                appended.Last,
                cancellationToken).ConfigureAwait(false);

            if (completed.Outcome == CoachTurnFinalizeOutcome.LeaseLost)
            {
                // Another worker took the slot. Its result is the one the learner will see, so
                // this one reports a conflict rather than racing to answer.
                return LeaseLost<CoachTurnOperationDto>();
            }

            var record = await SettledAsync(
                owner, completed, operation.Id, IsCompleted, nameof(ICoachTurnOperationStore.CompleteAsync), cancellationToken)
                .ConfigureAwait(false);

            if (record is null)
            {
                return Unsettled<CoachTurnOperationDto>();
            }

            return CoachOperationResult<CoachTurnOperationDto>.Ok(
                ToOperationDto(record, answer, appended.Messages));
        }
        catch (CoachTurnLeaseLostException)
        {
            // A fenced write was refused, so this worker is not the writer any more. The operation
            // row belongs to whoever took it over: failing it here would overwrite their state.
            return LeaseLost<CoachTurnOperationDto>();
        }
        catch (OperationCanceledException) when (lease.IsLeaseLost && !cancellationToken.IsCancellationRequested)
        {
            // The run unwound because the lease went, not because the learner left.
            return LeaseLost<CoachTurnOperationDto>();
        }
        catch (OperationCanceledException)
        {
            await lease.QuiesceAsync().ConfigureAwait(false);

            var abandoned = await _operations.FailAsync(
                owner, operation.Id, LeaseOwnerId, claim.FencingVersion, "cancelled", CancellationToken.None)
                .ConfigureAwait(false);

            WarnIfUnsettled(abandoned, operation.Id, nameof(ICoachTurnOperationStore.FailAsync));
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError("Coach durable turn failed: {Reason}", ex.GetType().Name);

            await lease.QuiesceAsync().ConfigureAwait(false);

            var broken = await _operations.FailAsync(
                owner, operation.Id, LeaseOwnerId, claim.FencingVersion, "turn_failed", CancellationToken.None)
                .ConfigureAwait(false);

            WarnIfUnsettled(broken, operation.Id, nameof(ICoachTurnOperationStore.FailAsync));
            throw;
        }
    }

    /// <summary>
    /// The answer a superseded worker gives. Never a success, and never the stale worker's own
    /// output: the worker that holds the lease is producing the reply the learner will see.
    /// </summary>
    private static CoachOperationResult<T> LeaseLost<T>() =>
        CoachOperationResult<T>.Problem(
            CoachOperationStatus.RunInProgress,
            CoachProblemTypes.RunInProgress,
            "That conversation is already running a turn.");

    /// <summary>
    /// The answer when the turn's work landed but its operation row could not be moved to a
    /// terminal state.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Deliberately not a success and deliberately not <see cref="LeaseLost{T}"/>. Reporting
    /// success would hand the client an operation that still says Running: it merges the answer,
    /// polls the row it was given, and gives up after its poll budget — a turn that worked,
    /// presented as a four-minute hang. Reporting a lease conflict would send the client to poll
    /// that same row.
    /// </para>
    /// <para>
    /// A recoverable conflict is the honest answer. Whatever the turn committed is in the ledger,
    /// so reloading the conversation shows it, and the operation row is left for the recovery pass
    /// to reconcile once its lease lapses.
    /// </para>
    /// </remarks>
    private static CoachOperationResult<T> Unsettled<T>() =>
        CoachOperationResult<T>.Problem(
            CoachOperationStatus.PlanChangedElsewhere,
            CoachProblemTypes.ConversationStateConflict,
            "That turn finished, but its record could not be settled. Reload the conversation to see it.");

    /// <summary>
    /// The operation row to answer from once a finalizing write has run, or null when the row is
    /// not in a state that lets this worker claim the turn is over.
    /// </summary>
    /// <remarks>
    /// A finalizing write contends for the operation row with this worker's own lease heartbeat
    /// and with a learner's cancel, so it can be refused for reasons that say nothing about
    /// ownership. The refusal is not the danger; answering from the copy of the row that was read
    /// <em>before</em> the write is, because that copy still says Running. This re-reads instead,
    /// and reports what is actually stored.
    /// </remarks>
    private async Task<CoachTurnOperationRecord?> SettledAsync(
        CoachOwner owner,
        CoachTurnFinalizeResult finalized,
        string operationId,
        Func<CoachTurnOperationStatus, bool> isSettled,
        string operationName,
        CancellationToken cancellationToken)
    {
        if (finalized.Outcome == CoachTurnFinalizeOutcome.Success
            && finalized.Operation is { } written
            && isSettled(written.Status))
        {
            return written;
        }

        var current = await _operations
            .GetAsync(owner, operationId, cancellationToken)
            .ConfigureAwait(false);

        if (current is not null && isSettled(current.Status))
        {
            // Settled by an earlier attempt of this same write, or by the recovery pass. Either
            // way the durable record says the turn is over, which is what the client needs.
            return current;
        }

        _logger.LogWarning(
            "[Coach] {Operation} left operation {OperationId} unsettled: outcome {Outcome}, stored status {Status}.",
            operationName,
            operationId,
            finalized.Outcome,
            current?.Status.ToString() ?? "missing");

        return null;
    }

    /// <summary>
    /// Records a finalizing write that did not land, on a path whose answer is already a problem.
    /// </summary>
    /// <remarks>
    /// The caller has nothing better to report — the turn failed either way — but a row left
    /// non-terminal holds the conversation until its lease lapses, and that is worth seeing in the
    /// logs rather than inferring from a learner's complaint that the coach went quiet.
    /// </remarks>
    private void WarnIfUnsettled(CoachTurnFinalizeResult finalized, string operationId, string operationName)
    {
        if (finalized.Outcome is CoachTurnFinalizeOutcome.Success or CoachTurnFinalizeOutcome.AlreadyTerminal)
        {
            return;
        }

        _logger.LogWarning(
            "[Coach] {Operation} did not settle operation {OperationId}: {Outcome}. The conversation stays held until its lease lapses.",
            operationName,
            operationId,
            finalized.Outcome);
    }

    private static bool IsCompleted(CoachTurnOperationStatus status) =>
        status == CoachTurnOperationStatus.Completed;

    private static bool IsTerminal(CoachTurnOperationStatus status) =>
        status is CoachTurnOperationStatus.Completed
               or CoachTurnOperationStatus.Failed
               or CoachTurnOperationStatus.Cancelled;

    /// <summary>
    /// Thrown when a fenced durable write is refused because the lease moved on.
    /// </summary>
    /// <remarks>
    /// An exception rather than a status return because it can surface from any of the several
    /// ledger writes a turn makes, and every one of them means the same thing: stop, do not write
    /// anything else, and do not report success.
    /// </remarks>
    private sealed class CoachTurnLeaseLostException : Exception
    {
        public CoachTurnLeaseLostException()
            : base("The turn's lease was taken over before the write completed.")
        {
        }
    }

    public async Task<CoachOperationResult<CoachTurnResponse>> RunCompatibilityDecisionAsync(
        string conversationId,
        CoachCompatibilityDecision decision,
        CancellationToken cancellationToken = default)
    {
        if (Gate<CoachTurnResponse>(out var owner) is { } denied)
        {
            return denied;
        }

        // A decision with no client key is a fresh request every time, which is exactly what the
        // old routes did. Inventing a stable key here would turn a learner's second deliberate
        // tap into a silent replay of the first.
        var key = string.IsNullOrWhiteSpace(decision.ClientTurnId)
            ? Guid.NewGuid().ToString("N")
            : CoachCompatibilityKeys.IdempotencyKey(conversationId, decision.ClientTurnId!);

        var operationId = CoachCompatibilityKeys.OperationId(conversationId, key);

        var claim = await _operations.ClaimAsync(
            owner,
            new ClaimCoachTurnRequest(
                conversationId,
                key,
                CanonicalDecision(conversationId, decision),
                LeaseOwnerId,
                LeaseDuration,
                operationId),
            cancellationToken).ConfigureAwait(false);

        switch (claim.Outcome)
        {
            case CoachTurnClaimOutcome.ConversationNotFound:
                return NotFound<CoachTurnResponse>();

            case CoachTurnClaimOutcome.PayloadConflict:
                // The same key answering a different suggestion. Refusing is the only safe answer:
                // applying it would act on a suggestion the learner did not look at.
                return CoachOperationResult<CoachTurnResponse>.Problem(
                    CoachOperationStatus.PlanChangedElsewhere,
                    CoachProblemTypes.IdempotencyConflict,
                    "That request id was already used for a different decision.");

            case CoachTurnClaimOutcome.InProgress:
            case CoachTurnClaimOutcome.ConversationBusy:
                return CoachOperationResult<CoachTurnResponse>.Problem(
                    CoachOperationStatus.RunInProgress,
                    CoachProblemTypes.RunInProgress,
                    "That conversation is already running a turn.");

            case CoachTurnClaimOutcome.ReplayCompleted:
            case CoachTurnClaimOutcome.ReplayTerminal:
            {
                var stored = DeserializeOutcome(claim.StoredOutcome, claim.StoredOutcomeSchemaVersion);
                if (stored is not null)
                {
                    return CoachOperationResult<CoachTurnResponse>.Ok(stored);
                }

                // A completed operation whose outcome cannot be read must not be re-run: the plan
                // write already landed. Refusing is honest; repeating it would double-apply.
                return CoachOperationResult<CoachTurnResponse>.Problem(
                    CoachOperationStatus.PlanChangedElsewhere,
                    CoachProblemTypes.IdempotencyConflict,
                    "That decision was already applied.");
            }

            case CoachTurnClaimOutcome.Claimed:
                break;

            default:
                return Unavailable<CoachTurnResponse>();
        }

        var operation = claim.Operation!;
        var fence = new CoachTurnFence(operation.Id, LeaseOwnerId, claim.FencingVersion);

        await using var lease = CoachTurnLeaseHeartbeat.Start(
            _leases, owner, fence, LeaseDuration, _clock, _logger, cancellationToken);

        try
        {
            var result = decision.Kind switch
            {
                CoachCompatibilityDecisionKind.AcceptSuggestion => await _sessions
                    .AcceptSuggestionAsync(
                        conversationId,
                        decision.SuggestionId ?? string.Empty,
                        new CoachSuggestionDecisionRequest { ClientTurnId = operation.Id },
                        lease.Token)
                    .ConfigureAwait(false),

                CoachCompatibilityDecisionKind.RejectSuggestion => await _sessions
                    .RejectSuggestionAsync(
                        conversationId,
                        decision.SuggestionId ?? string.Empty,
                        new CoachSuggestionDecisionRequest { ClientTurnId = operation.Id },
                        lease.Token)
                    .ConfigureAwait(false),

                _ => await _sessions
                    .UndoAsync(
                        conversationId,
                        new CoachUndoRequest { ClientTurnId = operation.Id },
                        lease.Token)
                    .ConfigureAwait(false)
            };

            if (!result.IsOk || result.Value is null)
            {
                await lease.QuiesceAsync().ConfigureAwait(false);

                var abandoned = await _operations.FailAsync(
                    owner,
                    operation.Id,
                    LeaseOwnerId,
                    claim.FencingVersion,
                    Clamp(result.Status.ToString(), CoachHistoryLimits.ErrorCodeMaxLength),
                    cancellationToken).ConfigureAwait(false);

                WarnIfUnsettled(abandoned, operation.Id, nameof(ICoachTurnOperationStore.FailAsync));

                return result;
            }

            var appended = await AppendResponseAsync(owner, conversationId, fence, result.Value, cancellationToken)
                .ConfigureAwait(false);

            var answer = WithLedgerIdentity(result.Value, appended.Messages);

            if (appended.Last is { } covered)
            {
                var required = _sessions.CheckpointIdentity(conversationId, covered);
                await _sessions
                    .StampCheckpointAsync(conversationId, required, cancellationToken)
                    .ConfigureAwait(false);
            }

            // The heartbeat stops before the row is finalized, for the same reason the durable
            // turn path stops it: a renewal committing between this completion's read and its
            // write refuses the completion and leaves the conversation held by a lease nobody is
            // renewing, so the learner's next decision is told the conversation is busy.
            await lease.QuiesceAsync().ConfigureAwait(false);

            var completed = await _operations.CompleteAsync(
                owner,
                operation.Id,
                LeaseOwnerId,
                claim.FencingVersion,
                SerializeOutcome(answer, _sessions.CurrentTurnDispute, _sessions.CurrentTurnGrounding),
                OutcomeSchemaVersion,
                appended.First,
                appended.Last,
                cancellationToken).ConfigureAwait(false);

            if (completed.Outcome == CoachTurnFinalizeOutcome.LeaseLost)
            {
                return LeaseLost<CoachTurnResponse>();
            }

            var settled = await SettledAsync(
                owner, completed, operation.Id, IsCompleted, nameof(ICoachTurnOperationStore.CompleteAsync), cancellationToken)
                .ConfigureAwait(false);

            if (settled is null)
            {
                return Unsettled<CoachTurnResponse>();
            }

            return CoachOperationResult<CoachTurnResponse>.Ok(answer);
        }
        catch (CoachTurnLeaseLostException)
        {
            return LeaseLost<CoachTurnResponse>();
        }
        catch (OperationCanceledException) when (lease.IsLeaseLost && !cancellationToken.IsCancellationRequested)
        {
            return LeaseLost<CoachTurnResponse>();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError("Coach compatibility decision failed: {Reason}", ex.GetType().Name);

            await lease.QuiesceAsync().ConfigureAwait(false);

            var broken = await _operations.FailAsync(
                owner, operation.Id, LeaseOwnerId, claim.FencingVersion, "decision_failed", CancellationToken.None)
                .ConfigureAwait(false);

            WarnIfUnsettled(broken, operation.Id, nameof(ICoachTurnOperationStore.FailAsync));
            throw;
        }
    }

    /// <summary>
    /// The canonical bytes a decision digests to. Distinct from a turn's, so a decision and a
    /// text turn sharing a client id can never be mistaken for replays of each other.
    /// </summary>
    private static string CanonicalDecision(string conversationId, CoachCompatibilityDecision decision) =>
        string.Join(
            '\u001f',
            "decision",
            conversationId,
            decision.Kind.ToString(),
            decision.SuggestionId ?? string.Empty);

    /// <summary>
    /// Writes the learner's own words to the ledger before the model is called, so a crash during
    /// the model call still leaves the conversation showing what the learner said.
    /// </summary>
    private async Task<long?> AppendLearnerMessageAsync(
        CoachOwner owner,
        string conversationId,
        CoachTurnOperationRecord operation,
        CoachTurnFence fence,
        CoachTurnRequest turn,
        CancellationToken cancellationToken)
    {
        var operationId = operation.Id;
        var text = turn.Text;
        if (string.IsNullOrWhiteSpace(text))
        {
            // A chip tap or a constraint action carries no learner prose. There is nothing
            // truthful to write, and inventing a line would put words in the learner's mouth.
            return null;
        }

        if (operation.AttemptCount > 1)
        {
            // A previous attempt died somewhere after this point, so the learner's line may
            // already be in the ledger. Writing it again would show the learner saying the same
            // thing twice for a message they only sent once.
            var existing = await ReadOperationMessagesAsync(owner, conversationId, operationId, cancellationToken)
                .ConfigureAwait(false);

            var already = existing.FirstOrDefault(m => m.Role == CoachMessageRole.Learner);
            if (already is not null)
            {
                return already.Sequence;
            }
        }

        var payload = CoachHistoryProjection.LearnerMessage(text, _clock.GetUtcNow().UtcDateTime);

        var appended = await _messages.AppendAsync(
            owner,
            new AppendCoachMessageRequest(
                conversationId,
                CoachMessageRole.Learner,
                CoachMessageKind.Text,
                payload,
                operationId,
                MessageId: null,
                Fence: fence),
            cancellationToken).ConfigureAwait(false);

        if (appended.Status == CoachHistoryStatus.LeaseLost)
        {
            throw new CoachTurnLeaseLostException();
        }

        if (appended.Status != CoachHistoryStatus.Success)
        {
            throw new InvalidOperationException("The learner message could not be appended to the coach ledger.");
        }

        return appended.Message?.Sequence;
    }

    /// <summary>
    /// Appends the validated, learner-visible half of a turn in the order the reducer produced
    /// it. Provider-hidden content and tool traces never reach this method.
    /// </summary>
    /// <remarks>
    /// Every append carries the caller's fence, so a worker that has been superseded cannot put a
    /// second copy of the answer in front of the learner. The check is made by the same statement
    /// that admits the write, which is what closes the window between "I still held the lease a
    /// moment ago" and "I am writing now".
    /// </remarks>
    private async Task<(long? First, long? Last, IReadOnlyList<CoachHistoryMessageDto> Messages)> AppendResponseAsync(
        CoachOwner owner,
        string conversationId,
        CoachTurnFence fence,
        CoachTurnResponse response,
        CancellationToken cancellationToken)
    {
        long? first = null;
        long? last = null;
        var written = new List<CoachHistoryMessageDto>();

        foreach (var payload in CoachHistoryProjection.ResponseMessages(response))
        {
            var appended = await _messages.AppendAsync(
                owner,
                new AppendCoachMessageRequest(
                    conversationId,
                    payload.Kind == CoachMessagePayloadKind.LearnerText
                        ? CoachMessageRole.Learner
                        : CoachMessageRole.Coach,
                    CoachHistoryProjection.KindFor(payload.Kind),
                    payload,
                    fence.OperationId,
                    MessageId: null,
                    Fence: fence),
                cancellationToken).ConfigureAwait(false);

            if (appended.Status == CoachHistoryStatus.LeaseLost)
            {
                // Refused mid-response is the same verdict as refused on the first line: this
                // worker is not the writer. Anything it already appended belongs to the operation
                // the winner is reconciling, and the winner reads the ledger rather than this
                // worker's return value.
                throw new CoachTurnLeaseLostException();
            }

            if (appended.Status != CoachHistoryStatus.Success || appended.Message is null)
            {
                throw new InvalidOperationException("A coach response message could not be appended to the ledger.");
            }

            first ??= appended.Message.Sequence;
            last = appended.Message.Sequence;
            written.Add(CoachHistoryProjection.ToHistoryMessage(appended.Message));
        }

        return (first, last, written);
    }

    /// <summary>
    /// Returns the response the learner should receive, with its message list replaced by the rows
    /// the ledger committed.
    /// </summary>
    /// <remarks>
    /// The reducers mint a fresh identifier and a caller-clock timestamp for every message they
    /// build, because they predate the ledger and had nothing durable to point at. Returning those
    /// would hand the client one identity for the message it just received and a different one for
    /// the same message on the next page load. The ledger row is the authority for both, and its
    /// text is the clamped text a reload will actually show, so the live answer and the reloaded
    /// answer are byte-identical.
    /// <para>
    /// Alignment is positional and only applied when the counts agree. They agree by construction —
    /// <see cref="CoachHistoryProjection.ResponseMessages"/> walks the same list in the same order,
    /// dropping only the learner echo that was appended before the model ran — but a future change
    /// that breaks that pairing should leave the response untouched rather than silently attach one
    /// message's identity to another message's text.
    /// </para>
    /// </remarks>
    private static CoachTurnResponse WithLedgerIdentity(
        CoachTurnResponse response,
        IReadOnlyList<CoachHistoryMessageDto> appended)
    {
        var committed = appended.Select(m => m.Message).ToArray();

        return committed.Length == response.Messages.Count
            ? response.WithMessages(committed)
            : response;
    }

    /// <summary>
    /// Builds the bounded, role-tagged history a rebuilt agent session starts from.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Only learner text and coach prose are included. Receipts, notices, and suggestion
    /// snapshots are server-authored artefacts of an earlier turn; replaying them would let a
    /// past decision read as a fresh instruction. Everything here is conversation data, never
    /// instruction, and the prompt layer labels it that way.
    /// </para>
    /// <para>
    /// History stops strictly below <paramref name="upperExclusiveSequence"/>, which is the ledger
    /// row holding the message this turn is answering. That row is committed before the checkpoint
    /// is consulted, so an unbounded read hands the model the same sentence twice: once under
    /// EARLIER IN THIS CONVERSATION, where it reads as something already said and dealt with, and
    /// again under LEARNER MESSAGE, where it is the thing to answer. It also spent a slot of the
    /// message cap and its share of the character budget, evicting the oldest real turn to pay for
    /// a copy of text the model was about to be shown anyway.
    /// </para>
    /// <para>
    /// The bound is the sequence this turn's own append returned, not the ledger head read a moment
    /// earlier. They differ on a retry: the dead attempt already wrote the learner row, so the head
    /// a recovering attempt reads <em>includes</em> the message being answered, and bounding on it
    /// would replay exactly what this exists to exclude.
    /// </para>
    /// </remarks>
    private async Task<IReadOnlyList<CoachPriorMessage>> BuildPriorMessagesAsync(
        CoachOwner owner,
        string conversationId,
        long upperExclusiveSequence,
        CancellationToken cancellationToken)
    {
        // Bounded at the database rather than by discarding rows afterwards: the cap means "the
        // newest fifty messages before this one", and filtering a newest-fifty page would return
        // forty-nine of them.
        var page = await _messages
            .GetBeforeSequenceAsync(owner, conversationId, upperExclusiveSequence, RebuildMessageCap, cancellationToken)
            .ConfigureAwait(false);

        if (page.Status != CoachHistoryStatus.Success || page.Items.Count == 0)
        {
            return Array.Empty<CoachPriorMessage>();
        }

        var selected = new List<CoachPriorMessage>();
        var budget = RebuildCharacterCap;

        // Walk newest-first so the character cap drops the oldest turns, then restore order.
        for (var i = page.Items.Count - 1; i >= 0; i--)
        {
            var record = page.Items[i];
            if (record.Payload is not { } payload)
            {
                continue;
            }

            if (payload.Kind is not (CoachMessagePayloadKind.LearnerText or CoachMessagePayloadKind.CoachText or CoachMessagePayloadKind.StructuredAnswer))
            {
                continue;
            }

            var text = payload.Kind == CoachMessagePayloadKind.StructuredAnswer && payload.Answer is { } answer
                ? answer.PlainText
                : payload.Text;

            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            if (text.Length > budget)
            {
                break;
            }

            budget -= text.Length;
            selected.Add(new CoachPriorMessage(record.Role, text));
        }

        selected.Reverse();
        return selected;
    }

    private async Task<bool> IsCancelRequestedAsync(
        CoachOwner owner,
        string operationId,
        CancellationToken cancellationToken)
    {
        var current = await _operations.GetAsync(owner, operationId, cancellationToken).ConfigureAwait(false);
        return current?.CancelRequested == true;
    }

    /// <summary>
    /// Ends a turn that was cancelled before the model ran: a visible notice, no plan effect, no
    /// budget charge.
    /// </summary>
    private async Task<CoachOperationResult<CoachTurnOperationDto>> CancelTurnAsync(
        CoachOwner owner,
        string conversationId,
        CoachTurnOperationRecord operation,
        CoachTurnFence fence,
        CoachTurnLeaseHeartbeat lease,
        CancellationToken cancellationToken)
    {
        var notice = await _messages.AppendAsync(
            owner,
            new AppendCoachMessageRequest(
                conversationId,
                CoachMessageRole.Coach,
                CoachMessageKind.Notice,
                CancellationNotice(_clock.GetUtcNow().UtcDateTime),
                operation.Id,
                MessageId: null,
                Fence: fence),
            cancellationToken).ConfigureAwait(false);

        if (notice.Status == CoachHistoryStatus.LeaseLost)
        {
            // A superseded worker must not narrate the turn either. The worker that holds the
            // lease will say what happened.
            throw new CoachTurnLeaseLostException();
        }

        // Same reason as a completion: the cancelling write reads the row before it writes it, and
        // a renewal landing in between would refuse it and leave a cancelled turn recorded as
        // running.
        await lease.QuiesceAsync().ConfigureAwait(false);

        var cancelled = await _operations.FailAsync(
            owner, operation.Id, LeaseOwnerId, fence.FencingVersion, "cancelled", cancellationToken)
            .ConfigureAwait(false);

        var record = await SettledAsync(
            owner, cancelled, operation.Id, IsTerminal, nameof(ICoachTurnOperationStore.FailAsync), cancellationToken)
            .ConfigureAwait(false);

        if (record is null)
        {
            return Unsettled<CoachTurnOperationDto>();
        }

        var messages = notice.Message is null
            ? Array.Empty<CoachHistoryMessageDto>()
            : new[] { CoachHistoryProjection.ToHistoryMessage(notice.Message) };

        return CoachOperationResult<CoachTurnOperationDto>.Ok(ToOperationDto(record, result: null, messages));
    }

    /// <summary>
    /// Recovers a turn whose previous attempt died mid-saga, or returns null when there is
    /// nothing to recover and the turn may safely run again.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The plan and the conversation ledger live in separate contexts, so a crash can land
    /// between them. This closes that window by reading what the dead attempt left behind rather
    /// than repeating the work to find out — repeating a plan write to discover whether a plan
    /// write happened is exactly the duplicate this exists to prevent.
    /// </para>
    /// <para>
    /// Two recoverable shapes:
    /// </para>
    /// <list type="number">
    /// <item>
    /// Coach output is already in the ledger. Everything ran; only the completion was lost.
    /// Complete the operation from the committed ledger and answer with it.
    /// </item>
    /// <item>
    /// The plan recorded a revision but no coach output reached the ledger. The effect happened
    /// and the account of it did not. Append a notice describing the applied change and complete,
    /// so the learner sees the effect they caused instead of a turn that looks like it never ran.
    /// </item>
    /// </list>
    /// <para>
    /// Anything else means the attempt died before it changed anything, and re-running is safe.
    /// </para>
    /// </remarks>
    private async Task<CoachOperationResult<CoachTurnOperationDto>?> ReconcileAsync(
        CoachOwner owner,
        string conversationId,
        CoachTurnOperationRecord operation,
        CoachTurnFence fence,
        CoachTurnLeaseHeartbeat lease,
        CancellationToken cancellationToken)
    {
        var written = await ReadOperationMessagesAsync(owner, conversationId, operation.Id, cancellationToken)
            .ConfigureAwait(false);

        var coachOutput = written.Where(m => m.Role == CoachMessageRole.Coach).ToList();

        if (coachOutput.Count > 0)
        {
            return await FinishReconciledAsync(
                owner, operation, fence.FencingVersion, coachOutput, lease, cancellationToken).ConfigureAwait(false);
        }

        // Exact, not approximate. The revision this operation wrote carries the operation's own
        // id, so there is no window to widen and no neighbouring conversation's revision to
        // mistake for this one.
        var revision = await _sessions
            .GetRevisionByOperationAsync(operation.Id, cancellationToken)
            .ConfigureAwait(false);

        if (revision is null)
        {
            // Nothing was applied and nothing was said. The turn never got past the model call,
            // so running it again produces one effect, not two.
            return null;
        }

        // The plan moved but the account of it was lost. Rebuild the receipt from the revision
        // itself: the learner caused a change, and telling them so vaguely — "something was
        // applied" — would be a worse answer than the one the durable record can actually give.
        var receipt = await _messages.AppendAsync(
            owner,
            new AppendCoachMessageRequest(
                conversationId,
                CoachMessageRole.Coach,
                CoachMessageKind.Receipt,
                RecoveredReceipt(revision, _clock.GetUtcNow().UtcDateTime),
                operation.Id,
                MessageId: null,
                Fence: fence),
            cancellationToken).ConfigureAwait(false);

        if (receipt.Status == CoachHistoryStatus.LeaseLost)
        {
            throw new CoachTurnLeaseLostException();
        }

        var recovered = receipt.Message is null
            ? Array.Empty<CoachMessageRecord>()
            : new[] { receipt.Message };

        return await FinishReconciledAsync(
            owner, operation, fence.FencingVersion, recovered, lease, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Copies a turn request, replacing the client turn id with the durable operation id.
    /// </summary>
    /// <remarks>
    /// Copied rather than mutated because the request is a caller-owned contract object, and
    /// copied by hand because that contract is a class rather than a record — deliberately, since
    /// it is a wire shape whose value equality nothing depends on. If a member is added to
    /// <see cref="CoachTurnRequest"/> it must be added here too, so a plan write cannot silently
    /// lose the field that correlates it to its operation.
    /// </remarks>
    private static CoachTurnRequest WithOperationId(CoachTurnRequest request, string operationId) => new()
    {
        InputKind = request.InputKind,
        Text = request.Text,
        ChipId = request.ChipId,
        ConstraintAction = request.ConstraintAction,
        PendingSuggestionId = request.PendingSuggestionId,
        ExpectedPlanVersion = request.ExpectedPlanVersion,
        ClientTurnId = operationId
    };

    /// <summary>
    /// Rebuilds the receipt for a change that was committed by an attempt which then died.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Built from the durable revision rather than from a replayed turn, because replaying the
    /// turn is precisely what must not happen: the plan write already landed, and running it
    /// again would apply the change twice.
    /// </para>
    /// <para>
    /// The revision stores the normalized delta and the preservation counts, which is everything
    /// the learner-visible receipt needs. It cannot reproduce the full diff, so the receipt says
    /// what changed and what was kept without inventing a before-and-after it no longer has.
    /// </para>
    /// </remarks>
    private static CoachMessagePayload RecoveredReceipt(CoachPlanRevision revision, DateTime createdAtUtc)
    {
        var delta = CoachNormalizedJson.Deserialize<CoachConstraintDeltaDto>(revision.AcceptedConstraintDeltaJson)
                    ?? new CoachConstraintDeltaDto();
        var summary = CoachConstraintMapper.Summarize(delta);

        var lines = new List<string>();
        if (revision.PreservedCompletedCount > 0)
        {
            lines.Add($"Kept {revision.PreservedCompletedCount} completed items");
        }

        if (revision.PreservedInProgressCount > 0)
        {
            lines.Add($"Kept {revision.PreservedInProgressCount} started items");
        }

        return new CoachMessagePayload
        {
            Kind = CoachMessagePayloadKind.Receipt,
            CreatedAtUtc = createdAtUtc,
            Text = summary,
            Receipt = new CoachStoredReceipt
            {
                ReceiptId = revision.Id,
                RevisionId = revision.Id,
                Summary = summary,
                ChangeLines = lines
            }
        };
    }

    /// <summary>Completes a recovered operation from what is already committed.</summary>
    private async Task<CoachOperationResult<CoachTurnOperationDto>> FinishReconciledAsync(
        CoachOwner owner,
        CoachTurnOperationRecord operation,
        long fencingVersion,
        IReadOnlyList<CoachMessageRecord> committed,
        CoachTurnLeaseHeartbeat lease,
        CancellationToken cancellationToken)
    {
        var first = committed.Count == 0 ? (long?)null : committed.Min(m => m.Sequence);
        var last = committed.Count == 0 ? (long?)null : committed.Max(m => m.Sequence);

        // Recovery ends the turn here, so the heartbeat has nothing left to keep alive and must
        // not be writing the row this completion is about to read.
        await lease.QuiesceAsync().ConfigureAwait(false);

        var completed = await _operations.CompleteAsync(
            owner,
            operation.Id,
            LeaseOwnerId,
            fencingVersion,
            // No stored outcome: the response object died with the attempt that built it. The
            // ledger is the surviving truth, and it is what the client is answered from.
            outcomePayload: null,
            outcomeSchemaVersion: OutcomeSchemaVersion,
            first,
            last,
            cancellationToken).ConfigureAwait(false);

        if (completed.Outcome == CoachTurnFinalizeOutcome.LeaseLost)
        {
            // A third worker is recovering this operation and holds the lease. Its account is the
            // one the learner will read.
            throw new CoachTurnLeaseLostException();
        }

        var record = await SettledAsync(
            owner, completed, operation.Id, IsCompleted, nameof(ICoachTurnOperationStore.CompleteAsync), cancellationToken)
            .ConfigureAwait(false);

        if (record is null)
        {
            return Unsettled<CoachTurnOperationDto>();
        }

        return CoachOperationResult<CoachTurnOperationDto>.Ok(
            ToOperationDto(
                record,
                result: null,
                await ProjectWithWritesAsync(record.ConversationId, committed, cancellationToken)
                    .ConfigureAwait(false)));
    }

    /// <summary>Every ledger message this operation wrote, in order.</summary>
    private async Task<IReadOnlyList<CoachMessageRecord>> ReadOperationMessagesAsync(
        CoachOwner owner,
        string conversationId,
        string operationId,
        CancellationToken cancellationToken)
    {
        // A turn writes a handful of messages, so the newest page always contains them all.
        var page = await _messages
            .GetLatestAsync(owner, conversationId, CoachHistoryLimits.MessagePageMax, cancellationToken)
            .ConfigureAwait(false);

        return page.Status != CoachHistoryStatus.Success
            ? Array.Empty<CoachMessageRecord>()
            : page.Items
                .Where(m => string.Equals(m.OperationId, operationId, StringComparison.Ordinal))
                .OrderBy(m => m.Sequence)
                .ToList();
    }

    /// <summary>
    /// The highest sequence the ledger has assigned for this conversation, which is what a
    /// trustworthy checkpoint must already cover.
    /// </summary>
    private async Task<long> LedgerHeadAsync(
        CoachOwner owner,
        string conversationId,
        CancellationToken cancellationToken)
    {
        var record = await _conversations.GetAsync(owner, conversationId, cancellationToken).ConfigureAwait(false);
        return record.Conversation?.LastSequence ?? 0;
    }

    /// <summary>
    /// The canonical request bytes the store digests. Field order is fixed here, not left to
    /// serializer defaults, so the same logical request always produces the same digest.
    /// </summary>
    private static string CanonicalRequest(string conversationId, CoachTurnRequest turn) =>
        string.Join(
            '\u001f',
            conversationId,
            turn.InputKind.ToString(),
            turn.Text ?? string.Empty,
            turn.ChipId ?? string.Empty,
            turn.PendingSuggestionId ?? string.Empty,
            turn.ExpectedPlanVersion ?? string.Empty,
            turn.ConstraintAction is null ? string.Empty : JsonSerializer.Serialize(turn.ConstraintAction, OutcomeJson));

    private CoachOperationResult<T>? Gate<T>(out CoachOwner owner)
    {
        if (!TryOwner(out owner))
        {
            return Unavailable<T>();
        }

        if (!IsEnabled)
        {
            return Unavailable<T>();
        }

        return null;
    }

    private bool TryOwner(out CoachOwner owner)
    {
        if (!_userScope.TryGetUserProfileId(out var userProfileId))
        {
            throw new UnauthorizedAccessException("The request has no user profile scope.");
        }

        return CoachOwner.TryCreate(userProfileId, null, out owner);
    }

    private async Task<bool> HasActiveCheckpointAsync(string conversationId, CancellationToken cancellationToken)
    {
        var session = await _sessions.GetSessionAsync(conversationId, cancellationToken).ConfigureAwait(false);
        return session.IsOk;
    }

    private CoachOperationResult<CoachConversationDto> Ok(CoachConversationRecord record, bool hasActiveCheckpoint) =>
        CoachOperationResult<CoachConversationDto>.Ok(
            CoachHistoryProjection.ToConversation(record, hasActiveCheckpoint));

    private static CoachOperationResult<T> Unavailable<T>() =>
        CoachOperationResult<T>.Problem(
            CoachOperationStatus.Unavailable,
            CoachProblemTypes.Unavailable,
            "The coach history is not available.");

    private static CoachOperationResult<T> NotFound<T>() =>
        CoachOperationResult<T>.Problem(
            CoachOperationStatus.SessionNotFound,
            CoachProblemTypes.ConversationNotFound,
            "That conversation was not found.");

    private static CoachOperationResult<T> Conflict<T>() =>
        CoachOperationResult<T>.Problem(
            CoachOperationStatus.PlanChangedElsewhere,
            CoachProblemTypes.ConversationStateConflict,
            "That conversation changed somewhere else. Reload it and try again.");

    /// <summary>
    /// A generic, date-based title. No model call is made to name a conversation: a title
    /// generated from the learner's first message is a second inference on private text, and
    /// renaming is one tap away.
    /// </summary>
    private string FallbackTitle() =>
        CoachHistoryTitles.Fallback(DateOnly.FromDateTime(_clock.GetUtcNow().UtcDateTime));

    private static string? NormalizeLanguage(string? code) =>
        string.IsNullOrWhiteSpace(code)
            ? null
            : Clamp(code.Trim(), CoachHistoryLimits.TargetLanguageCodeMaxLength);

    /// <summary>
    /// The visible line a cancelled turn leaves behind. Written by the server, in the server's
    /// own voice, so the timeline explains the gap instead of simply ending.
    /// </summary>
    /// <summary>
    /// The notice a recovered turn leaves when the plan change landed but the account of it did
    /// not. It says what is true and no more: the change happened, the explanation is gone.
    /// </summary>
    private static CoachMessagePayload ReconciledNotice(DateTime createdAtUtc) => new()
    {
        Kind = CoachMessagePayloadKind.Notice,
        CreatedAtUtc = createdAtUtc,
        Text = "Your plan change was applied, but the coach's reply was lost. Nothing was applied twice.",
        Notice = new CoachStoredNotice
        {
            ReasonCode = CoachNoticeReasonCodes.Recovered,
            Text = "Your plan change was applied, but the coach's reply was lost. Nothing was applied twice."
        }
    };

    private static CoachMessagePayload CancellationNotice(DateTime createdAtUtc) => new()
    {
        Kind = CoachMessagePayloadKind.Notice,
        CreatedAtUtc = createdAtUtc,
        Text = "That turn was stopped, so nothing was changed.",
        Notice = new CoachStoredNotice
        {
            ReasonCode = CoachNoticeReasonCodes.Cancelled,
            Text = "That turn was stopped, so nothing was changed."
        }
    };

    private static string BuildLeaseOwnerId()
    {
        var raw = string.Concat(Environment.MachineName, ":", Environment.ProcessId.ToString(), ":", Guid.NewGuid().ToString("N")[..8]);
        return Clamp(raw, CoachHistoryLimits.LeaseOwnerMaxLength);
    }

    private static string Clamp(string value, int max) =>
        value.Length <= max ? value : value[..max];

    /// <summary>
    /// Derives a stable conversation id from the owner and the client's idempotency key, so the
    /// same create request always names the same conversation without the key being stored.
    /// </summary>
    private static string DeriveConversationId(CoachOwner owner, string idempotencyKey)
    {
        var material = Encoding.UTF8.GetBytes(
            string.Concat(owner.UserProfileId, "\u001f", "conversation", "\u001f", idempotencyKey.Trim()));
        var digest = SHA256.HashData(material);
        return Convert.ToHexString(digest, 0, 16).ToLowerInvariant();
    }

    private CoachTurnOperationDto ToOperationDto(
        CoachTurnOperationRecord record,
        CoachTurnResponse? result,
        IReadOnlyList<CoachHistoryMessageDto>? messages = null) =>
        new()
        {
            OperationId = record.Id,
            ConversationId = record.ConversationId,
            State = record.Status switch
            {
                CoachTurnOperationStatus.Pending => CoachTurnOperationState.Pending,
                CoachTurnOperationStatus.Running => CoachTurnOperationState.Running,
                CoachTurnOperationStatus.Completed => CoachTurnOperationState.Completed,
                CoachTurnOperationStatus.Cancelled => CoachTurnOperationState.Cancelled,
                _ => CoachTurnOperationState.Failed
            },
            CancelRequested = record.CancelRequested,
            Result = result,
            Messages = messages ?? Array.Empty<CoachHistoryMessageDto>(),
            FirstResponseSequence = record.FirstResponseSequence,
            LastResponseSequence = record.LastResponseSequence,
            ErrorCode = record.ErrorCode,
            CreatedAtUtc = record.CreatedAt,
            UpdatedAtUtc = record.UpdatedAt
        };

    /// <summary>
    /// Reads back exactly the messages one turn appended, so a poll after a dropped response
    /// shows the same timeline the submit would have returned.
    /// </summary>
    private async Task<IReadOnlyList<CoachHistoryMessageDto>> ReadTurnMessagesAsync(
        CoachOwner owner,
        CoachTurnOperationRecord record,
        CancellationToken cancellationToken)
    {
        if (record.FirstResponseSequence is not { } first || record.LastResponseSequence is not { } last)
        {
            return Array.Empty<CoachHistoryMessageDto>();
        }

        var page = await _messages
            .GetRangeAsync(owner, record.ConversationId, first, last, cancellationToken)
            .ConfigureAwait(false);

        return page.Status == CoachHistoryStatus.Success
            ? await ProjectWithWritesAsync(record.ConversationId, page.Items, cancellationToken)
                .ConfigureAwait(false)
            : Array.Empty<CoachHistoryMessageDto>();
    }

    /// <summary>
    /// Projects a page of stored rows, attaching each turn's proposed change to the message that
    /// announced it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is what makes a proposal card survive a reload, a route change, and a new device. The
    /// card is not client state kept alive across navigation; it is rebuilt from the ledger every
    /// time the thread is read, in the exchange that produced it, carrying whatever the server
    /// says is true now.
    /// </para>
    /// <para>
    /// The pairing is exact rather than heuristic. A message row records the turn operation that
    /// wrote it, and a write proposal records the turn it was proposed in — the same identifier,
    /// because the durable turn's own id is what the turn pipeline hands the write scope. The
    /// anchor is the last message of the turn, so the card reads after what Sam said rather than
    /// beside the learner's question.
    /// </para>
    /// <para>
    /// A ledger read that fails degrades to a page without cards rather than to a failed history
    /// read. A learner who cannot see a proposal has lost a control; a learner who cannot load
    /// their conversation has lost the conversation.
    /// </para>
    /// </remarks>
    private async Task<CoachHistoryMessageDto[]> ProjectWithWritesAsync(
        string conversationId,
        IReadOnlyList<CoachMessageRecord> records,
        CancellationToken cancellationToken)
    {
        if (_writeLedger is null || records.Count == 0)
        {
            return records.Select(record => CoachHistoryProjection.ToHistoryMessage(record)).ToArray();
        }

        var turnIds = records
            .Select(record => record.OperationId)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id!)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        IReadOnlyList<CoachWriteOperationDto> writes = Array.Empty<CoachWriteOperationDto>();

        if (turnIds.Length > 0)
        {
            try
            {
                writes = await _writeLedger
                    .ListForTurnsAsync(conversationId, turnIds, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    "[Coach] Proposed changes for conversation {ConversationId} could not be read: {Failure}.",
                    conversationId,
                    CoachExceptionSanitizer.Describe(ex));
            }
        }

        var byIndex = CoachWriteAnchoring.ByMessage(records, writes);

        var items = new CoachHistoryMessageDto[records.Count];
        for (var i = 0; i < records.Count; i++)
        {
            var write = byIndex.TryGetValue(i, out var found) ? found : null;
            items[i] = CoachHistoryProjection.ToHistoryMessage(
                records[i],
                write is null ? null : CoachWriteAnchoring.Anchored(write, records[i].Id));
        }

        return items;
    }



    /// <summary>
    /// The stored payload for a completed turn: the answer, plus this turn's trace when there is
    /// one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The trace is <em>projected</em> from the in-memory observations, never serialized from them.
    /// What crosses this line is closed codes, counts and durations; the <c>CoachResultScope</c>
    /// objects the observations carry stop here.
    /// </para>
    /// <para>
    /// Projection failure must not cost the learner their answer. A trace is a diagnostic; the
    /// answer is the turn. So the projection is guarded and a failure writes the answer alone,
    /// with a content-free warning.
    /// </para>
    /// </remarks>
    /// <summary>How many recent turns the correction load will look back over.</summary>
    /// <remarks>
    /// Three, because a dispute that is still open three answers later is a dispute the coach is
    /// failing to resolve rather than one the learner is still waiting on, and because this read
    /// decrypts every row it touches and sits on the front of every turn. The store clamps it
    /// again; this is the number that expresses the intent.
    /// </remarks>
    private const int DisputeLookbackTurns = 3;

    /// <summary>
    /// What the correction state needs from durable history, read once before the turn runs.
    /// </summary>
    /// <param name="ActiveDispute">The open correction, or null.</param>
    /// <param name="PriorCoachMessageId">The exact coach message a new correction would anchor to.</param>
    /// <param name="PriorTrace">That turn's content-free trace.</param>
    private readonly record struct CoachDisputeContext(
        CoachTurnDisputeState? ActiveDispute,
        string? PriorCoachMessageId,
        CoachTurnTraceSummary? PriorTrace);

    /// <summary>
    /// Loads the correction state for one conversation.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Owner and conversation scoped, and bounded.</b> The store's owned set does the tenant
    /// filtering, so an empty owner reads nothing rather than everything, and the lookback is a
    /// small constant so a long conversation does not put unbounded decryption on the front of a
    /// turn. A dispute is never carried across conversations or owners: one learner disagreeing in
    /// one thread must not constrain an answer in another.
    /// </para>
    /// <para>
    /// <b>Fail closed.</b> Every failure mode here — the flag off, no history store, an unreadable
    /// payload, an unknown schema version, a throw — yields no dispute. A correction the server
    /// cannot read is a correction it cannot honour, and pretending otherwise would constrain an
    /// answer against a claim nobody can name.
    /// </para>
    /// <para>
    /// <b>No new table.</b> This reads the protected outcomes that already exist, through the same
    /// decoder the replay path uses, so retention and erasure need no second story: when the
    /// operation rows go, the disputes go with them.
    /// </para>
    /// </remarks>
    private async Task<CoachDisputeContext> LoadDisputeContextAsync(
        CoachOwner owner,
        string conversationId,
        CancellationToken cancellationToken)
    {
        if (_disputes is null || !_disputes.IsEnabled)
        {
            return default;
        }

        try
        {
            var outcomes = await _operations
                .GetRecentOutcomesAsync(owner, conversationId, DisputeLookbackTurns, cancellationToken)
                .ConfigureAwait(false);

            CoachTurnDisputeState? active = null;
            CoachTurnTraceSummary? priorTrace = null;

            // Newest first. The first readable outcome supplies the trace a new dispute would carry;
            // the first *open* dispute is the one still in force.
            foreach (var outcome in outcomes)
            {
                var stored = ReadOutcome(outcome.Payload, outcome.SchemaVersion);
                if (stored is null)
                {
                    // An unreadable or unknown-version row degrades this one turn's dispute view and
                    // nothing else. Answer and trace are read by their own callers, unaffected.
                    continue;
                }

                priorTrace ??= stored.Trace;

                if (active is null && stored.Dispute is { IsOpen: true } open)
                {
                    active = open;
                }

                if (active is not null && priorTrace is not null)
                {
                    break;
                }
            }

            var priorCoachMessageId = await FindPriorCoachMessageIdAsync(
                owner, conversationId, cancellationToken).ConfigureAwait(false);

            return new CoachDisputeContext(active, priorCoachMessageId, priorTrace);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(
                "[Coach] The correction state could not be loaded and was treated as absent. {Failure}",
                CoachExceptionSanitizer.Describe(ex));

            return default;
        }
    }

    /// <summary>
    /// The ledger id of the most recent coach message, which is what a new dispute anchors to.
    /// </summary>
    /// <remarks>
    /// The <em>coach</em> message specifically, not the newest message: the learner's correction is
    /// the newest row by the time this runs, and anchoring a dispute to the learner's own sentence
    /// would key it to something the coach never claimed.
    /// </remarks>
    private async Task<string?> FindPriorCoachMessageIdAsync(
        CoachOwner owner,
        string conversationId,
        CancellationToken cancellationToken)
    {
        var page = await _messages
            .GetLatestAsync(owner, conversationId, DisputeLookbackTurns * 2, cancellationToken)
            .ConfigureAwait(false);

        for (var i = page.Items.Count - 1; i >= 0; i--)
        {
            var message = page.Items[i];

            if (message.Role == CoachMessageRole.Coach
                && message.Id.Length is > 0 and <= CoachTurnDisputeState.MaxDisputedMessageIdLength)
            {
                return message.Id;
            }
        }

        return null;
    }

    private string SerializeOutcome(
        CoachTurnResponse answer,
        CoachTurnDisputeState? dispute = null,
        CoachGroundingTurnSummary? grounding = null)
    {
        CoachTurnTraceSummary? trace;
        try
        {
            trace = Tools.Observation.CoachTurnTraceProjection.Project(_observations);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                "[Coach] The turn trace could not be projected and was omitted. {Failure}",
                CoachExceptionSanitizer.Describe(ex));
            trace = null;
        }

        // Grounding comes from the turn's own evaluation, via CoachTurnGroundingEvaluator, which
        // projects it once and hands the same object to the metric and to this write. Null only
        // when the ladder did not run — an Off deployment produces no record, and the property is
        // then omitted entirely rather than written as an explicit null.
        return JsonSerializer.Serialize(
            new CoachStoredTurnOutcome(answer, trace, dispute, grounding), OutcomeJson);
    }

    private static CoachTurnResponse? DeserializeOutcome(string? payload, int? schemaVersion) =>
        ReadOutcome(payload, schemaVersion)?.Answer;

    /// <summary>
    /// Reads a stored outcome under whichever version it was written with.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A version-1 row holds the answer at the root and yields a null trace. A version-2 row holds
    /// the wrapper. Anything else is treated as absent, exactly as an unreadable payload always
    /// was — the ledger still holds the messages, so the client re-reads the conversation instead
    /// of seeing a 500.
    /// </para>
    /// <para>
    /// The version-1 arm is not legacy tolerance to be tidied away later. It is the whole reason
    /// the bump is safe, and deleting it silently empties every turn stored before this build.
    /// </para>
    /// <para>
    /// <b>Tolerance is section-scoped.</b> The wrapper is read as two independent halves rather
    /// than as one object, because they are worth different things. A single
    /// <c>Deserialize&lt;CoachStoredTurnOutcome&gt;</c> made the trace's forward compatibility the
    /// answer's problem: a row written by a later build naming one enum member this one does not
    /// threw, the whole read returned null, and a completed turn read back as no answer at all.
    /// The diagnostic took the turn down with it. Now the answer is parsed strictly and on its own,
    /// and the trace is parsed after it under
    /// <see cref="CoachTurnTraceIntegrity"/> — unreadable trace, answer preserved, trace null.
    /// </para>
    /// <para>
    /// <b>What still fails the whole read.</b> A payload that is not JSON, a root that is not an
    /// object, and an answer that will not parse. Those are not version skew; they are a corrupt or
    /// foreign payload, and reporting an answer-shaped null beside a readable trace would claim the
    /// turn produced no answer when what actually happened is that this build cannot read the row.
    /// </para>
    /// </remarks>
    internal static CoachStoredTurnOutcome? ReadOutcome(string? payload, int? schemaVersion)
    {
        if (payload is null)
        {
            return null;
        }

        try
        {
            return schemaVersion switch
            {
                LegacyOutcomeSchemaVersion => new CoachStoredTurnOutcome(
                    JsonSerializer.Deserialize<CoachTurnResponse>(payload, OutcomeJson), null),

                // Both wrapped versions go through one parser. A v2 row has no dispute section and
                // reads back with a null dispute; a v3 row reads all three. Branching to a second
                // parser would duplicate the answer's strictness and the trace's tolerance, and the
                // two copies would drift.
                WrappedOutcomeSchemaVersionWithoutDispute or OutcomeSchemaVersion =>
                    ReadWrappedOutcome(payload),

                _ => null
            };
        }
        catch (JsonException)
        {
            // A payload this build cannot read is treated as absent. The ledger still holds the
            // messages, so the client re-reads the conversation instead of seeing a 500.
            return null;
        }
    }

    /// <summary>
    /// Reads a wrapped payload as independently-judged sections.
    /// </summary>
    /// <remarks>
    /// A <see cref="JsonException"/> raised here — malformed document, or an answer that will not
    /// parse — propagates to <see cref="ReadOutcome"/> and makes the whole row absent. That is
    /// deliberate: only the trace section is tolerated, and it is tolerated inside
    /// <see cref="ReadTraceSection"/> where the boundary is visible.
    /// </remarks>
    private static CoachStoredTurnOutcome? ReadWrappedOutcome(string payload)
    {
        using var document = JsonDocument.Parse(payload);

        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var answer = TryGetSection(document.RootElement, nameof(CoachStoredTurnOutcome.Answer)) is { } answerSection
            ? answerSection.Deserialize<CoachTurnResponse>(OutcomeJson)
            : null;

        return new CoachStoredTurnOutcome(
            answer,
            ReadTraceSection(document.RootElement),
            ReadDisputeSection(document.RootElement),
            ReadGroundingSection(document.RootElement));
    }

    /// <summary>
    /// The trace, or null when this build cannot read it correctly.
    /// </summary>
    /// <remarks>
    /// Two ways to be unreadable, and both land here rather than upstairs. An unknown enum
    /// <em>name</em> throws inside the deserializer, because the three scope enums carry the string
    /// converter. An unknown enum <em>ordinal</em> or an unknown argument-mask bit does not throw at
    /// all — System.Text.Json materialises any integer into an enum — so the parsed result is put
    /// through the integrity census before it is believed.
    /// </remarks>
    private static CoachTurnTraceSummary? ReadTraceSection(JsonElement root)
    {
        if (TryGetSection(root, nameof(CoachStoredTurnOutcome.Trace)) is not { } section)
        {
            return null;
        }

        try
        {
            var trace = section.Deserialize<CoachTurnTraceSummary>(OutcomeJson);

            return trace is not null && CoachTurnTraceIntegrity.IsReadable(trace) ? trace : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// The open dispute, or null when there is none or this build cannot read it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Tolerated exactly as the trace is, and for the same reason: a dispute is state <em>about</em>
    /// the answer, and an unreadable one must not take the answer down with it. A learner whose
    /// completed turn read back empty because a later build named one enum member this one does not
    /// would have lost the thing they came for, to protect a constraint on the next turn.
    /// </para>
    /// <para>
    /// The bounded-identifier check runs here rather than at the rule, because this is the boundary
    /// a foreign payload crosses. A dispute whose message identifier is longer than the ledger's own
    /// identifiers is not a dispute this build wrote, and reading it would let a stored blob become
    /// a channel for prose the protected outcome is not allowed to hold.
    /// </para>
    /// </remarks>
    private static CoachTurnDisputeState? ReadDisputeSection(JsonElement root)
    {
        if (TryGetSection(root, nameof(CoachStoredTurnOutcome.Dispute)) is not { } section)
        {
            return null;
        }

        try
        {
            var dispute = section.Deserialize<CoachTurnDisputeState>(OutcomeJson);

            if (dispute is null)
            {
                return null;
            }

            var identifier = dispute.DisputedMessageId;

            if (string.IsNullOrWhiteSpace(identifier)
                || identifier.Length > CoachTurnDisputeState.MaxDisputedMessageIdLength
                || !Enum.IsDefined(dispute.Signal)
                || !Enum.IsDefined(dispute.Resolution))
            {
                return null;
            }

            return dispute;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// What the honesty layer did, or null when there is nothing readable.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The third tolerated section, on the same terms as the trace and the dispute: unreadable here
    /// means <em>this section</em> is null, never that the turn had no answer. A learner whose
    /// completed turn read back empty because a later build named one rule code this one does not
    /// would have lost the thing they came for, to protect a diagnostic.
    /// </para>
    /// <para>
    /// Two ways to be unreadable and both land here. An unknown enum <em>name</em> throws inside the
    /// deserializer, because every enum on this shape carries the string converter. An unknown enum
    /// <em>ordinal</em>, an out-of-range count, or a duplicated rule does not throw at all —
    /// System.Text.Json materialises any integer into an enum — so the parsed result goes through
    /// <see cref="CoachGroundingTurnSummary.IsWellFormed"/> before it is believed.
    /// </para>
    /// </remarks>
    private static CoachGroundingTurnSummary? ReadGroundingSection(JsonElement root)
    {
        if (TryGetSection(root, nameof(CoachStoredTurnOutcome.Grounding)) is not { } section)
        {
            return null;
        }

        try
        {
            var grounding = section.Deserialize<CoachGroundingTurnSummary>(OutcomeJson);

            return grounding is not null && grounding.IsWellFormed() ? grounding : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// The named section, or null when it is absent or explicitly null.
    /// </summary>
    /// <remarks>
    /// Case-insensitive, because <see cref="OutcomeJson"/> is built from
    /// <see cref="JsonSerializerDefaults.Web"/>: it writes <c>answer</c> and <c>trace</c> and reads
    /// either casing. Matching case-sensitively here would read every row this build itself wrote
    /// as having no answer.
    /// </remarks>
    private static JsonElement? TryGetSection(JsonElement root, string name)
    {
        foreach (var property in root.EnumerateObject())
        {
            if (!string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            return property.Value.ValueKind == JsonValueKind.Null ? null : property.Value;
        }

        return null;
    }
}
