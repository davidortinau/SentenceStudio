using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SentenceStudio.Api.Coach.Operations;
using SentenceStudio.Api.Coach.Persistence;
using SentenceStudio.Api.Coach.Reports;
using SentenceStudio.Api.Coach.Telemetry;
using SentenceStudio.Api.Coach.Tools;
using SentenceStudio.Services.Plans;

namespace SentenceStudio.Api.Coach.Opportunities;

/// <summary>
/// Writes one content-free row per (learner, problem, UTC day), and refuses to do anything else.
/// </summary>
/// <remarks>
/// <para>
/// <b>Response neutrality is the first requirement, not the last.</b> Every call site invokes this
/// after the turn result has already been computed, the whole body is inside a
/// <c>try/catch</c>, and there is no return value a caller could branch on. A recorder that threw
/// — or that a caller awaited before deciding what to say — would have turned a telemetry
/// feature into a way to fail a learner's turn.
/// </para>
/// <para>
/// <b>Fail closed on identity.</b> No trusted owner means no row. The recorder never guesses,
/// never falls back to "first profile", and never writes an unowned row. That is the same rule
/// every repository on this codebase follows, and it is what keeps the ledger owner-scoped by
/// construction rather than by query discipline.
/// </para>
/// <para>
/// <b>Closed vocabularies only.</b> <c>CapabilityCode</c>, <c>ToolName</c>, and
/// <c>FailureCode</c> are validated against their closed sets before the write. A signal
/// carrying anything else is dropped with a content-free warning, because an unvalidated code
/// column is a free-text column wearing a different name — and free text is where a learner's
/// phrase would eventually appear.
/// </para>
/// <para>
/// <b>Its own scope, deliberately.</b> The write runs on a private <c>CoachDbContext</c> from a
/// fresh service scope rather than on whatever context the calling service happens to hold. That
/// keeps it off any ambient transaction (so a rolled-back write proposal does not erase the
/// evidence that it was refused), keeps it off the caller's change tracker (which the write
/// ledger clears on its own error paths), and makes concurrent tool-boundary calls safe on a
/// type that is not thread-safe.
/// </para>
/// </remarks>
public sealed class CoachOpportunityRecorder : ICoachOpportunityRecorder
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IUserScopeProvider _userScope;
    private readonly ICoachToolRegistry _registry;
    private readonly IOptionsMonitor<CoachOpportunityOptions> _options;
    private readonly IOptionsMonitor<CoachResponseReportOptions> _reportOptions;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<CoachOpportunityRecorder> _logger;

    public CoachOpportunityRecorder(
        IServiceScopeFactory scopeFactory,
        IUserScopeProvider userScope,
        ICoachToolRegistry registry,
        IOptionsMonitor<CoachOpportunityOptions> options,
        IOptionsMonitor<CoachResponseReportOptions> reportOptions,
        TimeProvider timeProvider,
        ILogger<CoachOpportunityRecorder> logger)
    {
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _userScope = userScope ?? throw new ArgumentNullException(nameof(userScope));
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _reportOptions = reportOptions ?? throw new ArgumentNullException(nameof(reportOptions));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Whether this signal is admitted by the current configuration.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two switches, and the second one is the whole point.
    /// <c>Coach:Opportunities:Enabled</c> governs <b>automatic</b> capture — the three observer
    /// boundaries that watch the server refuse itself. <c>Coach:Reports:Enabled</c> governs the
    /// one signal a human produces on purpose.
    /// </para>
    /// <para>
    /// A deployment that turns automatic capture off has said "stop inferring problems from my
    /// turns". It has not said "discard the reports my learners deliberately filed" — and it must
    /// not, because the learner was told the report goes somewhere a person looks. Suppressing it
    /// here would make that message untrue while every test still passed.
    /// </para>
    /// <para>
    /// The bypass is safe precisely because it is keyed on
    /// <see cref="CoachOpportunityKind.UserReportedResponse"/>: no mapper, detector, or tool
    /// observer produces that kind, and <c>CoachOpportunityNoFeedbackLoopTests</c> holds that
    /// line. It cannot become a way for a heuristic to write while capture is off.
    /// </para>
    /// </remarks>
    internal bool IsAdmitted(CoachOpportunityKind kind) =>
        kind == CoachOpportunityKind.UserReportedResponse
            ? _reportOptions.CurrentValue.Enabled
            : _options.CurrentValue.Enabled;

    /// <inheritdoc />
    public async ValueTask RecordAsync(
        CoachOpportunitySignal signal,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (!IsAdmitted(signal.Kind))
            {
                return;
            }

            var normalized = Normalize(signal);
            if (normalized is not { } write)
            {
                return;
            }

            // Resolved from the caller's scope, never from the private one below: identity is an
            // ambient request fact, and re-resolving it inside a fresh scope would be one more
            // way to get an owner that is not the one whose turn this was.
            if (!_userScope.TryGetUserProfileId(out var userProfileId)
                || string.IsNullOrWhiteSpace(userProfileId))
            {
                _logger.LogDebug(
                    "[Coach] An opportunity signal was dropped: no trusted owner. Capability={CapabilityCode}",
                    write.CapabilityCode);
                return;
            }

            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<CoachDbContext>();

            await WriteAsync(db, userProfileId.Trim(), write, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Shape only, and never rethrown. A ledger that could fail a turn would be worse than
            // no ledger: the whole value of this table is that it observes without participating.
            // CoachExceptionSanitizer is the only path from an exception to a log line on this
            // codebase, because Exception.ToString concatenates the message, the inner chain, and
            // Data — which on a coach path carry prompt and learner text.
            var facts = CoachExceptionSanitizer.Describe(ex);
            _logger.LogWarning(
                "[Coach] An opportunity signal could not be recorded; the turn is unaffected. " +
                "Category={FailureCategory} ProviderStatus={ProviderStatus} " +
                "ProviderCode={ProviderErrorCode} InnerDepth={InnerDepth}",
                facts.Category,
                facts.ProviderStatus,
                facts.ProviderErrorCode,
                facts.InnerDepth);
        }
    }

    /// <summary>
    /// Validates the signal against every closed vocabulary and strips what an aggregate-only row
    /// must not carry. Returns null when the signal cannot be recorded at all.
    /// </summary>
    /// <remarks>
    /// <b>The pointer strip is unconditional.</b> It runs on every aggregate-only signal rather
    /// than trusting the mapper that produced it, so a mapper that forgot — or a future mapper
    /// written by somebody who had not read this file — still cannot produce an inspectable row
    /// for a refusal. That is the mechanism behind "a safe refusal becomes a number, never a
    /// dossier".
    /// </remarks>
    internal CoachOpportunitySignal? Normalize(CoachOpportunitySignal signal)
    {
        if (!Enum.IsDefined(signal.Kind)
            || !Enum.IsDefined(signal.Disposition)
            || !Enum.IsDefined(signal.Surface)
            || !Enum.IsDefined(signal.OfferLink))
        {
            _logger.LogWarning(
                "[Coach] An opportunity signal was dropped: an enum member is not defined. Kind={Kind}",
                (int)signal.Kind);
            return null;
        }

        if (!CoachOpportunityCapabilityCodes.IsKnown(signal.CapabilityCode))
        {
            _logger.LogWarning(
                "[Coach] An opportunity signal was dropped: the capability code is not in the " +
                "closed set. Kind={Kind}",
                (int)signal.Kind);
            return null;
        }

        var normalized = signal;

        // The registry rather than CoachToolNames.All: that property is an alias for the core
        // five, so validating against it would silently reject every Sam read and write tool and
        // leave the ledger unable to name the surface that refused.
        if (!string.IsNullOrWhiteSpace(normalized.ToolName) && !_registry.IsRegistered(normalized.ToolName))
        {
            _logger.LogWarning(
                "[Coach] An opportunity signal named an unregistered tool; the name was dropped. " +
                "Kind={Kind} Capability={CapabilityCode}",
                (int)normalized.Kind,
                normalized.CapabilityCode);
            normalized = normalized with { ToolName = null };
        }

        if (!string.IsNullOrWhiteSpace(normalized.FailureCode)
            && !CoachOpportunityFailureCodes.IsKnown(normalized.FailureCode))
        {
            _logger.LogWarning(
                "[Coach] An opportunity signal carried a failure code outside the closed set; it " +
                "was dropped. Kind={Kind} Capability={CapabilityCode}",
                (int)normalized.Kind,
                normalized.CapabilityCode);
            normalized = normalized with { FailureCode = null };
        }

        if (normalized.StopReason is { } stopReason && !Enum.IsDefined(stopReason))
        {
            normalized = normalized with { StopReason = null };
        }

        if (normalized.Disposition == CoachOpportunityDisposition.AggregateOnly)
        {
            normalized = normalized.WithoutPointers();
        }

        // A learner's report is always individually reviewable, and that is enforced here rather
        // than trusted to the call site. Downgrading one to a counter would strip the two message
        // pointers on the way past — and those pointers are the only route back to the exchange
        // the learner was complaining about. A report with its evidence stripped is a number
        // telling a reviewer that somebody, somewhere, was unhappy about something.
        if (normalized.Kind == CoachOpportunityKind.UserReportedResponse
            && normalized.Disposition != CoachOpportunityDisposition.Product)
        {
            _logger.LogWarning(
                "[Coach] A learner report signal arrived as aggregate-only; it was restored to " +
                "Product. Capability={CapabilityCode}",
                normalized.CapabilityCode);
            normalized = normalized with { Disposition = CoachOpportunityDisposition.Product };
        }

        return normalized;
    }

    private async Task WriteAsync(
        CoachDbContext db,
        string userProfileId,
        CoachOpportunitySignal signal,
        CancellationToken cancellationToken)
    {
        var now = _timeProvider.GetUtcNow().UtcDateTime;
        var bucket = DateOnly.FromDateTime(now);
        var fingerprint = CoachOpportunityFingerprint.Compute(signal);

        var relatedId = signal.RelatedOpportunityId;
        if (relatedId is null && signal.Disposition == CoachOpportunityDisposition.Product)
        {
            relatedId = await FindRelatedAsync(db, userProfileId, signal, now, cancellationToken)
                .ConfigureAwait(false);
        }

        var isNpgsql = db.Database.IsNpgsql();

        // Both statements are compile-time constants. Nothing derived from a model completion, a
        // learner message, or any other untrusted input is ever concatenated into SQL here — every
        // value travels as a parameter, and every parameter's value is an identifier the server
        // issued, a closed-vocabulary constant, an enum ordinal, or a timestamp.
        //
        // The absent-value sentinels ('' for text, -1 for numbers, unwrapped by NULLIF) exist so
        // no parameter is ever typed only by a null, which is the one case where the two providers
        // infer differently.
        var sql = isNpgsql ? PostgresUpsert : SqliteUpsert;

        var parameters = new object[]
        {
            /* p0  */ Guid.NewGuid().ToString("n"),
            /* p1  */ Truncate(userProfileId, CoachOpportunityLimits.UserProfileIdMaxLength) ?? string.Empty,
            /* p2  */ Text(signal.Evidence.ConversationId, CoachOpportunityLimits.IdMaxLength),
            /* p3  */ Text(signal.TurnId, CoachOpportunityLimits.IdMaxLength),
            /* p4  */ Text(signal.TurnOperationId, CoachOpportunityLimits.IdMaxLength),
            /* p5  */ (int)signal.Kind,
            /* p6  */ (int)signal.Disposition,
            /* p7  */ (int)signal.Surface,
            /* p8  */ signal.CapabilityCode,
            /* p9  */ Text(signal.ToolName, CoachOpportunityLimits.ToolNameMaxLength),
            /* p10 */ RiskClassOrAbsent(signal.ToolName),
            /* p11 */ Text(signal.FailureCode, CoachOpportunityLimits.FailureCodeMaxLength),
            /* p12 */ signal.StopReason.HasValue ? (int)signal.StopReason.Value : AbsentNumber,
            /* p13 */ (int)signal.OfferLink,
            /* p14 */ Text(signal.Evidence.MessageId, CoachOpportunityLimits.IdMaxLength),
            /* p15 */ signal.Evidence.MessageSequence ?? AbsentSequence,
            /* p16 */ Text(signal.Evidence.OfferMessageId, CoachOpportunityLimits.IdMaxLength),
            /* p17 */ signal.Evidence.OfferMessageSequence ?? AbsentSequence,
            /* p18 */ Text(signal.WriteOperationId, CoachOpportunityLimits.IdMaxLength),
            /* p19 */ Text(relatedId, CoachOpportunityLimits.IdMaxLength),
            /* p20 */ fingerprint,
            /* p21 */ bucket.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            /* p22 */ FormatTimestamp(now, isNpgsql),
            /* p23 */ CoachOpportunityLimits.SchemaVersion
        };

        await db.Database.ExecuteSqlRawAsync(sql, parameters, cancellationToken).ConfigureAwait(false);

        // Content-free by construction: a kind ordinal, a closed-vocabulary code, a digest of
        // closed-vocabulary inputs, and a disposition. There is nothing here that could carry a
        // learner's words even if somebody wanted it to.
        _logger.LogInformation(
            "[Coach] Opportunity recorded. Kind={Kind} Capability={CapabilityCode} " +
            "Disposition={Disposition} Fingerprint={Fingerprint}",
            signal.Kind,
            signal.CapabilityCode,
            signal.Disposition,
            fingerprint);
    }

    /// <summary>
    /// Finds the most recent capability refusal in the same conversation that this row continues.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is what turns two rows into one story: "the model offered a setting change it is not
    /// allowed to make" followed minutes later by "the learner said yes and nothing bound to it"
    /// is one product problem, and the chain says so without either row carrying a word of what
    /// was said.
    /// </para>
    /// <para>
    /// Owner-scoped and conversation-scoped, so a chain can never reach across learners. Returns
    /// null rather than throwing when nothing matches — a missing chain is not a failure.
    /// </para>
    /// </remarks>
    private static async Task<string?> FindRelatedAsync(
        CoachDbContext db,
        string userProfileId,
        CoachOpportunitySignal signal,
        DateTime now,
        CancellationToken cancellationToken)
    {
        if (signal.Kind != CoachOpportunityKind.AmbiguousFollowUp)
        {
            return null;
        }

        var conversationId = signal.Evidence.ConversationId;
        if (string.IsNullOrWhiteSpace(conversationId))
        {
            return null;
        }

        var since = now - CoachOpportunityLimits.RelatedOpportunityWindow;

        return await db.CoachOpportunities
            .AsNoTracking()
            .Where(row => row.UserProfileId == userProfileId
                          && row.ConversationId == conversationId
                          && row.LastObservedAtUtc >= since
                          && (row.Kind == CoachOpportunityKind.UnsupportedCapability
                              || row.Kind == CoachOpportunityKind.ProposalRefusedByPolicy))
            .OrderByDescending(row => row.LastObservedAtUtc)
            .Select(row => row.Id)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    private int RiskClassOrAbsent(string? toolName)
    {
        if (string.IsNullOrWhiteSpace(toolName))
        {
            return AbsentNumber;
        }

        var registration = _registry.Find(toolName);
        return registration is null ? AbsentNumber : (int)registration.RiskClass;
    }

    private const int AbsentNumber = -1;
    private const long AbsentSequence = -1L;

    private static string Text(string? value, int maxLength) =>
        Truncate(value, maxLength) ?? string.Empty;

    private static string? Truncate(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        return trimmed.Length <= maxLength ? trimmed : trimmed[..maxLength];
    }

    /// <summary>
    /// Renders a timestamp in the exact textual form the target provider stores.
    /// </summary>
    /// <remarks>
    /// Passed as text and cast in SQL rather than as a <see cref="DateTime"/> parameter, because
    /// the API host turns on <c>Npgsql.EnableLegacyTimestampBehavior</c> process-wide and that
    /// switch changes which PostgreSQL type an inferred <see cref="DateTime"/> parameter maps to.
    /// An explicit ISO-8601 string with a <c>Z</c> suffix cast to <c>timestamptz</c> has one
    /// meaning under every combination of that switch, the session time zone, and the host's
    /// local zone.
    /// </remarks>
    private static string FormatTimestamp(DateTime utc, bool isNpgsql) =>
        isNpgsql
            ? utc.ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'", CultureInfo.InvariantCulture)
            : utc.ToString("yyyy-MM-dd HH:mm:ss.fffffff", CultureInfo.InvariantCulture);

    private const string ColumnList = """
        ("Id", "UserProfileId", "TenantId", "ConversationId", "TurnId", "TurnOperationId",
         "Kind", "Disposition", "Surface", "CapabilityCode", "ToolName", "RiskClass",
         "FailureCode", "StopReason", "OfferLink",
         "EvidenceMessageId", "EvidenceMessageSequence",
         "EvidenceOfferMessageId", "EvidenceOfferMessageSequence",
         "WriteOperationId", "RelatedOpportunityId",
         "Fingerprint", "DedupBucketDate", "OccurrenceCount",
         "FirstObservedAtUtc", "LastObservedAtUtc",
         "Status", "ReviewedAtUtc", "ReviewerNoteCode", "LinkedSpecPath",
         "EvidenceRevealCount", "EvidenceLastRevealedAtUtc", "SchemaVersion", "Version")
        """;

    private const string PostgresUpsert = $"""
        INSERT INTO "CoachOpportunity" {ColumnList}
        VALUES (@p0, @p1, NULL, NULLIF(@p2, ''), NULLIF(@p3, ''), NULLIF(@p4, ''),
                @p5, @p6, @p7, @p8, NULLIF(@p9, ''), NULLIF(@p10, -1),
                NULLIF(@p11, ''), NULLIF(@p12, -1), @p13,
                NULLIF(@p14, ''), NULLIF(@p15, -1),
                NULLIF(@p16, ''), NULLIF(@p17, -1),
                NULLIF(@p18, ''), NULLIF(@p19, ''),
                @p20, CAST(@p21 AS date), 1,
                CAST(@p22 AS timestamptz), CAST(@p22 AS timestamptz),
                0, NULL, NULL, NULL,
                0, NULL, @p23, 0)
        ON CONFLICT ("UserProfileId", "Fingerprint", "DedupBucketDate")
        DO UPDATE SET "OccurrenceCount" = "CoachOpportunity"."OccurrenceCount" + 1,
                      "LastObservedAtUtc" = CAST(@p22 AS timestamptz)
        """;

    private const string SqliteUpsert = $"""
        INSERT INTO "CoachOpportunity" {ColumnList}
        VALUES (@p0, @p1, NULL, NULLIF(@p2, ''), NULLIF(@p3, ''), NULLIF(@p4, ''),
                @p5, @p6, @p7, @p8, NULLIF(@p9, ''), NULLIF(@p10, -1),
                NULLIF(@p11, ''), NULLIF(@p12, -1), @p13,
                NULLIF(@p14, ''), NULLIF(@p15, -1),
                NULLIF(@p16, ''), NULLIF(@p17, -1),
                NULLIF(@p18, ''), NULLIF(@p19, ''),
                @p20, @p21, 1,
                @p22, @p22,
                0, NULL, NULL, NULL,
                0, NULL, @p23, 0)
        ON CONFLICT ("UserProfileId", "Fingerprint", "DedupBucketDate")
        DO UPDATE SET "OccurrenceCount" = "CoachOpportunity"."OccurrenceCount" + 1,
                      "LastObservedAtUtc" = @p22
        """;
}
