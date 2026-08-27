using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SentenceStudio.Api.Coach.Application;
using SentenceStudio.Api.Coach.Operations;
using SentenceStudio.Api.Coach.Opportunities;
using SentenceStudio.Api.Coach.Persistence;
using SentenceStudio.Api.Coach.Persistence.History;
using SentenceStudio.Api.Coach.Telemetry;
using SentenceStudio.Api.Coach.Tools;
using SentenceStudio.Contracts.Coach;
using SentenceStudio.Services.Plans;

namespace SentenceStudio.Api.Coach.Reports;

/// <summary>
/// Records a learner's report that one coach response did not serve them.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is a learner action, not a model capability.</b> Nothing here is reachable from a tool,
/// an agent, or a model completion: the only entry point is an authenticated HTTP route the
/// learner's own client calls, the owner is derived from the request scope, and no argument is
/// ever taken from anything the model produced. That is what makes the ledger row it raises
/// trustworthy — a model that could file reports about itself would be a model with a channel
/// into the product backlog.
/// </para>
/// <para>
/// <b>Nothing on this path decrypts a message.</b> The pairing check reads identifiers, roles,
/// kinds, sequences, and the operation correlation off the ledger rows; the encrypted payloads
/// are never opened. The one decryption anywhere near this feature is the operator's explicit,
/// counted evidence reveal, which happens later and elsewhere.
/// </para>
/// </remarks>
public sealed class CoachResponseReportService
{
    private readonly CoachDbContext _db;
    private readonly IUserScopeProvider _userScope;
    private readonly ICoachTurnOperationStore _operations;
    private readonly ICoachToolRegistry _registry;
    private readonly ICoachOpportunityRecorder _recorder;
    private readonly IOptionsMonitor<CoachResponseReportOptions> _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<CoachResponseReportService> _logger;

    private static readonly JsonSerializerOptions OutcomeJson = new(JsonSerializerDefaults.Web);

    public CoachResponseReportService(
        CoachDbContext db,
        IUserScopeProvider userScope,
        ICoachTurnOperationStore operations,
        ICoachToolRegistry registry,
        ICoachOpportunityRecorder recorder,
        IOptionsMonitor<CoachResponseReportOptions> options,
        TimeProvider timeProvider,
        ILogger<CoachResponseReportService> logger)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _userScope = userScope ?? throw new ArgumentNullException(nameof(userScope));
        _operations = operations ?? throw new ArgumentNullException(nameof(operations));
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _recorder = recorder ?? throw new ArgumentNullException(nameof(recorder));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>Whether reporting is switched on for this deployment.</summary>
    public bool IsEnabled => _options.CurrentValue.Enabled;

    // ------------------------------------------------------------------ read

    /// <summary>
    /// Returns the coach responses this learner has already reported in one conversation.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The client cannot derive this. A browser that forgot everything must still render
    /// "Reported for review" on exactly the responses it did before the reload, and only the
    /// server knows which those are.
    /// </para>
    /// <para>
    /// Owner-scoped, and an unknown or foreign conversation answers the same empty list a real
    /// but unreported conversation does. <b>That is deliberate and it is the whole reason this
    /// route cannot be used as an existence oracle:</b> the response shape carries no "found" bit
    /// for a caller to read.
    /// </para>
    /// </remarks>
    public async Task<CoachOperationResult<CoachReportedResponsesDto>> ListReportedAsync(
        string conversationId,
        CancellationToken cancellationToken = default)
    {
        if (!IsEnabled)
        {
            return Unavailable<CoachReportedResponsesDto>();
        }

        if (!TryResolveOwner(out var owner) || !IsWellFormedId(conversationId))
        {
            // No owner and a malformed id both answer the empty list rather than an error. A
            // conversation with nothing reported in it answers the same thing, so a caller
            // learns nothing from either.
            return CoachOperationResult<CoachReportedResponsesDto>.Ok(new CoachReportedResponsesDto());
        }

        var trimmed = conversationId.Trim();

        var ids = await _db.CoachResponseReports
            .AsNoTracking()
            .Where(row => row.UserProfileId == owner.UserProfileId && row.ConversationId == trimmed)
            .OrderBy(row => row.CoachMessageSequence)
            .Select(row => row.CoachMessageId)
            .Take(CoachResponseReportLimits.ReportedResponsePageSize)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return CoachOperationResult<CoachReportedResponsesDto>.Ok(
            new CoachReportedResponsesDto { MessageIds = ids });
    }

    // ------------------------------------------------------------------ write

    /// <summary>
    /// Records one report, or reports that one already existed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Idempotent per (learner, coach response), regardless of reason.</b> That choice, rather
    /// than per (learner, response, reason), is what makes the learner-facing state expressible:
    /// a response is reported or it is not, and "Reported for review" is a fact about the response
    /// rather than about a reason the learner cannot see afterwards. Reporting the same response
    /// again — from a second device, after a reload, or by pressing twice — answers
    /// <see cref="CoachResponseReportState.AlreadyReported"/> with the reason that won, and
    /// changes nothing.
    /// </para>
    /// <para>
    /// <b>Race-safe across instances by the database, not by this code.</b> Two replicas can both
    /// pass the pre-check; the unique index on
    /// <c>(UserProfileId, CoachMessageId)</c> is what decides which one wrote it, and the loser
    /// reads the winner's row and returns the same answer a reload would get. A read-then-write
    /// in either process could not have made that guarantee.
    /// </para>
    /// </remarks>
    public async Task<CoachOperationResult<CoachResponseReportResponse>> ReportAsync(
        string conversationId,
        string coachMessageId,
        CoachResponseReportRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!IsEnabled)
        {
            return Unavailable<CoachResponseReportResponse>();
        }

        if (!Enum.IsDefined(request.Reason))
        {
            return CoachOperationResult<CoachResponseReportResponse>.Problem(
                CoachOperationStatus.InvalidInput,
                CoachProblemTypes.InvalidTurnInput,
                "The report reason is not one this server accepts.");
        }

        if (!IsWellFormedId(conversationId) || !IsWellFormedId(coachMessageId))
        {
            return CoachOperationResult<CoachResponseReportResponse>.Problem(
                CoachOperationStatus.InvalidInput,
                CoachProblemTypes.InvalidTurnInput,
                "A report names a conversation and a response.");
        }

        if (!TryResolveOwner(out var owner))
        {
            // No trusted owner reads exactly like an unknown conversation. The alternative — a
            // distinct 401-shaped answer — would let an unauthenticated probe tell a real
            // conversation id from an invented one.
            return NotFound<CoachResponseReportResponse>();
        }

        var conversation = conversationId.Trim();
        var responseId = coachMessageId.Trim();

        var response = await _db.CoachMessages
            .AsNoTracking()
            .Where(m => m.UserProfileId == owner.UserProfileId
                        && m.ConversationId == conversation
                        && m.Id == responseId)
            .Select(m => new MessageFacts(m.Id, m.Sequence, m.Role, m.Kind, m.OperationId))
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        // Absent, foreign, or in another conversation are one answer. A caller cannot tell which,
        // which is what stops this route being an existence oracle for message identifiers.
        if (response is null)
        {
            return NotFound<CoachResponseReportResponse>();
        }

        // Ownership of the response is established by this point, so a refusal below tells the
        // caller only about their own data. That is what lets the error be truthful instead of
        // another indistinguishable 404: they are entitled to know this message of theirs cannot
        // be paired to a request.

        // Kind first, because it is the one refusal that does not depend on a second read. A
        // receipt is the record of a change applied to the learner's own data rather than the
        // coach answering them, and a quarrel with it belongs to the surface that can undo it.
        // The client withholds the flag for the same reason; this is here so the rule holds for a
        // request that did not come from the client.
        if (!CoachResponseReportability.IsReportableKind(response.Kind))
        {
            _logger.LogInformation(
                "[Coach] A response report was refused: this kind of message is not a reportable " +
                "response. ResponseKind={ResponseKind}",
                response.Kind);

            return CoachOperationResult<CoachResponseReportResponse>.Problem(
                CoachOperationStatus.InvalidInput,
                CoachProblemTypes.InvalidTurnInput,
                "This message is a record of a change rather than a response, so it was not reported.");
        }

        var learner = await FindPairedRequestAsync(owner, conversation, response, cancellationToken)
            .ConfigureAwait(false);

        if (learner is null)
        {
            _logger.LogInformation(
                "[Coach] A response report was refused: the response could not be paired to a " +
                "request. ResponseRole={ResponseRole} HasTurnCorrelation={HasTurnCorrelation}",
                response.Role,
                response.OperationId is { Length: > 0 });

            return CoachOperationResult<CoachResponseReportResponse>.Problem(
                CoachOperationStatus.InvalidInput,
                CoachProblemTypes.InvalidTurnInput,
                "This response could not be matched to the request it answered, so it was not reported.");
        }

        var existing = await FindExistingAsync(owner, responseId, cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            return Replay(existing);
        }

        var evidence = await GatherAsync(owner, conversation, response, cancellationToken).ConfigureAwait(false);
        var now = _timeProvider.GetUtcNow().UtcDateTime;

        var row = new CoachResponseReport
        {
            Id = Guid.NewGuid().ToString("n"),
            UserProfileId = owner.UserProfileId,
            TenantId = null,
            ConversationId = conversation,
            CoachMessageId = responseId,
            CoachMessageSequence = response.Sequence,
            RequestMessageId = learner.Id,
            RequestMessageSequence = learner.Sequence,
            Reason = request.Reason,
            ResponseKind = response.Kind,
            TurnOperationId = response.OperationId,
            TurnStatus = evidence.TurnStatus,
            StopReason = evidence.StopReason,
            TurnAttemptCount = evidence.AttemptCount,
            TurnErrorCode = evidence.TurnErrorCode,
            InvokedToolNames = evidence.InvokedToolNames,
            WriteOperationId = evidence.WriteOperationId,
            WriteStatus = evidence.WriteStatus,
            WriteFailureCode = evidence.WriteFailureCode,
            OpportunityId = null,
            ReportedAtUtc = now,
            SchemaVersion = CoachResponseReportLimits.SchemaVersion,

            GroundingStage = evidence.Grounding.Stage,
            GroundingRefused = evidence.Grounding.Refused,
            GroundingAltered = evidence.Grounding.Altered,
            GroundingRepairSuppressed = evidence.Grounding.RepairSuppressed,
            GroundingFindingCount = evidence.Grounding.FindingCount,
            GroundingRuleCodes = evidence.Grounding.RuleCodes,
            GroundingLimitationCode = evidence.Grounding.LimitationCode,
            GroundingShadowLabel = evidence.Grounding.ShadowLabel
        };

        try
        {
            _db.CoachResponseReports.Add(row);
            await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException)
        {
            // The unique index fired: another instance, or another tab, reported this response
            // between the pre-check and the insert. That is a success from the learner's point of
            // view — the response is reported — so the winner's row is read back and returned.
            _db.ChangeTracker.Clear();

            var winner = await FindExistingAsync(owner, responseId, cancellationToken).ConfigureAwait(false);
            if (winner is not null)
            {
                return Replay(winner);
            }

            throw;
        }

        // The product signal, raised after the learner's report is durable. Ordered this way on
        // purpose: the report is the learner's action and must survive even if the ledger is off,
        // unwritable, or mid-deployment.
        var opportunityId = await RaiseOpportunityAsync(owner, row, cancellationToken).ConfigureAwait(false);
        if (opportunityId is { Length: > 0 })
        {
            // Guarded for the same reason the call above it is, and it is not the same guard.
            // The report is already committed at this point, so an exception escaping here would
            // become a 500 and tell the learner their report was not filed — about a report that
            // was. A missing back-reference is already a normal state (the column is nullable and
            // documented as such); a false failure message is not.
            try
            {
                row.OpportunityId = opportunityId;
                await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                LogGatherFailure(ex, "opportunity-link");
                _db.ChangeTracker.Clear();
            }
        }

        // Content-free by construction: two enum names and a count. Nothing here could carry a
        // learner's words even if somebody wanted it to.
        _logger.LogInformation(
            "[Coach] A learner reported a response. Reason={Reason} ResponseKind={ResponseKind} " +
            "StopReason={StopReason}",
            row.Reason,
            row.ResponseKind,
            row.StopReason);

        return CoachOperationResult<CoachResponseReportResponse>.Ok(new CoachResponseReportResponse
        {
            MessageId = row.CoachMessageId,
            Reason = row.Reason,
            State = CoachResponseReportState.Recorded,
            ReportedAtUtc = row.ReportedAtUtc
        });
    }

    // ------------------------------------------------------------------ pairing

    /// <summary>
    /// Finds the learner request one coach response answered, or null when it cannot be
    /// established.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The correlation is the ledger's, not the caller's.</b> Both rows of one exchange carry
    /// the operation id of the turn that appended them, stamped by the server at append time. A
    /// client cannot forge it, and — after a history reload renumbers the transcript — a client
    /// cannot reliably reconstruct it either, which is why the pairing is derived here instead of
    /// being asserted in the request.
    /// </para>
    /// <para>
    /// <b>Fails closed rather than falling back to adjacency.</b> A response with no turn
    /// correlation — a session-only turn, or a row written before durable operations existed — is
    /// not pairable, and this returns null. "The learner message just above it" would be right
    /// most of the time and wrong exactly when a turn produced several messages or a reload
    /// interleaved them, and being wrong here means sending a reviewer to read an exchange the
    /// learner never complained about.
    /// </para>
    /// <para>
    /// The greatest qualifying sequence below the response is taken, so a turn that appended
    /// several coach messages still resolves to the one request that opened it.
    /// </para>
    /// </remarks>
    private async Task<MessageFacts?> FindPairedRequestAsync(
        CoachOwner owner,
        string conversationId,
        MessageFacts response,
        CancellationToken cancellationToken)
    {
        if (response.Role != CoachMessageRole.Coach
            || response.OperationId is not { Length: > 0 } operationId)
        {
            return null;
        }

        return await _db.CoachMessages
            .AsNoTracking()
            .Where(m => m.UserProfileId == owner.UserProfileId
                        && m.ConversationId == conversationId
                        && m.OperationId == operationId
                        && m.Role == CoachMessageRole.Learner
                        && m.Sequence < response.Sequence)
            .OrderByDescending(m => m.Sequence)
            .Select(m => new MessageFacts(m.Id, m.Sequence, m.Role, m.Kind, m.OperationId))
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    // ------------------------------------------------------------------ evidence

    /// <summary>
    /// The turn facts a reviewer needs, gathered as closed codes and identifiers only.
    /// </summary>
    /// <remarks>
    /// Every field is optional and every lookup degrades to null. A turn operation aged out by
    /// cleanup, a write ledger row purged with its conversation, or a model outcome this build
    /// cannot deserialize all produce a thinner report, never a failed one: the learner's report
    /// is the thing being recorded, and the metadata is the thing being attached to it.
    /// </remarks>
    private async Task<TurnEvidence> GatherAsync(
        CoachOwner owner,
        string conversationId,
        MessageFacts response,
        CancellationToken cancellationToken)
    {
        if (response.OperationId is not { Length: > 0 } operationId)
        {
            return default;
        }

        CoachTurnOperationStatus? turnStatus = null;
        CoachStopReason? stopReason = null;
        int? attempts = null;
        string? errorCode = null;
        CoachGroundingReportFacts grounding = default;

        try
        {
            var operation = await _operations.GetAsync(owner, operationId, cancellationToken).ConfigureAwait(false);
            if (operation is not null)
            {
                turnStatus = operation.Status;
                attempts = operation.AttemptCount;
                errorCode = Bounded(operation.ErrorCode, CoachResponseReportLimits.FailureCodeMaxLength);
            }

            // A second, owner-scoped read rather than a field on the record above: the operation
            // row deliberately does not carry its decrypted outcome, and the store's own outcome
            // reader is the one path that decrypts it. Reusing that path is what keeps this
            // method from becoming a second place that knows how coach content is protected.
            var outcome = await _operations.GetOutcomeAsync(owner, operationId, cancellationToken)
                .ConfigureAwait(false);
            stopReason = ReadStopReason(outcome?.Payload);

            // The same decrypted payload, read through the conversation layer's own outcome reader
            // rather than a second parser. That reader already knows every stored schema version
            // and already treats an unknown one as absent, which is exactly the null this column
            // wants — and duplicating it here would produce two answers to "can this row be read".
            //
            // Deliberately not the request-scoped observation buffer. The buffer belongs to the
            // turn in flight; a learner filing a report is a different request, and reading a
            // buffer here would attach whatever the *reporting* request happened to do.
            grounding = ProjectGrounding(
                Application.History.CoachConversationService
                    .ReadOutcome(outcome?.Payload, outcome?.SchemaVersion)?.Grounding);
        }
        catch (Exception ex)
        {
            // Shape only, and swallowed. See CoachExceptionSanitizer: passing the exception to the
            // logger would write its message and inner chain, which on a coach path carry prompt
            // and learner text.
            LogGatherFailure(ex, "turn-operation");
        }

        string? invokedToolNames = null;
        string? writeOperationId = null;
        CoachWriteOperationStatus? writeStatus = null;
        string? writeFailureCode = null;

        try
        {
            var writes = await _db.CoachWriteOperations
                .AsNoTracking()
                .Where(w => w.UserProfileId == owner.UserProfileId
                            && w.ConversationId == conversationId
                            && w.TurnId == operationId)
                .OrderBy(w => w.CreatedAtUtc)
                .Select(w => new { w.Id, w.Status })
                .Take(CoachResponseReportLimits.InvokedToolNamesMaxCount)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            if (writes.Count > 0)
            {
                writeOperationId = writes[0].Id;
                writeStatus = writes[0].Status;
            }

            var audits = await _db.CoachWriteAudits
                .AsNoTracking()
                .Where(a => a.UserProfileId == owner.UserProfileId
                            && a.ConversationId == conversationId
                            && a.TurnId == operationId)
                .OrderBy(a => a.CreatedAtUtc)
                .Select(a => new { a.ToolName, a.FailureCode })
                .Take(CoachResponseReportLimits.InvokedToolNamesMaxCount * 4)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            invokedToolNames = JoinRegisteredToolNames(audits.Select(a => a.ToolName));

            writeFailureCode = Bounded(
                audits.LastOrDefault(a => !string.IsNullOrWhiteSpace(a.FailureCode))?.FailureCode,
                CoachResponseReportLimits.FailureCodeMaxLength);
        }
        catch (Exception ex)
        {
            LogGatherFailure(ex, "write-ledger");
        }

        return new TurnEvidence(
            turnStatus,
            stopReason,
            attempts,
            errorCode,
            invokedToolNames,
            writeOperationId,
            writeStatus,
            writeFailureCode,
            grounding);
    }

    /// <summary>
    /// The grounding columns for one stored summary, or all-null when there is none.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Null is three different situations and they must stay indistinguishable.</b> The ladder
    /// was Off, the row predates these columns, or the outcome could not be read. None of the three
    /// is a finding, and writing a zero for any of them would put a measurement in the operator's
    /// hands that nobody made.
    /// </para>
    /// <para>
    /// Content-free by construction: eight ordinals, booleans and counts, plus one string that can
    /// only ever hold names drawn from a closed enum.
    /// </para>
    /// </remarks>
    internal static CoachGroundingReportFacts ProjectGrounding(
        Validation.Claims.CoachGroundingTurnSummary? summary)
    {
        if (summary is null)
        {
            return default;
        }

        return new CoachGroundingReportFacts(
            Stage: Enum.IsDefined(summary.RequestedStage) ? (int)summary.RequestedStage : null,
            Refused: summary.Refused,
            Altered: summary.Altered,
            RepairSuppressed: summary.RepairSuppressedForLanguage,
            FindingCount: summary.FindingCount >= 0 ? summary.FindingCount : null,
            RuleCodes: RenderRuleCodes(summary.RuleCounts),
            LimitationCode: summary.LimitationCode is { } limitation && Enum.IsDefined(limitation)
                ? (int)limitation
                : null,
            ShadowLabel: Enum.IsDefined(summary.ShadowLabel) ? (int)summary.ShadowLabel : null);
    }

    /// <summary>
    /// The distinct rule names that fired, ordinal-sorted and comma-joined, or null.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Names, not ordinals.</b> A report row outlives the build that wrote it, and an ordinal is
    /// only meaningful beside the enum it came from. A name is still readable after a member is
    /// inserted.
    /// </para>
    /// <para>
    /// <b>Whole names or nothing.</b> An unrecognised code is dropped entirely — never rendered as
    /// its number, never abbreviated — because a reader cannot tell a partial name from a real one.
    /// If the joined result would exceed the column bound the whole value is dropped for the same
    /// reason: a truncated list reads as a short list, and a short list is a false statement about
    /// what fired.
    /// </para>
    /// <para>
    /// Ordinal-sorted so two reports of the same shape produce the same string and a rollup can
    /// group on it without normalising first.
    /// </para>
    /// </remarks>
    internal static string? RenderRuleCodes(
        IReadOnlyList<Validation.Claims.CoachGroundingRuleCount>? counts)
    {
        if (counts is null || counts.Count == 0)
        {
            return null;
        }

        var names = new SortedSet<string>(StringComparer.Ordinal);

        foreach (var entry in counts)
        {
            if (entry is null || !Enum.IsDefined(entry.Rule))
            {
                // A code this build cannot name is dropped whole. It is not rendered as a number,
                // because the column's contract is that every token in it is a member name.
                continue;
            }

            names.Add(entry.Rule.ToString());
        }

        if (names.Count == 0)
        {
            return null;
        }

        var joined = string.Join(',', names);

        return joined.Length <= CoachResponseReportLimits.GroundingRuleCodesMaxLength ? joined : null;
    }

    /// <summary>
    /// Renders the registered tool names from a turn's audit rows as one bounded, sorted list.
    /// </summary>
    /// <remarks>
    /// <b>The registry is the filter, and it runs before anything is written.</b> A name the
    /// registry does not know is dropped rather than truncated or stored, because a column that
    /// can hold an unrecognized string is a free-text column wearing a list's clothes. Sorting is
    /// ordinal so two turns that ran the same tools produce the same value, and the whole result
    /// is dropped rather than cut short if it would exceed the column — half a tool name is not a
    /// tool name.
    /// </remarks>
    internal string? JoinRegisteredToolNames(IEnumerable<string?> candidates)
    {
        var names = new SortedSet<string>(StringComparer.Ordinal);

        foreach (var candidate in candidates)
        {
            if (string.IsNullOrWhiteSpace(candidate))
            {
                continue;
            }

            var trimmed = candidate.Trim();
            if (!_registry.IsRegistered(trimmed))
            {
                continue;
            }

            names.Add(trimmed);

            if (names.Count >= CoachResponseReportLimits.InvokedToolNamesMaxCount)
            {
                break;
            }
        }

        if (names.Count == 0)
        {
            return null;
        }

        var joined = string.Join(',', names);
        return joined.Length <= CoachResponseReportLimits.InvokedToolNamesMaxLength ? joined : null;
    }

    /// <summary>
    /// Reads only the stop reason out of a turn's replayable outcome.
    /// </summary>
    /// <remarks>
    /// The payload is the same one the poll path already deserializes, and exactly one field is
    /// taken from it. Nothing else is read into a local, returned, stored, or logged — the
    /// outcome carries the coach's own words, and this method's contract is that none of them
    /// leave it.
    /// </remarks>
    internal static CoachStopReason? ReadStopReason(string? storedOutcome)
    {
        if (string.IsNullOrWhiteSpace(storedOutcome))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(storedOutcome);

            if (!document.RootElement.TryGetProperty(nameof(CoachTurnResponse.StopReason), out var property)
                && !document.RootElement.TryGetProperty("stopReason", out property))
            {
                return null;
            }

            return property.ValueKind switch
            {
                JsonValueKind.Number when property.TryGetInt32(out var ordinal) && Enum.IsDefined((CoachStopReason)ordinal)
                    => (CoachStopReason)ordinal,

                // Membership is checked on this arm too, and not only on the numeric one.
                // Enum.TryParse answers true for numeric text ("999") and for comma-separated
                // flag lists ("1,2"), neither of which is a declared member — so parsing alone
                // would let an undefined value reach a column this method's contract says holds
                // one of a closed set.
                JsonValueKind.String when Enum.TryParse<CoachStopReason>(property.GetString(), ignoreCase: true, out var parsed)
                                          && Enum.IsDefined(parsed)
                    => parsed,

                _ => null
            };
        }
        catch (JsonException)
        {
            // An outcome this build cannot read is treated as absent, exactly as the replay path
            // treats it. A report is not worth failing over a field that is decoration on it.
            return null;
        }
    }

    // ------------------------------------------------------------------ ledger

    /// <summary>
    /// Raises the product signal for one report and returns the ledger row it landed on.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The recorder has no return value by design — it is an observer, and a caller able to
    /// branch on it would be a caller able to be failed by it. The row is therefore found
    /// afterwards by the identity the recorder itself would have written: owner, fingerprint, and
    /// UTC bucket. A miss returns null and the report keeps its null
    /// <see cref="CoachResponseReport.OpportunityId"/>, which is a normal state and not an error.
    /// </para>
    /// <para>
    /// <b>The whole method is inside a catch.</b> A ledger that could fail a learner's report
    /// would have turned a feedback control into a way to be told the feedback was not accepted,
    /// after it already was.
    /// </para>
    /// </remarks>
    private async Task<string?> RaiseOpportunityAsync(
        CoachOwner owner,
        CoachResponseReport row,
        CancellationToken cancellationToken)
    {
        try
        {
            var capability = CoachOpportunityCapabilityCodes.ForReportReason(row.Reason);

            var signal = new CoachOpportunitySignal(
                CoachOpportunityKind.UserReportedResponse,
                capability,
                CoachOpportunitySurface.TurnOutcome,
                CoachOpportunityDisposition.Product,
                OfferLink: CoachOpportunityOfferLink.None,
                ToolName: null,
                FailureCode: row.WriteFailureCode ?? row.TurnErrorCode,
                StopReason: row.StopReason,
                Evidence: new CoachOpportunityEvidencePointer(
                    ConversationId: row.ConversationId,
                    MessageId: row.RequestMessageId,
                    MessageSequence: row.RequestMessageSequence,
                    OfferMessageId: row.CoachMessageId,
                    OfferMessageSequence: row.CoachMessageSequence),
                TurnId: row.TurnOperationId,
                TurnOperationId: row.TurnOperationId,
                WriteOperationId: row.WriteOperationId);

            await _recorder.RecordAsync(signal, cancellationToken).ConfigureAwait(false);

            // The identity the recorder would have written, derived under the SAME normalization
            // rules it applies. Two of those rules can change a fingerprint input, and both are
            // reproduced here rather than assumed away: an unknown failure code is dropped, and
            // an undefined stop reason is nulled. Getting either wrong does not fail anything
            // loudly — it silently looks up a fingerprint nothing was written under, leaves
            // OpportunityId null, and quietly costs the operator the report's turn facts.
            //
            // ReadStopReason already refuses an undefined value, so this is the second of two
            // gates rather than the only one. Both stay: the recorder's own Normalize is not
            // reachable from here, so agreement between the two derivations is a property this
            // line has to maintain, and CoachResponseReportServiceTests pins it.
            var fingerprint = CoachOpportunityFingerprint.Compute(
                CoachOpportunityKind.UserReportedResponse,
                capability,
                toolName: null,
                failureCode: CoachOpportunityFailureCodes.IsKnown(signal.FailureCode) ? signal.FailureCode : null,
                stopReason: row.StopReason is { } stopReason && Enum.IsDefined(stopReason) ? stopReason : null,
                offerLink: CoachOpportunityOfferLink.None);

            var bucket = DateOnly.FromDateTime(row.ReportedAtUtc);

            return await _db.CoachOpportunities
                .AsNoTracking()
                .Where(o => o.UserProfileId == owner.UserProfileId
                            && o.Fingerprint == fingerprint
                            && o.DedupBucketDate == bucket)
                .Select(o => o.Id)
                .FirstOrDefaultAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LogGatherFailure(ex, "opportunity-ledger");
            return null;
        }
    }

    // ------------------------------------------------------------------ helpers

    private async Task<CoachResponseReport?> FindExistingAsync(
        CoachOwner owner,
        string coachMessageId,
        CancellationToken cancellationToken) =>
        await _db.CoachResponseReports
            .AsNoTracking()
            .FirstOrDefaultAsync(
                row => row.UserProfileId == owner.UserProfileId && row.CoachMessageId == coachMessageId,
                cancellationToken)
            .ConfigureAwait(false);

    private static CoachOperationResult<CoachResponseReportResponse> Replay(CoachResponseReport row) =>
        CoachOperationResult<CoachResponseReportResponse>.Ok(new CoachResponseReportResponse
        {
            MessageId = row.CoachMessageId,
            Reason = row.Reason,
            State = CoachResponseReportState.AlreadyReported,
            ReportedAtUtc = row.ReportedAtUtc
        });

    private bool TryResolveOwner(out CoachOwner owner)
    {
        owner = default;

        if (!_userScope.TryGetUserProfileId(out var userProfileId))
        {
            return false;
        }

        return CoachOwner.TryCreate(userProfileId, tenantId: null, out owner);
    }

    /// <summary>
    /// Whether a value is shaped like an identifier this server could have issued.
    /// </summary>
    /// <remarks>
    /// A bound rather than a format, deliberately: the identifiers are opaque, so asserting a
    /// shape would couple this route to how they happen to be generated today. The bound exists
    /// so an oversized value is refused before it reaches a parameterized query with a
    /// <c>varchar(64)</c> on the other side.
    /// </remarks>
    private static bool IsWellFormedId(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && value.Trim().Length <= CoachResponseReportLimits.IdMaxLength;

    private static string? Bounded(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        return trimmed.Length <= maxLength ? trimmed : trimmed[..maxLength];
    }

    private static CoachOperationResult<T> Unavailable<T>() =>
        CoachOperationResult<T>.Problem(
            CoachOperationStatus.Unavailable,
            CoachProblemTypes.Unavailable,
            "Reporting a response is not available.");

    private static CoachOperationResult<T> NotFound<T>() =>
        CoachOperationResult<T>.Problem(
            CoachOperationStatus.SessionNotFound,
            CoachProblemTypes.ConversationNotFound,
            "That conversation is not available.");

    private void LogGatherFailure(Exception ex, string stage)
    {
        var facts = CoachExceptionSanitizer.Describe(ex);
        _logger.LogWarning(
            "[Coach] Report evidence could not be gathered at {Stage}; the report is unaffected. " +
            "Category={FailureCategory} ProviderStatus={ProviderStatus} ProviderCode={ProviderErrorCode} " +
            "InnerDepth={InnerDepth}",
            stage,
            facts.Category,
            facts.ProviderStatus,
            facts.ProviderErrorCode,
            facts.InnerDepth);
    }

    /// <summary>The ledger facts one message contributes to a pairing decision.</summary>
    /// <remarks>
    /// A projection, not the entity: the encrypted payload column is not selected, so this path
    /// cannot decrypt a message even by accident.
    /// </remarks>
    internal sealed record MessageFacts(
        string Id,
        long Sequence,
        CoachMessageRole Role,
        CoachMessageKind Kind,
        string? OperationId);

    private readonly record struct TurnEvidence(
        CoachTurnOperationStatus? TurnStatus,
        CoachStopReason? StopReason,
        int? AttemptCount,
        string? TurnErrorCode,
        string? InvokedToolNames,
        string? WriteOperationId,
        CoachWriteOperationStatus? WriteStatus,
        string? WriteFailureCode,
        CoachGroundingReportFacts Grounding = default);

    /// <summary>
    /// The grounding columns for one report. Every member null means "no evidence", never "zero".
    /// </summary>
    internal readonly record struct CoachGroundingReportFacts(
        int? Stage,
        bool? Refused,
        bool? Altered,
        bool? RepairSuppressed,
        int? FindingCount,
        string? RuleCodes,
        int? LimitationCode,
        int? ShadowLabel);
}
