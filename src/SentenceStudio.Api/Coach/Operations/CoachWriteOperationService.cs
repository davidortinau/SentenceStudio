using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using SentenceStudio.Api.Coach.Persistence;
using SentenceStudio.Api.Coach.Persistence.History;
using SentenceStudio.Api.Coach.Telemetry;
using SentenceStudio.Api.Coach.Tools;
using SentenceStudio.Contracts.Coach;
using SentenceStudio.Services.Plans;

namespace SentenceStudio.Api.Coach.Operations;

/// <summary>
/// The ledger every learner-owned write passes through.
/// </summary>
/// <remarks>
/// <para>
/// This type owns the whole rule set: a proposal is recorded but never executed, an execution
/// happens only after the learner approved it through the channel the tool's risk class demands,
/// an approval is idempotent, and a reversal is bounded and one-use. Handlers do the domain work;
/// nothing about approval, replay, or expiry lives in them, so a new tool cannot accidentally opt
/// out of any of it.
/// </para>
/// <para>
/// The owner is resolved from the authenticated principal on every entry point, before any query
/// runs. No method takes an owner, profile, tenant, or email identifier as a parameter, which is
/// what makes it structurally impossible for a model argument to select a different learner.
/// </para>
/// </remarks>
/// <summary>
/// The one capability the model-facing tool is given: record a proposal.
/// </summary>
/// <remarks>
/// The tool is handed this interface rather than <see cref="CoachWriteOperationService"/> itself so
/// that the object the model can reach cannot execute, confirm, or undo anything, and cannot reach
/// the coach database through a stored field. Holding the whole ledger would work, but it would
/// mean the tool's restraint was a matter of which methods it happened to call rather than which
/// methods it has.
/// </remarks>
public interface ICoachWriteProposer
{
    /// <inheritdoc cref="CoachWriteOperationService.ProposeAsync" />
    Task<CoachWriteProposalResult> ProposeAsync(
        string conversationId,
        string? turnId,
        string toolName,
        string argumentsJson,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Which learner-facing route an approval arrived on.
/// </summary>
/// <remarks>
/// The channel is decided by the route, never inferred from whether a confirmation secret
/// happens to be present. Inferring it meant a caller could execute a soft write through the
/// protected route simply by omitting the header, which is the one thing the two routes exist to
/// keep apart.
/// </remarks>
public enum CoachWriteApprovalChannel
{
    /// <summary>The soft acceptance route. Serves <see cref="CoachToolRiskClass.WriteSoft"/> only.</summary>
    Accept = 0,

    /// <summary>The protected confirmation route. Serves <see cref="CoachToolRiskClass.WriteHard"/> only.</summary>
    Confirm = 1
}

public sealed class CoachWriteOperationService : ICoachWriteProposer
{
    /// <summary>How many times an approval re-reads after losing the execution claim.</summary>
    /// <remarks>
    /// Two, because losing the claim means another approval already moved the row out of
    /// <c>Proposed</c>, and it can only move it forward. One re-read is therefore enough to find
    /// the answer the winner left — a receipt, or a row still in flight — and a third attempt
    /// would only be spinning.
    /// </remarks>
    private const int MaxTransitionAttempts = 2;

    /// <summary>How many times a proposal re-reads before giving up on a contended slot.</summary>
    /// <remarks>
    /// Three, because a repeat can legitimately go round twice: once to find a closed row and
    /// release its slot, and once more if another request took the freed slot before this one
    /// reached the insert. A third pass only happens under contention this method cannot win by
    /// spinning, so it refuses and says so rather than looping.
    /// </remarks>
    private const int MaxProposalAttempts = 3;

    private readonly CoachDbContext _db;
    private readonly ICoachContentProtector _protector;
    private readonly ICoachWriteHandlerCatalog _handlers;
    private readonly ICoachToolRegistry _registry;
    private readonly IUserScopeProvider _userScope;
    private readonly TimeProvider _time;
    private readonly ILogger<CoachWriteOperationService> _logger;

    /// <summary>
    /// The opportunity ledger, when this host has one. Optional so every hand-constructed test
    /// call site keeps working.
    /// </summary>
    private readonly Opportunities.ICoachOpportunityRecorder _opportunities;

    /// <summary>
    /// Signals queued by the audit helpers, flushed only after the audit rows they describe
    /// actually committed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Queued rather than written inline, because a refusal that was rolled back is not a
    /// refusal. The audit row and the ledger row have to agree about what happened, and the only
    /// moment that is known is after <c>SaveChanges</c> returns.
    /// </para>
    /// <para>
    /// Cleared when a save throws, for the same reason: the caller's recovery path writes its own
    /// audit, and carrying the failed attempt's signal forward would count one refusal twice.
    /// </para>
    /// </remarks>
    private readonly List<Opportunities.CoachOpportunitySignal> _pendingOpportunities = new();

    public CoachWriteOperationService(
        CoachDbContext db,
        ICoachContentProtector protector,
        ICoachWriteHandlerCatalog handlers,
        ICoachToolRegistry registry,
        IUserScopeProvider userScope,
        TimeProvider time,
        ILogger<CoachWriteOperationService> logger,
        Opportunities.ICoachOpportunityRecorder? opportunities = null)
    {
        _db = db;
        _protector = protector;
        _handlers = handlers;
        _registry = registry;
        _userScope = userScope;
        _time = time;
        _logger = logger;
        _opportunities = opportunities ?? Opportunities.NullCoachOpportunityRecorder.Instance;
    }

    /// <summary>
    /// Saves, then records any refusal the save made durable.
    /// </summary>
    /// <remarks>
    /// The ledger write runs on its own scope and its own connection, so it neither joins this
    /// method's transaction nor can fail it — <c>RecordAsync</c> swallows everything by contract.
    /// Ordering it after the save is what keeps the two ledgers consistent; isolating it is what
    /// keeps a telemetry failure from becoming a learner-visible one.
    /// </remarks>
    private async Task<int> SaveAuditedAsync(CancellationToken cancellationToken)
    {
        int written;
        try
        {
            written = await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            _pendingOpportunities.Clear();
            throw;
        }

        if (_pendingOpportunities.Count == 0)
        {
            return written;
        }

        var signals = _pendingOpportunities.ToArray();
        _pendingOpportunities.Clear();

        try
        {
            foreach (var signal in signals)
            {
                await _opportunities.RecordAsync(signal, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            // Every exception, including OperationCanceledException. The write already committed:
            // turning a successful audit into a thrown exception because a telemetry row could
            // not be written would undo the whole point of recording refusals — and the caller's
            // own recovery path would then write a second, contradictory audit row.
            //
            // Cancellation is caught for the same reason and no other. The learner's operation is
            // finished at this point: SaveChangesAsync above already observed the caller's token
            // and threw if it was cancelled, so cancellation semantics for the work the learner
            // asked for are enforced there. A token cancelled between the commit and this flush
            // says nothing about whether the audit succeeded — it did — so it must not be allowed
            // to report otherwise.
            var facts = CoachExceptionSanitizer.Describe(ex);
            _logger.LogWarning(
                "[Coach] A write refusal could not be added to the opportunity ledger; the audit " +
                "is unaffected. Category={FailureCategory} InnerDepth={InnerDepth}",
                facts.Category,
                facts.InnerDepth);
        }

        return written;
    }

    /// <summary>
    /// Queues a ledger signal for a refusal the write audit is about to record.
    /// </summary>
    /// <remarks>
    /// The mapper decides everything — kind, capability code, and whether the row is individually
    /// reviewable at all. A refusal whose code has no declared disposition returns null here and
    /// <c>CoachOpportunityTriggerMappingTests</c> fails the build until somebody declares one.
    /// </remarks>
    private void QueueOpportunity(
        string? failureCode,
        string? toolName,
        string? conversationId,
        string? turnId,
        string? operationId,
        string? settingName = null)
    {
        var signal = Opportunities.Mapping.CoachWriteAuditOpportunityMapper.Map(
            failureCode, toolName, conversationId, turnId, operationId, settingName);

        if (signal is { } value)
        {
            _pendingOpportunities.Add(value);
        }
    }

    private DateTime UtcNow => _time.GetUtcNow().UtcDateTime;

    // ---------------------------------------------------------------- propose

    /// <summary>
    /// Records what the model asked for, after validating that the learner owns everything the
    /// request refers to. Nothing in learner data changes here.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Repeating the same request in the same conversation is answered by whichever row still
    /// speaks for it. A live proposal replays itself, so a model that calls the same tool twice in
    /// one turn produces one proposal rather than two buttons that both work. An executed
    /// operation replays its authoritative receipt. An operation whose execution claim is
    /// outstanding refuses, because a second row would risk a second write.
    /// </para>
    /// <para>
    /// A row that is closed and left no effect — declined, elapsed, reversed, or closed after a
    /// failed handler — answers for nothing. The learner asked for something and does not have
    /// it, so the request has to be askable again. Such a row releases its idempotency slot here,
    /// on the first repeat that finds it, and the repeat then records a genuinely new proposal.
    /// </para>
    /// <para>
    /// Releasing lazily, in this one method, rather than eagerly at each of the four transitions
    /// that can close a row, is deliberate: a transition added later cannot forget to do it, and
    /// the transitions themselves — which arbitrate execution claims — stay untouched.
    /// </para>
    /// </remarks>
    public async Task<CoachWriteProposalResult> ProposeAsync(
        string conversationId,
        string? turnId,
        string toolName,
        string argumentsJson,
        CancellationToken cancellationToken = default)
    {
        var owner = RequireOwner(toolName);
        var handler = RequireHandler(toolName);
        RequireEnabled(toolName);

        if (string.IsNullOrWhiteSpace(conversationId))
        {
            throw new CoachToolException(
                CoachToolFailureKind.InvalidArgument, toolName, "The request has no conversation.");
        }

        // Fails closed rather than skipping the per-turn cap. A proposal with no turn to belong to
        // cannot be counted against one, and a path that cannot be counted is a path with no cap
        // at all. The turn pipeline always supplies an identity — the client's when it sent one,
        // a server-minted one when it did not — so this is unreachable in production and is here
        // to stay unreachable.
        if (string.IsNullOrWhiteSpace(turnId))
        {
            _logger.LogWarning(
                "[Coach] Write proposal refused: no turn identity for {Tool}.", toolName);
            throw new CoachToolException(
                CoachToolFailureKind.InvalidArgument, toolName, "The request has no turn.");
        }

        // Ownership and shape are proved before anything is written, including before the
        // idempotency digest is computed: a proposal that could never execute must not occupy a
        // ledger row, and a preview the learner might approve must never describe somebody
        // else's data.
        var preview = await handler.PrepareAsync(owner.UserProfileId, argumentsJson, cancellationToken)
            .ConfigureAwait(false);

        var lines = BoundLines(preview.Lines);
        var summary = BoundLine(preview.Summary);
        var canonicalArgs = preview.CanonicalArgumentsJson;
        GuardPayloadSize(toolName, canonicalArgs, CoachWriteLimits.ArgumentsMaxBytes);

        var keyDigest = ComputeIdempotencyDigest(owner, conversationId, toolName, canonicalArgs);

        for (var attempt = 1; ; attempt++)
        {
            var now = UtcNow;

            var existing = await _db.CoachWriteOperations
                .FirstOrDefaultAsync(
                    o => o.UserProfileId == owner.UserProfileId
                         && o.ConversationId == conversationId
                         && o.IdempotencyKeyDigest == keyDigest,
                    cancellationToken)
                .ConfigureAwait(false);

            if (existing is not null)
            {
                var replay = await ReplayOrReleaseAsync(
                    owner, existing, keyDigest, now, cancellationToken).ConfigureAwait(false);

                if (replay is not null)
                {
                    return replay;
                }

                // The row released its slot and detached itself. Go back to the top rather than
                // inserting straight away, because another request may have taken the freed slot
                // between the release and here — and if it did, its row is classified by the same
                // rules as any other repeat.
                if (attempt < MaxProposalAttempts)
                {
                    continue;
                }

                await AuditProposalContentionAsync(owner, conversationId, cancellationToken)
                    .ConfigureAwait(false);
                throw Refused(toolName, "That change could not be proposed just now. Ask again.");
            }

            await GuardProposalBudgetAsync(
                    owner, conversationId, turnId, toolName, keyDigest, now, cancellationToken)
                .ConfigureAwait(false);

            var operationId = NewId();
            var record = new CoachWriteOperation
            {
                Id = operationId,
                UserProfileId = owner.UserProfileId,
                TenantId = owner.TenantId,
                ConversationId = conversationId,
                TurnId = Truncate(turnId, CoachWriteLimits.IdMaxLength),
                ToolName = toolName,
                RiskClass = handler.RiskClass,
                Status = CoachWriteOperationStatus.Proposed,
                UndoKind = handler.UndoKind,
                EntityKind = handler.EntityKind,
                EntityId = Truncate(preview.EntityId, CoachWriteLimits.IdMaxLength),
                IdempotencyKeyDigest = keyDigest,
                ProtectedArguments = Protect(owner, CoachProtectedContentKind.WriteOperationArguments, operationId, canonicalArgs),
                ProtectedPreview = Protect(
                    owner,
                    CoachProtectedContentKind.WriteOperationPreview,
                    operationId,
                    CoachNormalizedJson.Serialize(
                        new CoachWriteNarrative(CoachWriteNarrative.CurrentSchemaVersion, summary, lines))),
                ContentProtectionVersion = _protector.CurrentVersion,
                ExpiresAtUtc = now.Add(CoachWriteLimits.ProposalLifetime),
                CreatedAtUtc = now,
                UpdatedAtUtc = now,
                Version = 1
            };

            _db.CoachWriteOperations.Add(record);
            AppendAudit(record, CoachWriteAuditEvent.Proposed, now, failureCode: null);

            try
            {
                await SaveAuditedAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (DbUpdateException)
            {
                // Two calls raced on the unique idempotency index. The loser drops its insert and
                // goes back to the top, where the winner's row is read and classified by exactly
                // the same rules as any other repeat — so the learner sees one proposal, and a
                // winner that had already settled is answered from its own state rather than from
                // an assumption about what it must be.
                _db.ChangeTracker.Clear();
                if (attempt < MaxProposalAttempts)
                {
                    continue;
                }

                await AuditProposalContentionAsync(owner, conversationId, cancellationToken)
                    .ConfigureAwait(false);
                throw Refused(toolName, "That change could not be proposed just now. Ask again.");
            }

            return Describe(owner, record, isDuplicate: false);
        }
    }

    // ---------------------------------------------------------------- read

    /// <summary>Reads one proposal or receipt the learner owns.</summary>
    public async Task<CoachWriteProposalResult?> GetAsync(
        string conversationId,
        string operationId,
        CancellationToken cancellationToken = default)
    {
        var owner = RequireOwner("coach_write_get");
        var record = await FindOwnedAsync(owner, conversationId, operationId, cancellationToken)
            .ConfigureAwait(false);

        return record is null ? null : Describe(owner, record, isDuplicate: false);
    }

    /// <summary>Reads the receipt for an executed or reversed operation.</summary>
    public async Task<CoachWriteReceipt?> GetReceiptAsync(
        string conversationId,
        string operationId,
        CancellationToken cancellationToken = default)
    {
        var owner = RequireOwner("coach_write_receipt");
        var record = await FindOwnedAsync(owner, conversationId, operationId, cancellationToken)
            .ConfigureAwait(false);

        return record is null ? null : BuildReceipt(owner, record);
    }

    // ---------------------------------------------------------------- client state
    //
    // Everything below answers the same question the card on screen is asking: what is true
    // about this change right now? The shapes are the public contract rather than the ledger's
    // own records, so nothing protected, no arguments, no prior values, and no audit row can
    // reach a client through them.

    /// <summary>Reads one operation's full client-facing state.</summary>
    public async Task<CoachWriteOperationDto?> GetStateAsync(
        string conversationId,
        string operationId,
        CancellationToken cancellationToken = default)
    {
        var owner = RequireOwner("coach_write_state");
        var record = await FindOwnedAsync(owner, conversationId, operationId, cancellationToken)
            .ConfigureAwait(false);

        return record is null ? null : DescribeState(owner, record, messageId: null);
    }

    /// <summary>
    /// True when this conversation has a proposal still waiting for the learner's answer.
    /// </summary>
    /// <remarks>
    /// <para>
    /// One of the five authoritative conjuncts behind the referent-loss detector: a learner who
    /// typed "yes" while a proposal was open had somewhere for that answer to go, even if this
    /// particular turn did not take it. Only an answer with <em>nothing</em> open is a lost
    /// referent.
    /// </para>
    /// <para>
    /// Owner-scoped and expiry-aware, so an elapsed proposal reads as closed — which is correct:
    /// the learner can no longer accept it, so their "yes" has nothing to bind to either.
    /// </para>
    /// <para>
    /// Returns false for a missing owner rather than throwing. The caller is telemetry, and a
    /// detector that could 500 a turn would be worse than no detector.
    /// </para>
    /// </remarks>
    public async Task<bool> HasOpenProposalAsync(
        string? conversationId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(conversationId))
        {
            return false;
        }

        if (!_userScope.TryGetUserProfileId(out var userProfileId)
            || string.IsNullOrWhiteSpace(userProfileId))
        {
            return false;
        }

        var now = UtcNow;

        return await _db.CoachWriteOperations
            .AsNoTracking()
            .AnyAsync(
                o => o.UserProfileId == userProfileId
                     && o.ConversationId == conversationId
                     && o.Status == CoachWriteOperationStatus.Proposed
                     && o.ExpiresAtUtc > now,
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Reads the change one turn proposed, so a live turn response can carry it.
    /// </summary>
    /// <remarks>
    /// A turn records at most one proposal — the ledger refuses the second before it is written —
    /// so this reads the turn's proposal rather than choosing between several. The ordering is
    /// kept so that a row recorded before that invariant existed still resolves to one answer
    /// instead of an arbitrary one. A turn that proposed nothing answers null, which is the
    /// overwhelmingly common case and costs one indexed read.
    /// </remarks>
    public async Task<CoachWriteOperationDto?> GetLatestForTurnAsync(
        string conversationId,
        string? turnId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(conversationId) || string.IsNullOrWhiteSpace(turnId))
        {
            return null;
        }

        var owner = RequireOwner("coach_write_turn_state");

        var record = await _db.CoachWriteOperations
            .Where(o => o.UserProfileId == owner.UserProfileId
                        && o.ConversationId == conversationId
                        && o.TurnId == turnId)
            .OrderByDescending(o => o.CreatedAtUtc)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        return record is null ? null : DescribeState(owner, record, messageId: null);
    }

    /// <summary>
    /// Reads every change proposed by the given turns, so a page of history can rebuild its cards.
    /// </summary>
    /// <remarks>
    /// Bounded by the caller's page, and owner-scoped like every other read here: a turn id
    /// belonging to somebody else matches nothing rather than matching their row. Returned in
    /// creation order so a caller pairing them with messages gets a stable result.
    /// </remarks>
    public async Task<IReadOnlyList<CoachWriteOperationDto>> ListForTurnsAsync(
        string conversationId,
        IReadOnlyCollection<string> turnIds,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(conversationId) || turnIds is null || turnIds.Count == 0)
        {
            return Array.Empty<CoachWriteOperationDto>();
        }

        var owner = RequireOwner("coach_write_history_state");

        var records = await _db.CoachWriteOperations
            .Where(o => o.UserProfileId == owner.UserProfileId
                        && o.ConversationId == conversationId
                        && o.TurnId != null
                        && turnIds.Contains(o.TurnId))
            .OrderBy(o => o.CreatedAtUtc)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return records.Count == 0
            ? Array.Empty<CoachWriteOperationDto>()
            : records.Select(record => DescribeState(owner, record, messageId: null)).ToArray();
    }

    /// <summary>
    /// Restates one ledger row as the public contract, including its receipt when it has one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The preview narrative and the receipt narrative are two different sentences about the same
    /// change — "this would happen" and "this happened" — so both are read when both exist. The
    /// summary the card shows is the receipt's once the change has run, because a card that still
    /// reads "Sam would like to add..." above an applied receipt is describing a decision the
    /// learner has already made.
    /// </para>
    /// <para>
    /// A payload that cannot be decrypted does not fail the read. The state is still true and the
    /// learner still needs to see it, so the narrative falls back to empty and the card renders
    /// its own localized copy from <see cref="CoachWriteOperationDto.ChangeKind"/>. Losing the
    /// detail lines is a smaller harm than losing the fact that a change is pending.
    /// </para>
    /// </remarks>
    private CoachWriteOperationDto DescribeState(
        CoachOwner owner,
        CoachWriteOperation record,
        string? messageId,
        bool isDuplicate = false)
    {
        var effective = CoachWriteOperationStates.IsEffective(record.Status);
        var hasReceipt = record.ProtectedReceipt is not null;

        var preview = ReadNarrativeSafely(owner, record, receipt: false);
        var receiptNarrative = hasReceipt ? ReadNarrativeSafely(owner, record, receipt: true) : null;
        var headline = receiptNarrative ?? preview;

        var isProtected = record.RiskClass == CoachToolRiskClass.WriteHard;
        var now = UtcNow;

        return new CoachWriteOperationDto
        {
            OperationId = record.Id,
            ConversationId = record.ConversationId,
            TurnId = record.TurnId,
            MessageId = messageId,
            ChangeKind = CoachWriteProjection.ChangeKind(record.ToolName),
            RiskClass = CoachWriteProjection.RiskClass(record.RiskClass),
            Status = CoachWriteProjection.Status(record.Status),
            ApprovalMode = isProtected
                ? CoachWriteApprovalModes.Confirm
                : CoachWriteApprovalModes.Accept,
            Summary = headline?.Summary ?? string.Empty,
            Lines = headline?.Lines ?? Array.Empty<string>(),
            ExpiresAtUtc = record.ExpiresAtUtc,
            RequiresConfirmation = isProtected,
            // Only an outstanding secret has an expiry worth showing. A spent one is cleared by
            // the transition that spent it, so a non-null value here always means "there is a
            // confirmation in flight", never "there was one once".
            ConfirmationExpiresAtUtc = record.ConfirmationDigest is null
                ? null
                : record.ConfirmationExpiresAtUtc,
            IsReversible = record.UndoKind != CoachWriteUndoKind.None,
            IsDuplicate = isDuplicate,
            AlreadyExecuted = effective,
            // A receipt is offered for anything that ran, including something since reversed:
            // "you added this, then undid it" is the honest account, and the status inside the
            // receipt is what says which of the two is true now.
            Receipt = hasReceipt || record.ExecutedAtUtc is not null
                ? new CoachWriteReceiptDto
                {
                    OperationId = record.Id,
                    ChangeKind = CoachWriteProjection.ChangeKind(record.ToolName),
                    RiskClass = CoachWriteProjection.RiskClass(record.RiskClass),
                    Status = CoachWriteProjection.Status(record.Status),
                    TargetKind = CoachWriteProjection.TargetKind(record.EntityKind),
                    TargetId = record.EntityId,
                    Summary = receiptNarrative?.Summary ?? preview?.Summary ?? string.Empty,
                    Lines = receiptNarrative?.Lines ?? Array.Empty<string>(),
                    ExecutedAtUtc = record.ExecutedAtUtc ?? record.UpdatedAtUtc,
                    CanUndo = record.Status == CoachWriteOperationStatus.Executed
                              && record.UndoKind != CoachWriteUndoKind.None
                              && record.ProtectedPriorState is not null
                              && record.UndoExpiresAtUtc is not null
                              && record.UndoExpiresAtUtc > now,
                    UndoExpiresAtUtc = record.UndoExpiresAtUtc
                }
                : null
        };
    }

    /// <summary>
    /// Reads a protected narrative, answering null rather than throwing when it cannot be read.
    /// </summary>
    /// <remarks>
    /// A key rotation, a schema version this build does not understand, or a genuinely corrupt
    /// row must not take out the whole conversation read that happened to include it. The caller
    /// renders the state without the detail lines instead.
    /// </remarks>
    private CoachWriteNarrative? ReadNarrativeSafely(
        CoachOwner owner, CoachWriteOperation record, bool receipt)
    {
        try
        {
            return receipt ? UnprotectReceipt(owner, record) : UnprotectNarrative(owner, record);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(
                "[Coach] The stored description for write operation {Operation} could not be read.",
                record.Id);
            return null;
        }
    }

    // ---------------------------------------------------------------- confirmation secret

    /// <summary>
    /// Mints the one-use secret a protected write requires, bound to this learner, conversation,
    /// operation, and the exact arguments recorded at proposal time.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The secret is returned once, here, on an authenticated owner-scoped route. Only its digest
    /// is stored, so it cannot be recovered from a database copy, and it never appears in a tool
    /// result — the model has no route that returns it and no tool that could call one.
    /// </para>
    /// <para>
    /// Calling this again rotates the secret: the previous digest is overwritten, which makes the
    /// earlier value permanently unusable. That is deliberate. A learner who reopens the
    /// confirmation prompt should not leave a second working key behind them.
    /// </para>
    /// </remarks>
    public async Task<CoachWriteConfirmationChallenge?> IssueConfirmationAsync(
        string conversationId,
        string operationId,
        CancellationToken cancellationToken = default)
    {
        var owner = RequireOwner("coach_write_confirmation");
        var record = await FindOwnedAsync(owner, conversationId, operationId, cancellationToken)
            .ConfigureAwait(false);

        if (record is null)
        {
            return null;
        }

        var now = UtcNow;

        if (record.RiskClass != CoachToolRiskClass.WriteHard)
        {
            await DenyAsync(record, CoachWriteFailureCodes.WrongAcceptanceChannel, now, cancellationToken)
                .ConfigureAwait(false);
            throw Refused(record.ToolName, "This change does not use a confirmation code.");
        }

        if (record.Status != CoachWriteOperationStatus.Proposed)
        {
            await DenyAsync(record, CoachWriteFailureCodes.InvalidState, now, cancellationToken)
                .ConfigureAwait(false);
            throw Refused(record.ToolName, "This change is no longer awaiting approval.");
        }

        if (record.ExpiresAtUtc <= now)
        {
            await ExpireAsync(record, now, cancellationToken).ConfigureAwait(false);
            throw Refused(record.ToolName, "This proposal has expired.");
        }

        var secret = NewSecret();
        var arguments = UnprotectArguments(owner, record);

        record.ConfirmationDigest = ComputeConfirmationDigest(owner, record, arguments, secret);
        record.ConfirmationExpiresAtUtc = now.Add(CoachWriteLimits.ConfirmationLifetime);
        record.UpdatedAtUtc = now;
        record.Version++;

        await SaveAuditedAsync(cancellationToken).ConfigureAwait(false);

        var narrative = UnprotectNarrative(owner, record);
        return new CoachWriteConfirmationChallenge(
            record.Id,
            record.ToolName,
            secret,
            narrative.Summary,
            narrative.Lines,
            record.ConfirmationExpiresAtUtc.Value);
    }

    // ---------------------------------------------------------------- approve

    /// <summary>Executes a soft learner-owned write the learner explicitly accepted.</summary>
    public Task<CoachWriteReceipt> AcceptAsync(
        string conversationId,
        string operationId,
        CancellationToken cancellationToken = default) =>
        ApproveAsync(
            conversationId,
            operationId,
            CoachWriteApprovalChannel.Accept,
            confirmationSecret: null,
            cancellationToken);

    /// <summary>Executes a protected write against a one-use confirmation secret.</summary>
    public Task<CoachWriteReceipt> ConfirmAsync(
        string conversationId,
        string operationId,
        string? confirmationSecret,
        CancellationToken cancellationToken = default) =>
        ApproveAsync(
            conversationId,
            operationId,
            CoachWriteApprovalChannel.Confirm,
            confirmationSecret,
            cancellationToken);

    private async Task<CoachWriteReceipt> ApproveAsync(
        string conversationId,
        string operationId,
        CoachWriteApprovalChannel channel,
        string? confirmationSecret,
        CancellationToken cancellationToken)
    {
        var owner = RequireOwner("coach_write_approve");

        for (var attempt = 1; ; attempt++)
        {
            var record = await FindOwnedAsync(owner, conversationId, operationId, cancellationToken)
                .ConfigureAwait(false);

            if (record is null)
            {
                await AuditOrphanDenialAsync(owner, conversationId, operationId, cancellationToken)
                    .ConfigureAwait(false);
                throw Refused("coach_write_approve", "No such pending change for this learner.");
            }

            var now = UtcNow;

            // The route is checked before anything else about the operation's state, because the
            // route is a property of the request and not of the row. A protected write reached
            // through the soft acceptance route, or a soft write reached through the protected
            // route, is refused whatever the row happens to say — including when the row has
            // already executed, so no reply from the wrong route can ever read as a success.
            await GuardApprovalRouteAsync(record, channel, now, cancellationToken).ConfigureAwait(false);

            // An already-settled operation replays its receipt. This is what makes approval
            // idempotent: a retried request, a double-tapped button, and a resumed client all
            // land on the same stored result instead of writing a second time.
            //
            // A confirmation secret presented here is neither checked nor honoured, and it is not
            // grounds for a refusal either. Execution clears the digest, so there is nothing left
            // to compare against and the secret authorizes nothing — that is what makes it
            // genuinely one-use. What it must not do is turn a completed change into a message
            // saying the change failed. The learner's client retries with the same secret it was
            // given, and the honest answer to "did my change happen" is the receipt.
            //
            // This leaks nothing. The reply is exactly what the receipt route returns for the same
            // owner, conversation, and operation, and that route asks for no secret at all: it is
            // ownership that authorizes reading a receipt, so a forged secret buys an attacker
            // nothing they could not already have. And no second write can follow, because the
            // status branch here returns before the claim is ever attempted.
            if (record.Status is CoachWriteOperationStatus.Executed or CoachWriteOperationStatus.Undone)
            {
                if (!string.IsNullOrEmpty(confirmationSecret))
                {
                    _logger.LogInformation(
                        "[Coach] Write operation {Operation} replayed its receipt for a spent confirmation.",
                        record.Id);
                }

                AppendAudit(record, CoachWriteAuditEvent.Replayed, now, failureCode: null);
                await SaveAuditedAsync(cancellationToken).ConfigureAwait(false);
                return BuildReceipt(owner, record);
            }

            // Somebody holds the execution claim. Whether they are still working or died holding
            // it, this request must not run the handler: the domain write may already have
            // happened. Saying so is the only truthful answer available, and it is a refusal
            // rather than a receipt because no receipt exists to replay.
            if (record.Status == CoachWriteOperationStatus.Executing)
            {
                await DenyAsync(record, CoachWriteFailureCodes.ExecutionInDoubt, now, cancellationToken)
                    .ConfigureAwait(false);
                throw Refused(record.ToolName, "This change is already being carried out.");
            }

            if (record.Status != CoachWriteOperationStatus.Proposed)
            {
                await DenyAsync(record, CoachWriteFailureCodes.InvalidState, now, cancellationToken)
                    .ConfigureAwait(false);
                throw Refused(record.ToolName, "This change is no longer awaiting approval.");
            }

            if (record.ExpiresAtUtc <= now)
            {
                await ExpireAsync(record, now, cancellationToken).ConfigureAwait(false);
                throw Refused(record.ToolName, "This proposal has expired.");
            }

            var arguments = UnprotectArguments(owner, record);
            await GuardApprovalSecretAsync(owner, record, arguments, confirmationSecret, now, cancellationToken)
                .ConfigureAwait(false);

            var handler = RequireHandler(record.ToolName);
            RequireEnabled(record.ToolName);

            // ------------------------------------------------------------- claim
            // Everything above only read. The handler below changes learner data and, for the
            // import tools, calls out to a third party. Between those two facts sits the only
            // thing that makes execution exactly-once: a single conditional UPDATE that moves the
            // row out of Proposed. It is the database that arbitrates, so two API processes, two
            // requests on one process, and a retry after a dropped response all contend on the
            // same row rather than on a lock that only exists inside one of them.
            var claimed = await TryClaimForExecutionAsync(owner, record, cancellationToken)
                .ConfigureAwait(false);

            if (claimed is null)
            {
                // Lost the claim. Re-read and answer from whatever the winner left behind: a
                // receipt if it finished, a refusal if it is still working. Never the handler.
                _db.ChangeTracker.Clear();
                if (attempt < MaxTransitionAttempts)
                {
                    continue;
                }

                // Exhausted the re-reads without ever seeing a settled row. The audit says so
                // explicitly rather than borrowing the in-doubt code, because losing a claim is
                // a normal outcome of two learners' tabs racing and an unrecorded execution is
                // not: an operator reading the audit has to be able to tell them apart.
                await AuditClaimLossAsync(owner, conversationId, operationId, cancellationToken)
                    .ConfigureAwait(false);
                throw Refused(record.ToolName, "This change is already being carried out.");
            }

            record = claimed;

            CoachWriteExecution execution;
            try
            {
                execution = await handler.ExecuteAsync(owner.UserProfileId, arguments, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // The claim is spent either way. A handler that threw may have changed nothing or
                // may have changed something and failed on the way back, and the ledger cannot
                // tell those apart, so the operation closes as Failed rather than returning to
                // Proposed where a second approval could run it again.
                await FailClaimAsync(record, CoachWriteFailureCodes.ExecutionFailed, cancellationToken)
                    .ConfigureAwait(false);

                if (ex is CoachToolException)
                {
                    throw;
                }

                throw Refused(record.ToolName, "That change could not be carried out.");
            }

            var executedAt = UtcNow;
            var summary = BoundLine(execution.Summary);
            var lines = BoundLines(execution.Lines);
            var reversible = handler.UndoKind != CoachWriteUndoKind.None && execution.PriorStateJson is not null;

            record.Status = CoachWriteOperationStatus.Executed;
            record.ExecutedAtUtc = executedAt;

            // Spend the confirmation. Keeping the digest would leave a credential on the row that
            // stays comparable forever; clearing it means a copy of the secret taken from a log or a
            // shared screen is inert the moment the change lands.
            record.ConfirmationDigest = null;
            record.ConfirmationExpiresAtUtc = null;
            record.EntityId = Truncate(execution.EntityId, CoachWriteLimits.IdMaxLength) ?? record.EntityId;
            record.ProtectedReceipt = Protect(
                owner,
                CoachProtectedContentKind.WriteOperationReceipt,
                record.Id,
                CoachNormalizedJson.Serialize(
                    new CoachWriteNarrative(CoachWriteNarrative.CurrentSchemaVersion, summary, lines)));

            if (reversible)
            {
                GuardPayloadSize(record.ToolName, execution.PriorStateJson!, CoachWriteLimits.PriorStateMaxBytes);
                record.ProtectedPriorState = Protect(
                    owner,
                    CoachProtectedContentKind.WriteOperationPriorState,
                    record.Id,
                    CoachNormalizedJson.Serialize(
                        new CoachWritePriorState(
                            CoachWritePriorState.CurrentSchemaVersion, execution.PriorStateJson!)));
                record.UndoExpiresAtUtc = executedAt.Add(CoachWriteLimits.UndoWindow);
            }
            else
            {
                record.UndoKind = CoachWriteUndoKind.None;
            }

            // The secret is one-use, so it is destroyed the moment it succeeds. A replayed
            // request finds no digest to match and is answered from the receipt above.
            record.ConfirmationDigest = null;
            record.ConfirmationExpiresAtUtc = null;
            record.UpdatedAtUtc = executedAt;
            record.Version++;

            AppendAudit(record, CoachWriteAuditEvent.Executed, executedAt, failureCode: null);

            try
            {
                await SaveAuditedAsync(cancellationToken).ConfigureAwait(false);
                return BuildReceipt(owner, record);
            }
            catch (DbUpdateException ex)
            {
                // Nothing in the protocol can legitimately move a row whose claim this request
                // holds, so reaching here means the settle itself failed. The write already
                // happened, so the row stays Executing — in doubt, and refused by every later
                // approval — rather than being retried or reported as complete.
                _db.ChangeTracker.Clear();
                _logger.LogError(
                    ex,
                    "[Coach] Write operation {Operation} executed but its receipt could not be recorded.",
                    record.Id);

                // A log line is not a diagnosis anyone can query. The audit gets its own row,
                // written on a cleared tracker so it does not inherit the failure, saying exactly
                // which operation is in doubt and why. If this write fails too the row is still
                // Executing and still un-runnable — the state is safe without the audit, the
                // audit is what makes it explicable.
                await AuditReceiptFailureAsync(record, cancellationToken).ConfigureAwait(false);

                throw Refused(
                    record.ToolName, "That change was carried out but its result could not be recorded.");
            }
        }
    }

    /// <summary>
    /// Moves the operation from <c>Proposed</c> to <c>Executing</c> with one conditional update,
    /// and returns the freshly-read row when this caller won.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The predicate carries the owner, the status, and the version the caller read, so the update
    /// matches at most one row and matches it at most once. <c>ExecuteUpdateAsync</c> issues the
    /// statement directly rather than through the change tracker, which is what allows it to be the
    /// arbiter for callers that have never shared a <see cref="DbContext"/> — or a process.
    /// </para>
    /// <para>
    /// The winner re-reads rather than patching the tracked entity, so the concurrency token it
    /// later saves against is the one the database actually holds.
    /// </para>
    /// </remarks>
    private async Task<CoachWriteOperation?> TryClaimForExecutionAsync(
        CoachOwner owner, CoachWriteOperation record, CancellationToken cancellationToken)
    {
        var claimedVersion = record.Version + 1;
        var claimedAt = UtcNow;
        var operationId = record.Id;
        var conversationId = record.ConversationId;
        var readVersion = record.Version;

        var rows = await _db.CoachWriteOperations
            .Where(o => o.Id == operationId
                        && o.UserProfileId == owner.UserProfileId
                        && o.ConversationId == conversationId
                        && o.Status == CoachWriteOperationStatus.Proposed
                        && o.Version == readVersion)
            .ExecuteUpdateAsync(
                set => set
                    .SetProperty(o => o.Status, CoachWriteOperationStatus.Executing)
                    .SetProperty(o => o.Version, claimedVersion)
                    .SetProperty(o => o.UpdatedAtUtc, claimedAt),
                cancellationToken)
            .ConfigureAwait(false);

        if (rows == 0)
        {
            _logger.LogInformation(
                "[Coach] Write operation {Operation} was already claimed by another approval.", operationId);
            return null;
        }

        _db.ChangeTracker.Clear();
        return await FindOwnedAsync(owner, conversationId, operationId, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Closes a claimed operation that could not complete, so it can never be approved again.
    /// </summary>
    /// <remarks>
    /// Best effort by design: if this save also fails, the row stays <c>Executing</c>, which is
    /// still refused by every later approval. Failing to record the failure must never replace the
    /// exception the caller is already carrying.
    /// </remarks>
    private async Task FailClaimAsync(
        CoachWriteOperation record, string failureCode, CancellationToken cancellationToken)
    {
        try
        {
            var failedAt = UtcNow;
            record.Status = CoachWriteOperationStatus.Failed;
            record.ConfirmationDigest = null;
            record.ConfirmationExpiresAtUtc = null;
            record.UndoExpiresAtUtc = null;
            record.UpdatedAtUtc = failedAt;
            record.Version++;
            AppendAudit(record, CoachWriteAuditEvent.Denied, failedAt, failureCode);
            await SaveAuditedAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _db.ChangeTracker.Clear();
            _logger.LogError(
                ex,
                "[Coach] Write operation {Operation} could not be closed after a failed execution.",
                record.Id);
        }
    }

    // ---------------------------------------------------------------- reject

    /// <summary>Records that the learner declined, so the proposal can never execute.</summary>
    public async Task<bool> RejectAsync(
        string conversationId,
        string operationId,
        CancellationToken cancellationToken = default)
    {
        var owner = RequireOwner("coach_write_reject");
        var record = await FindOwnedAsync(owner, conversationId, operationId, cancellationToken)
            .ConfigureAwait(false);

        if (record is null)
        {
            return false;
        }

        var now = UtcNow;
        if (record.Status != CoachWriteOperationStatus.Proposed)
        {
            await DenyAsync(record, CoachWriteFailureCodes.InvalidState, now, cancellationToken)
                .ConfigureAwait(false);
            return false;
        }

        record.Status = CoachWriteOperationStatus.Rejected;
        record.ConfirmationDigest = null;
        record.ConfirmationExpiresAtUtc = null;
        record.UpdatedAtUtc = now;
        record.Version++;
        AppendAudit(record, CoachWriteAuditEvent.Rejected, now, failureCode: null);

        await SaveAuditedAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    // ---------------------------------------------------------------- undo

    /// <summary>
    /// Reverses an executed operation inside its window, once.
    /// </summary>
    /// <remarks>
    /// The reversal gets its own ledger row and its own audit trail, because an undo is itself a
    /// write to learner data and an audit that recorded only the original would be describing a
    /// state the database is no longer in.
    /// </remarks>
    public async Task<CoachWriteReceipt> UndoAsync(
        string conversationId,
        string operationId,
        CancellationToken cancellationToken = default)
    {
        var owner = RequireOwner("coach_write_undo");
        var record = await FindOwnedAsync(owner, conversationId, operationId, cancellationToken)
            .ConfigureAwait(false);

        if (record is null)
        {
            await AuditOrphanDenialAsync(owner, conversationId, operationId, cancellationToken)
                .ConfigureAwait(false);
            throw Refused("coach_write_undo", "No such change for this learner.");
        }

        var now = UtcNow;

        if (record.Status == CoachWriteOperationStatus.Undone)
        {
            await DenyAsync(record, CoachWriteFailureCodes.UndoConsumed, now, cancellationToken)
                .ConfigureAwait(false);
            throw Refused(record.ToolName, "This change was already undone.");
        }

        if (record.Status != CoachWriteOperationStatus.Executed)
        {
            await DenyAsync(record, CoachWriteFailureCodes.InvalidState, now, cancellationToken)
                .ConfigureAwait(false);
            throw Refused(record.ToolName, "This change has not been carried out.");
        }

        if (record.UndoKind == CoachWriteUndoKind.None || record.ProtectedPriorState is null)
        {
            await DenyAsync(record, CoachWriteFailureCodes.NotReversible, now, cancellationToken)
                .ConfigureAwait(false);
            throw Refused(record.ToolName, "This change cannot be undone.");
        }

        if (record.UndoExpiresAtUtc is null || record.UndoExpiresAtUtc <= now)
        {
            await DenyAsync(record, CoachWriteFailureCodes.UndoExpired, now, cancellationToken)
                .ConfigureAwait(false);
            throw Refused(record.ToolName, "The window for undoing this change has closed.");
        }

        var handler = RequireHandler(record.ToolName);
        var arguments = UnprotectArguments(owner, record);
        var priorState = UnprotectPriorState(owner, record);

        // ----------------------------------------------------------------- claim
        // The undo window is itself the one-use token, so claiming it needs no new column: a
        // single conditional UPDATE clears it, and only the caller whose UPDATE matched a row that
        // still had a live window is allowed to run the reversal. Losers see a spent window and
        // are told so. If the reversal then fails, the window stays spent, which is the point —
        // an undo that half-happened must not be offered again as though nothing had.
        var claimedRecord = await TryClaimUndoAsync(owner, record, now, cancellationToken)
            .ConfigureAwait(false);

        if (claimedRecord is null)
        {
            _db.ChangeTracker.Clear();
            var current = await FindOwnedAsync(owner, conversationId, operationId, cancellationToken)
                .ConfigureAwait(false);

            if (current is not null)
            {
                await DenyAsync(current, CoachWriteFailureCodes.UndoConsumed, UtcNow, cancellationToken)
                    .ConfigureAwait(false);
            }

            throw Refused(
                record.ToolName,
                current?.Status == CoachWriteOperationStatus.Undone
                    ? "This change was already undone."
                    : "This change is already being undone.");
        }

        record = claimedRecord;

        CoachWriteExecution reversal;
        try
        {
            reversal = await handler
                .UndoAsync(owner.UserProfileId, arguments, priorState.StateJson, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await DenyAsync(record, CoachWriteFailureCodes.ExecutionFailed, UtcNow, cancellationToken)
                .ConfigureAwait(false);

            if (ex is CoachToolException)
            {
                throw;
            }

            throw Refused(record.ToolName, "That change could not be undone.");
        }

        var undoneAt = UtcNow;
        var undoId = NewId();
        var summary = BoundLine(reversal.Summary);
        var lines = BoundLines(reversal.Lines);

        var undoRecord = new CoachWriteOperation
        {
            Id = undoId,
            UserProfileId = owner.UserProfileId,
            TenantId = owner.TenantId,
            ConversationId = record.ConversationId,
            // A turn identity of its own, derived from the operation it reverses. A reversal is
            // not a second proposal in the original turn: it asked the learner nothing, and the
            // turn's one card belongs to the row that did — which now reads Undone and carries its
            // own receipt. Sharing the original's turn would also collide with the index that
            // holds a turn to one proposal, so this is load-bearing rather than cosmetic.
            TurnId = Truncate(
                $"{CoachWriteTurnScope.UndoTurnPrefix}{record.Id}", CoachWriteLimits.IdMaxLength),
            ToolName = record.ToolName,
            RiskClass = record.RiskClass,
            Status = CoachWriteOperationStatus.Executed,
            UndoKind = CoachWriteUndoKind.None,
            EntityKind = record.EntityKind,
            EntityId = record.EntityId,
            // Bound to the operation it reverses, so a reversal can never collide with an
            // ordinary proposal and can never itself be reversed a second time.
            IdempotencyKeyDigest = ComputeIdempotencyDigest(
                owner, record.ConversationId, record.ToolName, $"undo:{record.Id}"),
            ProtectedArguments = Protect(
                owner, CoachProtectedContentKind.WriteOperationArguments, undoId, arguments),
            ProtectedPreview = Protect(
                owner,
                CoachProtectedContentKind.WriteOperationPreview,
                undoId,
                CoachNormalizedJson.Serialize(
                    new CoachWriteNarrative(CoachWriteNarrative.CurrentSchemaVersion, summary, lines))),
            ProtectedReceipt = Protect(
                owner,
                CoachProtectedContentKind.WriteOperationReceipt,
                undoId,
                CoachNormalizedJson.Serialize(
                    new CoachWriteNarrative(CoachWriteNarrative.CurrentSchemaVersion, summary, lines))),
            ContentProtectionVersion = _protector.CurrentVersion,
            ExpiresAtUtc = undoneAt.Add(CoachWriteLimits.ProposalLifetime),
            ExecutedAtUtc = undoneAt,
            CreatedAtUtc = undoneAt,
            UpdatedAtUtc = undoneAt,
            Version = 1
        };

        record.Status = CoachWriteOperationStatus.Undone;
        record.UndoneAtUtc = undoneAt;
        record.UndoOperationId = undoId;
        record.UndoExpiresAtUtc = null;
        record.UpdatedAtUtc = undoneAt;
        record.Version++;

        _db.CoachWriteOperations.Add(undoRecord);
        AppendAudit(record, CoachWriteAuditEvent.Undone, undoneAt, failureCode: null);
        AppendAudit(undoRecord, CoachWriteAuditEvent.Executed, undoneAt, failureCode: null);

        try
        {
            await SaveAuditedAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException ex)
        {
            // The claim above is exclusive, so reaching here means the settle itself failed rather
            // than that a second undo raced. The reversal already happened and the window is
            // already spent, so the row is left as it is instead of being retried.
            _db.ChangeTracker.Clear();
            _logger.LogError(
                ex,
                "[Coach] Write operation {Operation} was undone but the reversal could not be recorded.",
                record.Id);

            // Same reasoning as the approval settle: the window is spent, so no second undo can
            // run, and the audit is what turns "spent for no visible reason" into a fact an
            // operator can look up.
            await AuditReceiptFailureAsync(record, cancellationToken).ConfigureAwait(false);

            throw Refused(
                record.ToolName, "That change was undone but its result could not be recorded.");
        }

        return BuildReceipt(owner, undoRecord) with
        {
            Status = CoachWriteOperationStatus.Undone,
            CanUndo = false
        };
    }

    /// <summary>
    /// Spends the undo window with one conditional update, and returns the freshly-read row when
    /// this caller won the right to run the reversal.
    /// </summary>
    /// <remarks>
    /// The predicate insists on the owner, the executed status, a live window, and the version the
    /// caller read, so at most one caller can ever match — including callers in other processes,
    /// which no in-memory lock could reach.
    /// </remarks>
    private async Task<CoachWriteOperation?> TryClaimUndoAsync(
        CoachOwner owner, CoachWriteOperation record, DateTime now, CancellationToken cancellationToken)
    {
        var claimedVersion = record.Version + 1;
        var operationId = record.Id;
        var conversationId = record.ConversationId;
        var readVersion = record.Version;

        var rows = await _db.CoachWriteOperations
            .Where(o => o.Id == operationId
                        && o.UserProfileId == owner.UserProfileId
                        && o.ConversationId == conversationId
                        && o.Status == CoachWriteOperationStatus.Executed
                        && o.UndoExpiresAtUtc != null
                        && o.UndoExpiresAtUtc > now
                        && o.Version == readVersion)
            .ExecuteUpdateAsync(
                set => set
                    .SetProperty(o => o.UndoExpiresAtUtc, (DateTime?)null)
                    .SetProperty(o => o.Version, claimedVersion)
                    .SetProperty(o => o.UpdatedAtUtc, now),
                cancellationToken)
            .ConfigureAwait(false);

        if (rows == 0)
        {
            _logger.LogInformation(
                "[Coach] Undo for write operation {Operation} was already claimed.", operationId);
            return null;
        }

        _db.ChangeTracker.Clear();
        return await FindOwnedAsync(owner, conversationId, operationId, cancellationToken)
            .ConfigureAwait(false);
    }

    // ---------------------------------------------------------------- internals

    private CoachOwner RequireOwner(string toolName)
    {
        // Fails closed before any query. The provider throws when the principal carries no
        // profile claim, and an empty string is treated the same way rather than being allowed
        // through to a filter that would then match every row with a null owner.
        string id;
        try
        {
            id = _userScope.UserProfileId;
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.LogWarning("[Coach] Write operation refused: no user scope.");
            throw new CoachToolException(
                CoachToolFailureKind.Unauthorized, toolName, "The request has no user scope.", ex);
        }

        if (!CoachOwner.TryCreate(id, null, out var owner))
        {
            _logger.LogWarning("[Coach] Write operation refused: empty user scope.");
            throw new CoachToolException(
                CoachToolFailureKind.Unauthorized, toolName, "The request has no user scope.");
        }

        return owner;
    }

    private ICoachWriteHandler RequireHandler(string toolName)
    {
        var handler = _handlers.Find(toolName);
        if (handler is null)
        {
            throw new CoachToolException(
                CoachToolFailureKind.InvalidArgument, toolName, "That change is not available.");
        }

        return handler;
    }

    private void RequireEnabled(string toolName)
    {
        // The registry is the single source of truth for what is switched on. Checking it here
        // means turning the feature off stops queued proposals from executing, not just stops new
        // ones from being made.
        if (!_registry.EnabledNames.Contains(toolName))
        {
            throw new CoachToolException(
                CoachToolFailureKind.InvalidArgument, toolName, "That change is not available.");
        }
    }

    private Task<CoachWriteOperation?> FindOwnedAsync(
        CoachOwner owner, string conversationId, string operationId, CancellationToken cancellationToken) =>
        _db.CoachWriteOperations
            .FirstOrDefaultAsync(
                o => o.UserProfileId == owner.UserProfileId
                     && o.ConversationId == conversationId
                     && o.Id == operationId,
                cancellationToken);

    /// <summary>
    /// Refuses an approval that arrived on the route belonging to the other risk class.
    /// </summary>
    /// <remarks>
    /// The check is on the route, not on whether a secret was supplied. A caller who omits the
    /// confirmation header is not making a soft request; they are making an unconfirmed protected
    /// request, and a soft operation reached through that route must be refused rather than
    /// quietly executed. This runs before any state branch so the two routes stay disjoint even
    /// for rows that already settled.
    /// </remarks>
    private async Task GuardApprovalRouteAsync(
        CoachWriteOperation record,
        CoachWriteApprovalChannel channel,
        DateTime now,
        CancellationToken cancellationToken)
    {
        var isProtected = record.RiskClass == CoachToolRiskClass.WriteHard;
        var arrivedOnConfirm = channel == CoachWriteApprovalChannel.Confirm;

        if (isProtected == arrivedOnConfirm)
        {
            return;
        }

        await DenyAsync(record, CoachWriteFailureCodes.WrongAcceptanceChannel, now, cancellationToken)
            .ConfigureAwait(false);

        throw Refused(
            record.ToolName,
            isProtected
                ? "This change needs an explicit confirmation."
                : "This change is accepted, not confirmed.");
    }

    private async Task GuardApprovalSecretAsync(
        CoachOwner owner,
        CoachWriteOperation record,
        string arguments,
        string? confirmationSecret,
        DateTime now,
        CancellationToken cancellationToken)
    {
        // The route already matched the risk class, so a soft operation here is on the soft
        // route. It carries no secret, and anything presented is refused rather than ignored.
        if (record.RiskClass != CoachToolRiskClass.WriteHard)
        {
            if (confirmationSecret is not null)
            {
                await DenyAsync(record, CoachWriteFailureCodes.WrongAcceptanceChannel, now, cancellationToken)
                    .ConfigureAwait(false);
                throw Refused(record.ToolName, "This change is accepted, not confirmed.");
            }

            return;
        }

        if (string.IsNullOrWhiteSpace(confirmationSecret))
        {
            await DenyAsync(record, CoachWriteFailureCodes.ConfirmationRequired, now, cancellationToken)
                .ConfigureAwait(false);
            throw Refused(record.ToolName, "This change needs an explicit confirmation.");
        }

        if (record.ConfirmationDigest is null)
        {
            await DenyAsync(record, CoachWriteFailureCodes.ConfirmationConsumed, now, cancellationToken)
                .ConfigureAwait(false);
            throw Refused(record.ToolName, "That confirmation is no longer valid.");
        }

        if (record.ConfirmationExpiresAtUtc is null || record.ConfirmationExpiresAtUtc <= now)
        {
            await DenyAsync(record, CoachWriteFailureCodes.ConfirmationExpired, now, cancellationToken)
                .ConfigureAwait(false);
            throw Refused(record.ToolName, "That confirmation has expired.");
        }

        // Recomputed from the owner, conversation, operation, and the arguments as recorded —
        // not from anything the caller supplied. A secret minted for another learner, another
        // conversation, another operation, or the same operation before its arguments were
        // rewritten produces a different digest and is refused here.
        var expected = ComputeConfirmationDigest(owner, record, arguments, confirmationSecret!);
        if (!FixedTimeEquals(expected, record.ConfirmationDigest))
        {
            await DenyAsync(record, CoachWriteFailureCodes.ConfirmationMismatch, now, cancellationToken)
                .ConfigureAwait(false);
            throw Refused(record.ToolName, "That confirmation is not valid for this change.");
        }
    }

    /// <summary>
    /// Refuses a proposal that would be the turn's second, before any row is written.
    /// </summary>
    /// <remarks>
    /// <para>
    /// One row per turn, counted in the database rather than assumed from what this request has
    /// seen. Two calls in the same turn arrive on the same scope but not necessarily in the same
    /// order the model made them, and a second API process would share neither. The count is the
    /// only thing both of them can agree on.
    /// </para>
    /// <para>
    /// Every status counts, including rows that have already settled. The surface shows one card
    /// per turn whatever state it is in, so a turn that recorded an approved change and then
    /// recorded a second proposal would hide one of them — and the hidden one would be the
    /// receipt for something that actually happened.
    /// </para>
    /// <para>
    /// The refusal is audited but writes no operation row. A refused proposal must leave a trace
    /// an operator can find and nothing a learner can approve; those are different requirements
    /// and this is the only place that satisfies both.
    /// </para>
    /// </remarks>
    private async Task GuardProposalBudgetAsync(
        CoachOwner owner,
        string conversationId,
        string turnId,
        string toolName,
        string keyDigest,
        DateTime now,
        CancellationToken cancellationToken)
    {
        // No blank-turn escape hatch. The caller has already refused a proposal with no turn to
        // belong to, so every proposal that reaches here is counted — a request that could skip
        // the count would be a write path with the shared twenty-call budget as its only bound,
        // which is not the same bound and is not write-only.
        //
        // The request's own idempotency digest is excluded. Two identical calls racing both read
        // no existing row, and the loser must reach the unique index and be answered from the
        // winner's row; counting the winner against it here would turn a replay into a refusal and
        // break the one guarantee a repeat is supposed to have. A row released by an earlier
        // decline is not excluded — its digest has already been moved aside — so it still holds
        // the turn's single slot, which is correct: the turn already put a card in front of the
        // learner and cannot put a second one there.
        var open = await _db.CoachWriteOperations
            .CountAsync(
                o => o.UserProfileId == owner.UserProfileId
                     && o.ConversationId == conversationId
                     && o.TurnId == turnId
                     && o.IdempotencyKeyDigest != keyDigest,
                cancellationToken)
            .ConfigureAwait(false);

        if (open >= CoachWriteLimits.ProposalsPerTurnMax)
        {
            _logger.LogWarning(
                "[Coach] Write proposal refused: this turn has already proposed a change ({Tool}).", toolName);

            await AuditProposalBudgetRefusalAsync(owner, conversationId, turnId, toolName, cancellationToken)
                .ConfigureAwait(false);

            // Named as a bound, not as a fault. The model asked for something reasonable at the
            // wrong moment, and the sentence has to be one it can act on: stop proposing, say what
            // is already waiting, and let the learner answer it.
            throw new CoachToolException(
                CoachToolFailureKind.BudgetExhausted,
                toolName,
                "Only one change can be proposed per turn, and this turn already proposed one. "
                + "Tell the learner what is waiting and let them answer it first.");
        }
    }

    /// <summary>
    /// Records that a second proposal in one turn was refused, without creating an operation.
    /// </summary>
    /// <remarks>
    /// Best effort, and swallowed on failure: the refusal is already correct without it, and
    /// turning a clean bounded refusal into a server error to report a bounded refusal would be
    /// the worse outcome. The row carries the turn and the tool name and no arguments, so a
    /// repeated pattern is diagnosable without the audit becoming a copy of what the model asked
    /// for.
    /// </remarks>
    private async Task AuditProposalBudgetRefusalAsync(
        CoachOwner owner,
        string conversationId,
        string turnId,
        string toolName,
        CancellationToken cancellationToken)
    {
        try
        {
            _db.ChangeTracker.Clear();

            // Routed through the shared helper rather than adding the row inline. Both audit
            // writers now go through exactly two methods, and both of those queue the ledger
            // signal — so `one_proposal_per_turn`, which is one of the most product-relevant
            // refusals Sam produces, cannot be silently uncovered by the one call site that used
            // to build its own row.
            AddStandaloneAudit(
                owner,
                conversationId,
                operationId: string.Empty,
                CoachWriteFailureCodes.ProposalBudgetExhausted,
                toolName: toolName,
                turnId: turnId);

            await SaveAuditedAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _db.ChangeTracker.Clear();
            _logger.LogError(
                ex, "[Coach] A refused second proposal on turn {Turn} could not be audited.", turnId);
        }
    }

    /// <summary>
    /// Answers a repeated request from the row that already exists, or releases that row's
    /// idempotency slot and returns null so the caller can record a genuinely new proposal.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the whole of the status-aware idempotency rule, in one place, so that no caller can
    /// hold a different opinion about what an existing row means.
    /// </para>
    /// <para>
    /// A live proposal replays itself. An operation whose execution claim is outstanding refuses:
    /// the domain write may already have happened, so neither a receipt nor a second proposal
    /// would be true. An executed operation replays its stored receipt, which is the authoritative
    /// record of what was done. Everything else is closed and left no effect, so it releases the
    /// slot — including a proposal that sat unanswered past its window, which is expired first so
    /// that its refusal is recorded before its slot is freed.
    /// </para>
    /// </remarks>
    private async Task<CoachWriteProposalResult?> ReplayOrReleaseAsync(
        CoachOwner owner,
        CoachWriteOperation record,
        string keyDigest,
        DateTime now,
        CancellationToken cancellationToken)
    {
        if (CoachWriteOperationStates.IsInFlight(record.Status))
        {
            await DenyAsync(record, CoachWriteFailureCodes.ExecutionInDoubt, now, cancellationToken)
                .ConfigureAwait(false);
            throw Refused(record.ToolName, "This change is already being carried out.");
        }

        if (CoachWriteOperationStates.IsEffective(record.Status))
        {
            return Describe(owner, record, isDuplicate: true);
        }

        if (CoachWriteOperationStates.IsOpen(record.Status) && record.ExpiresAtUtc > now)
        {
            return Describe(owner, record, isDuplicate: true);
        }

        if (CoachWriteOperationStates.IsOpen(record.Status))
        {
            await ExpireAsync(record, now, cancellationToken).ConfigureAwait(false);
        }

        await ReleaseIdempotencyAsync(owner, record, keyDigest, cancellationToken).ConfigureAwait(false);
        return null;
    }

    /// <summary>
    /// Moves a closed, ineffective operation out of the idempotency namespace so the request it
    /// failed to carry out can be asked again.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The row is kept, with its audit trail and its identifier intact; only the digest changes,
    /// to a value derived from the operation id and therefore unique to that row. The same shape
    /// the undo ledger already uses for its own rows, and for the same reason: the digest is a
    /// namespace for open claims on a request, not a permanent label.
    /// </para>
    /// <para>
    /// A single conditional update, keyed on the digest still being the one this caller read and
    /// the status still being one that left no effect. Two requests releasing the same row resolve
    /// to one winner; a request that raced an approval — which can only move a row out of
    /// <c>Proposed</c>, never into a closed status behind our back — matches nothing and re-reads.
    /// The concurrency token is deliberately left alone: releasing changes nothing an approval or
    /// an undo can act on, so invalidating a version another request is holding would create a
    /// conflict where there is no conflict.
    /// </para>
    /// </remarks>
    private async Task<bool> ReleaseIdempotencyAsync(
        CoachOwner owner, CoachWriteOperation record, string keyDigest, CancellationToken cancellationToken)
    {
        var operationId = record.Id;
        var conversationId = record.ConversationId;
        var released = ComputeIdempotencyDigest(
            owner, conversationId, record.ToolName, $"released:{operationId}");
        var releasedAt = UtcNow;

        var rows = await _db.CoachWriteOperations
            .Where(o => o.Id == operationId
                        && o.UserProfileId == owner.UserProfileId
                        && o.ConversationId == conversationId
                        && o.IdempotencyKeyDigest == keyDigest
                        && (o.Status == CoachWriteOperationStatus.Undone
                            || o.Status == CoachWriteOperationStatus.Rejected
                            || o.Status == CoachWriteOperationStatus.Expired
                            || o.Status == CoachWriteOperationStatus.Failed))
            .ExecuteUpdateAsync(
                set => set
                    .SetProperty(o => o.IdempotencyKeyDigest, released)
                    .SetProperty(o => o.UpdatedAtUtc, releasedAt),
                cancellationToken)
            .ConfigureAwait(false);

        if (rows == 0)
        {
            _logger.LogInformation(
                "[Coach] Write operation {Operation} had already released its proposal slot.", operationId);
            _db.ChangeTracker.Clear();
            return false;
        }

        // The tracked copy of this row still carries the digest that was just replaced, and its
        // concurrency token was deliberately not moved. Saving it again would put the old digest
        // back and undo the release, so the entity is detached here rather than being left for a
        // caller to remember. The clear belongs to the method that invalidated the entity.
        _db.ChangeTracker.Clear();

        _logger.LogInformation(
            "[Coach] Write operation {Operation} released its proposal slot after closing without effect.",
            operationId);
        return true;
    }

    private async Task ExpireAsync(CoachWriteOperation record, DateTime now, CancellationToken cancellationToken)
    {
        record.Status = CoachWriteOperationStatus.Expired;
        record.ConfirmationDigest = null;
        record.ConfirmationExpiresAtUtc = null;
        record.UpdatedAtUtc = now;
        record.Version++;
        AppendAudit(record, CoachWriteAuditEvent.Denied, now, CoachWriteFailureCodes.ProposalExpired);

        try
        {
            await SaveAuditedAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateConcurrencyException)
        {
            // Another request expired the same proposal first. The row is already in the state
            // this method wanted, so the caller's refusal — which is about the expiry, not about
            // who recorded it — stands unchanged.
            //
            // The audit does not stand unchanged, though. Clearing the tracker to recover from the
            // conflict discards the audit row queued above along with the losing update, and an
            // expiry that left no trace is indistinguishable from one that never happened. The
            // evidence is written again on a clean tracker, so the audit records every request
            // that was refused for expiry rather than only the one that won the race.
            _db.ChangeTracker.Clear();
            _logger.LogInformation(
                "[Coach] Write operation {Operation} was expired concurrently.", record.Id);

            await AuditDetachedAsync(
                record,
                CoachWriteAuditEvent.Denied,
                now,
                CoachWriteFailureCodes.ProposalExpired,
                cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task DenyAsync(
        CoachWriteOperation record, string failureCode, DateTime now, CancellationToken cancellationToken)
    {
        AppendAudit(record, CoachWriteAuditEvent.Denied, now, failureCode);
        await SaveAuditedAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task AuditOrphanDenialAsync(
        CoachOwner owner, string conversationId, string operationId, CancellationToken cancellationToken)
    {
        // A refusal for an operation that does not exist still has to leave a trace, because that
        // is exactly the shape a cross-tenant probe takes. The row carries no payload and the
        // identifiers are opaque.
        AddStandaloneAudit(
            owner, conversationId, operationId, CoachWriteFailureCodes.OperationNotFound);

        await SaveAuditedAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Records that an approval never ran because another approval held the claim.
    /// </summary>
    /// <remarks>
    /// Written from the loser's side and deliberately not from the operation row, which the
    /// winner owns and may be mid-update. Best effort: failing to record a refusal must not turn
    /// a clean refusal into a server error.
    /// </remarks>
    private async Task AuditClaimLossAsync(
        CoachOwner owner, string conversationId, string operationId, CancellationToken cancellationToken)
    {
        try
        {
            _db.ChangeTracker.Clear();
            AddStandaloneAudit(owner, conversationId, operationId, CoachWriteFailureCodes.ClaimLost);
            await SaveAuditedAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _db.ChangeTracker.Clear();
            _logger.LogError(
                ex, "[Coach] A lost execution claim for {Operation} could not be audited.", operationId);
        }
    }

    /// <summary>
    /// Records that a domain write completed but its ledger settle did not.
    /// </summary>
    /// <remarks>
    /// The row itself is left where the failure left it — <c>Executing</c> after an approval,
    /// window-spent after an undo — because both of those are already un-runnable. What was
    /// missing was any way to find out why, so this writes one audit row carrying the operation
    /// id and <see cref="CoachWriteFailureCodes.ReceiptNotRecorded"/> and nothing else.
    /// </remarks>
    private Task AuditReceiptFailureAsync(
        CoachWriteOperation record, CancellationToken cancellationToken) =>
        AuditDetachedAsync(
            record,
            CoachWriteAuditEvent.Denied,
            UtcNow,
            CoachWriteFailureCodes.ReceiptNotRecorded,
            cancellationToken);

    /// <summary>
    /// Writes one audit row for an operation whose own save has just failed or been abandoned.
    /// </summary>
    /// <remarks>
    /// Written on a cleared change tracker so it does not carry the entities whose save just
    /// failed, and swallowed on failure so it can never replace the answer the caller is already
    /// carrying. The values come from the in-memory record, which stays readable after the
    /// tracker is cleared.
    /// </remarks>
    private async Task AuditDetachedAsync(
        CoachWriteOperation record,
        CoachWriteAuditEvent auditEvent,
        DateTime now,
        string? failureCode,
        CancellationToken cancellationToken)
    {
        try
        {
            _db.ChangeTracker.Clear();
            AppendAudit(record, auditEvent, now, failureCode);
            await SaveAuditedAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _db.ChangeTracker.Clear();
            _logger.LogError(
                ex,
                "[Coach] An audit row for write operation {Operation} could not be written.",
                record.Id);
        }
    }

    /// <summary>
    /// Records that a repeated proposal gave up after the idempotency slot changed hands under it.
    /// </summary>
    /// <remarks>
    /// Its own code rather than a borrowed one. Losing a slot repeatedly is a contention outcome,
    /// not a rejected request, and an operator reading the audit has to be able to tell a learner
    /// who was refused from a learner whose two tabs argued.
    /// </remarks>
    private async Task AuditProposalContentionAsync(
        CoachOwner owner, string conversationId, CancellationToken cancellationToken)
    {
        try
        {
            _db.ChangeTracker.Clear();
            AddStandaloneAudit(
                owner, conversationId, operationId: string.Empty, CoachWriteFailureCodes.ClaimLost);
            await SaveAuditedAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _db.ChangeTracker.Clear();

            // Shape only. A persistence failure on this path can quote the row it was writing,
            // and the write ledger holds learner-authored proposal payloads. See
            // CoachExceptionSanitizer.
            var facts = CoachExceptionSanitizer.Describe(ex);
            _logger.LogError(
                "[Coach] A contended write proposal could not be audited. " +
                "Category={FailureCategory} ProviderStatus={ProviderStatus} " +
                "ProviderCode={ProviderErrorCode} InnerDepth={InnerDepth}",
                facts.Category,
                facts.ProviderStatus,
                facts.ProviderErrorCode,
                facts.InnerDepth);
        }
    }

    /// <summary>Adds an audit row that is not attached to a loaded operation.</summary>
    private void AddStandaloneAudit(
        CoachOwner owner,
        string conversationId,
        string operationId,
        string failureCode,
        string? toolName = null,
        string? turnId = null,
        CoachToolRiskClass riskClass = CoachToolRiskClass.WriteHard,
        string? settingName = null)
    {
        _db.CoachWriteAudits.Add(new CoachWriteAudit
        {
            Id = NewId(),
            OperationId = Truncate(operationId, CoachWriteLimits.IdMaxLength) ?? string.Empty,
            UserProfileId = owner.UserProfileId,
            TenantId = owner.TenantId,
            ConversationId = Truncate(conversationId, CoachWriteLimits.IdMaxLength) ?? string.Empty,
            TurnId = Truncate(turnId, CoachWriteLimits.IdMaxLength),
            ToolName = Truncate(toolName, CoachWriteLimits.ToolNameMaxLength) ?? "coach_write_operation",
            RiskClass = riskClass,
            Event = CoachWriteAuditEvent.Denied,
            EntityKind = CoachWriteEntityKind.None,
            FailureCode = failureCode,
            CreatedAtUtc = UtcNow
        });

        QueueOpportunity(failureCode, toolName, conversationId, turnId, operationId, settingName);
    }

    private void AppendAudit(
        CoachWriteOperation record, CoachWriteAuditEvent auditEvent, DateTime now, string? failureCode)
    {
        _db.CoachWriteAudits.Add(new CoachWriteAudit
        {
            Id = NewId(),
            OperationId = record.Id,
            UserProfileId = record.UserProfileId,
            TenantId = record.TenantId,
            ConversationId = record.ConversationId,
            TurnId = record.TurnId,
            ToolName = record.ToolName,
            RiskClass = record.RiskClass,
            Event = auditEvent,
            EntityKind = record.EntityKind,
            EntityId = record.EntityId,
            FailureCode = failureCode,
            CreatedAtUtc = now
        });

        // Only a Denied event carries a failure code, so a successful proposal, execution,
        // reversal, or replay maps to nothing and no ledger row is queued.
        QueueOpportunity(
            failureCode, record.ToolName, record.ConversationId, record.TurnId, record.Id);
    }

    private CoachWriteProposalResult Describe(CoachOwner owner, CoachWriteOperation record, bool isDuplicate)
    {
        // AlreadyExecuted means the change is in place now, not that it once was. An undone
        // operation has a receipt and the learner does not have the change, so answering true
        // there would tell the model — and the card that reads the same field — that something is
        // done which the learner has since reversed. The receipt narrative goes with it: a
        // reversed operation is described by what it proposed, and the fact that it ran and was
        // put back is carried by the receipt route, which reports the status explicitly.
        var effective = CoachWriteOperationStates.IsEffective(record.Status);
        var narrative = effective && record.ProtectedReceipt is not null
            ? UnprotectReceipt(owner, record)
            : UnprotectNarrative(owner, record);

        return new CoachWriteProposalResult(
            record.Id,
            record.ToolName,
            record.RiskClass == CoachToolRiskClass.WriteHard
                ? CoachWriteApprovalModes.Confirm
                : CoachWriteApprovalModes.Accept,
            narrative.Summary,
            narrative.Lines,
            record.ExpiresAtUtc,
            isDuplicate,
            effective);
    }

    private CoachWriteReceipt BuildReceipt(CoachOwner owner, CoachWriteOperation record)
    {
        var narrative = record.ProtectedReceipt is not null
            ? UnprotectReceipt(owner, record)
            : UnprotectNarrative(owner, record);

        return new CoachWriteReceipt(
            record.Id,
            record.ToolName,
            record.RiskClass,
            record.Status,
            record.EntityKind,
            record.EntityId,
            narrative.Summary,
            narrative.Lines,
            record.ExecutedAtUtc ?? record.UpdatedAtUtc,
            record.Status == CoachWriteOperationStatus.Executed
                && record.UndoKind != CoachWriteUndoKind.None
                && record.UndoExpiresAtUtc is not null
                && record.UndoExpiresAtUtc > UtcNow,
            record.UndoExpiresAtUtc,
            record.UndoOperationId);
    }

    private string Protect(
        CoachOwner owner, CoachProtectedContentKind kind, string recordId, string plaintext) =>
        _protector.Protect(
            new CoachProtectionContext(owner, kind, recordId, _protector.CurrentVersion), plaintext);

    private string UnprotectArguments(CoachOwner owner, CoachWriteOperation record) =>
        Unprotect(owner, CoachProtectedContentKind.WriteOperationArguments, record, record.ProtectedArguments);

    private CoachWriteNarrative UnprotectNarrative(CoachOwner owner, CoachWriteOperation record) =>
        ReadNarrative(
            Unprotect(owner, CoachProtectedContentKind.WriteOperationPreview, record, record.ProtectedPreview),
            record.ToolName);

    private CoachWriteNarrative UnprotectReceipt(CoachOwner owner, CoachWriteOperation record) =>
        ReadNarrative(
            Unprotect(owner, CoachProtectedContentKind.WriteOperationReceipt, record, record.ProtectedReceipt!),
            record.ToolName);

    private CoachWritePriorState UnprotectPriorState(CoachOwner owner, CoachWriteOperation record)
    {
        var json = Unprotect(
            owner, CoachProtectedContentKind.WriteOperationPriorState, record, record.ProtectedPriorState!);
        var state = CoachNormalizedJson.Deserialize<CoachWritePriorState>(json);

        // An unknown schema is refused rather than guessed at. Undo restores learner data, so
        // "probably close enough" is the one answer that must never be available.
        if (state is null || state.SchemaVersion != CoachWritePriorState.CurrentSchemaVersion)
        {
            throw Refused(record.ToolName, "The undo record is from an older version.");
        }

        return state;
    }

    private CoachWriteNarrative ReadNarrative(string json, string toolName)
    {
        var narrative = CoachNormalizedJson.Deserialize<CoachWriteNarrative>(json);
        if (narrative is null || narrative.SchemaVersion != CoachWriteNarrative.CurrentSchemaVersion)
        {
            throw Refused(toolName, "This record is from an older version.");
        }

        return narrative;
    }

    private string Unprotect(
        CoachOwner owner, CoachProtectedContentKind kind, CoachWriteOperation record, string ciphertext)
    {
        var context = new CoachProtectionContext(owner, kind, record.Id, record.ContentProtectionVersion);
        if (!_protector.TryUnprotect(context, ciphertext, out var plaintext) || plaintext is null)
        {
            // Wrong key ring, wrong owner, or a tampered row. All three are the same answer.
            _logger.LogWarning(
                "[Coach] Write operation payload could not be read for {Tool}.", record.ToolName);
            throw Refused(record.ToolName, "This record could not be read.");
        }

        return plaintext;
    }

    private static void GuardPayloadSize(string toolName, string payload, int maxBytes)
    {
        if (Encoding.UTF8.GetByteCount(payload) > maxBytes)
        {
            throw new CoachToolException(
                CoachToolFailureKind.InvalidArgument, toolName, "The change is too large to record.");
        }
    }

    private static CoachToolException Refused(string toolName, string reason) =>
        new(CoachToolFailureKind.InvalidArgument, toolName, reason);

    private static string NewId() => Guid.NewGuid().ToString("N");

    private static string NewSecret() =>
        Base64Url(RandomNumberGenerator.GetBytes(32));

    /// <summary>
    /// Owner- and conversation-bound digest of a tool call. The same arguments in another
    /// conversation, or for another learner, hash to an unrelated value, so an idempotency
    /// collision can only ever happen inside one learner's own conversation.
    /// </summary>
    private static string ComputeIdempotencyDigest(
        CoachOwner owner, string conversationId, string toolName, string canonicalArguments)
    {
        var material = $"{owner.UserProfileId}\u001f{conversationId}\u001f{toolName}\u001f{canonicalArguments}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material)));
    }

    /// <summary>
    /// Binds a confirmation secret to the learner, the conversation, the operation, the tool, and
    /// the exact arguments recorded at proposal time.
    /// </summary>
    /// <remarks>
    /// Every one of those is load-bearing. Dropping the owner would let a captured secret be
    /// redeemed by another learner; dropping the conversation or operation would let one secret
    /// approve a different pending change; dropping the arguments would let a secret minted for a
    /// small edit approve a rewritten one.
    /// </remarks>
    private static string ComputeConfirmationDigest(
        CoachOwner owner, CoachWriteOperation record, string canonicalArguments, string secret)
    {
        var material =
            $"{owner.UserProfileId}\u001f{record.ConversationId}\u001f{record.Id}\u001f{record.ToolName}\u001f{canonicalArguments}";
        var key = Encoding.UTF8.GetBytes(secret);
        return Convert.ToHexString(HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(material)));
    }

    private static bool FixedTimeEquals(string left, string right)
    {
        var a = Encoding.UTF8.GetBytes(left);
        var b = Encoding.UTF8.GetBytes(right);
        return CryptographicOperations.FixedTimeEquals(a, b);
    }

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string BoundLine(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? string.Empty
            : value.Length <= CoachWriteLimits.LineMaxLength
                ? value
                : value[..CoachWriteLimits.LineMaxLength];

    private static IReadOnlyList<string> BoundLines(IReadOnlyList<string>? lines)
    {
        if (lines is null || lines.Count == 0)
        {
            return Array.Empty<string>();
        }

        var take = Math.Min(lines.Count, CoachWriteLimits.LineMax);
        var result = new List<string>(take);
        for (var i = 0; i < take; i++)
        {
            result.Add(BoundLine(lines[i]));
        }

        return result;
    }

    private static string? Truncate(string? value, int maxLength) =>
        string.IsNullOrWhiteSpace(value)
            ? null
            : value.Length <= maxLength ? value : value[..maxLength];
}

/// <summary>
/// The one-use confirmation a protected write needs, handed to the owner and to nobody else.
/// </summary>
public sealed record CoachWriteConfirmationChallenge(
    string OperationId,
    string ToolName,
    string ConfirmationSecret,
    string Summary,
    IReadOnlyList<string> Lines,
    DateTime ExpiresAtUtc);
