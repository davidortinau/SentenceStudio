using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SentenceStudio.WebApp.Operator;

/// <summary>One aggregated problem, exactly as the operator API reports it.</summary>
/// <remarks>
/// <c>DistinctLearners</c> is a count and never a list — the API has no shape that could return
/// learner identifiers on this path, and this client has no member that could receive them.
/// </remarks>
public sealed record SamOpportunityRollup(
    string Fingerprint,
    string Kind,
    string Disposition,
    string CapabilityCode,
    string? ToolName,
    string? FailureCode,
    string OfferLink,
    int TotalOccurrences,
    int DistinctLearners,
    int RowCount,
    DateTime FirstObservedAtUtc,
    DateTime LastObservedAtUtc,
    IReadOnlyList<string> Statuses);

/// <summary>One content-free ledger row.</summary>
public sealed record SamOpportunityRow(
    string Id,
    string Kind,
    string Disposition,
    string Surface,
    string CapabilityCode,
    string? ToolName,
    string? RiskClass,
    string? FailureCode,
    string? StopReason,
    string OfferLink,
    string Fingerprint,
    DateOnly DedupBucketDate,
    int OccurrenceCount,
    DateTime FirstObservedAtUtc,
    DateTime LastObservedAtUtc,
    string Status,
    DateTime? ReviewedAtUtc,
    string? ReviewerNoteCode,
    string? LinkedSpecPath,
    bool HasEvidence,
    int EvidenceRevealCount,
    DateTime? EvidenceLastRevealedAtUtc,
    int SchemaVersion,
    SamOpportunityReportFacts? Report = null);

/// <summary>
/// The turn facts behind one learner report.
/// </summary>
/// <remarks>
/// Closed codes only — enum names, counts, and registered tool names. Present on the detail
/// response for a <c>UserReportedResponse</c> row and absent on every other kind, so a card that
/// renders it is always rendering a report.
/// </remarks>
public sealed record SamOpportunityReportFacts(
    string Reason,
    string ResponseKind,
    string? TurnStatus,
    int? TurnAttemptCount,
    string? TurnErrorCode,
    string? InvokedToolNames,
    string? WriteStatus,
    string? WriteFailureCode,
    DateTime ReportedAtUtc,

    // Grounding evidence. Additive and defaulted, so a client reading a server that has not
    // shipped W9 deserializes the same record it always did, with every grounding member null —
    // which renders as "not measured" rather than as a finding of none.
    string? GroundingStage = null,
    bool? GroundingRefused = null,
    bool? GroundingAltered = null,
    bool? GroundingRepairSuppressed = null,
    int? GroundingFindingCount = null,
    string? GroundingRuleCodes = null,
    string? GroundingLimitationCode = null);

/// <summary>One page of ledger rows.</summary>
public sealed record SamOpportunityPage(
    IReadOnlyList<SamOpportunityRow> Items,
    int Total,
    int Skip,
    int Take);

/// <summary>A reviewer's decision. Closed vocabulary plus an optional validated path.</summary>
public sealed record SamOpportunityReviewRequest(
    string Status,
    string? ReviewerNoteCode,
    string? LinkedSpecPath);

/// <summary>The reviewed row plus the paste-ready markdown block.</summary>
public sealed record SamOpportunityReviewResponse(
    SamOpportunityRow Row,
    string MarkdownBlock);

/// <summary>The body that authorises an evidence reveal.</summary>
public sealed record SamOpportunityEvidenceRequest(string Acknowledgement);

/// <summary>The decrypted evidence for one Product row.</summary>
public sealed record SamOpportunityEvidenceResponse(
    string OpportunityId,
    string EvidenceState,
    string? LearnerMessageText,
    string? PriorCoachMessageText,
    bool CrossOwner,
    int EvidenceRevealCount);

/// <summary>Why an operator call did not return data.</summary>
public enum SamOpportunityClientStatus
{
    /// <summary>The call succeeded.</summary>
    Success = 0,

    /// <summary>
    /// The surface is off, this caller is outside the cohort, or the row does not exist. All
    /// three are indistinguishable by design.
    /// </summary>
    NotAvailable = 1,

    /// <summary>The request was malformed, or the acknowledgement literal was wrong.</summary>
    InvalidRequest = 2,

    /// <summary>
    /// The row belongs to another learner and cross-owner evidence is disabled.
    /// </summary>
    /// <remarks>
    /// <b>Not reachable from an HTTP status any more, and deliberately so.</b> The server answers
    /// a cross-owner refusal with the same 404 it answers "no such row" with, because a distinct
    /// status would confirm that the identifier names a real row owned by somebody else. The
    /// member is kept because the page renders it, and because removing it would silently change
    /// a public enum's ordinals.
    /// </remarks>
    CrossOwnerRefused = 3,

    /// <summary>This host's Data Protection key ring is ephemeral, so nothing can be decrypted.</summary>
    EphemeralKeyRing = 4,

    /// <summary>The call failed for a reason the surface does not name.</summary>
    Failed = 5,

    /// <summary>
    /// The review asked for a status transition the server's lifecycle refuses — an accepted row
    /// cannot be walked back to a status the retention sweep would delete.
    /// </summary>
    TransitionRefused = 6
}

/// <summary>An operator call's outcome.</summary>
public readonly record struct SamOpportunityResult<T>(SamOpportunityClientStatus Status, T? Value)
{
    /// <summary>True when the call returned data.</summary>
    public bool IsOk => Status == SamOpportunityClientStatus.Success && Value is not null;
}

/// <summary>
/// Typed client for the Development-only Sam opportunity operator API.
/// </summary>
/// <remarks>
/// <para>
/// Registered only in Development, and the routes it calls exist only in Development, so this
/// class is a dead letter in any other environment by two independent mechanisms rather than one.
/// </para>
/// <para>
/// It never throws for an expected refusal. A 404 means "no entry point" — the flag is off, the
/// caller is outside the cohort, or the row is gone — and those are normal traffic on a surface
/// whose whole design is to be indistinguishable from absent.
/// </para>
/// </remarks>
public sealed class SamOpportunityOperatorClient
{
    private const string BasePath = "/api/v1/coach/operator/opportunities";

    /// <summary>
    /// The literal the API requires before it will decrypt anything. Mirrored here so the reveal
    /// is one deliberate call rather than something a page can do while rendering — a request
    /// without it is refused server-side.
    /// </summary>
    public const string EvidenceAcknowledgement = "reveal-learner-content";

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly HttpClient _httpClient;
    private readonly ILogger<SamOpportunityOperatorClient> _logger;

    public SamOpportunityOperatorClient(
        HttpClient httpClient,
        ILogger<SamOpportunityOperatorClient> logger)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>Reads the safe cross-learner rollup.</summary>
    public Task<SamOpportunityResult<IReadOnlyList<SamOpportunityRollup>>> GetRollupAsync(
        DateTime? since = null,
        CancellationToken cancellationToken = default) =>
        GetAsync<IReadOnlyList<SamOpportunityRollup>>(
            since is { } value
                ? $"{BasePath}/rollup?since={Uri.EscapeDataString(value.ToString("O"))}"
                : $"{BasePath}/rollup",
            cancellationToken);

    /// <summary>Reads one page of individually reviewable rows.</summary>
    public Task<SamOpportunityResult<SamOpportunityPage>> ListAsync(
        string? status = null,
        string? capabilityCode = null,
        int skip = 0,
        int take = 50,
        CancellationToken cancellationToken = default)
    {
        var query = new List<string>
        {
            $"skip={skip}",
            $"take={take}"
        };

        if (!string.IsNullOrWhiteSpace(status))
        {
            query.Add($"status={Uri.EscapeDataString(status)}");
        }

        if (!string.IsNullOrWhiteSpace(capabilityCode))
        {
            query.Add($"capabilityCode={Uri.EscapeDataString(capabilityCode)}");
        }

        return GetAsync<SamOpportunityPage>(
            $"{BasePath}?{string.Join('&', query)}", cancellationToken);
    }

    /// <summary>Reads one content-free row.</summary>
    public Task<SamOpportunityResult<SamOpportunityRow>> GetAsync(
        string id,
        CancellationToken cancellationToken = default) =>
        GetAsync<SamOpportunityRow>(
            $"{BasePath}/{Uri.EscapeDataString(id)}", cancellationToken);

    /// <summary>Records a review decision and returns the paste-ready markdown block.</summary>
    public Task<SamOpportunityResult<SamOpportunityReviewResponse>> ReviewAsync(
        string id,
        SamOpportunityReviewRequest request,
        CancellationToken cancellationToken = default) =>
        PostAsync<SamOpportunityReviewRequest, SamOpportunityReviewResponse>(
            $"{BasePath}/{Uri.EscapeDataString(id)}/review",
            request,
            // The review route's only 409 is a refused lifecycle transition; the key-ring refusal
            // is reachable only from the evidence route. Mapping the status per route keeps the
            // two apart without the client having to parse a ProblemDetails title.
            SamOpportunityClientStatus.TransitionRefused,
            cancellationToken);

    /// <summary>
    /// Reveals the encrypted evidence behind one row. Explicit, acknowledged, and audited.
    /// </summary>
    public Task<SamOpportunityResult<SamOpportunityEvidenceResponse>> RevealEvidenceAsync(
        string id,
        CancellationToken cancellationToken = default) =>
        PostAsync<SamOpportunityEvidenceRequest, SamOpportunityEvidenceResponse>(
            $"{BasePath}/{Uri.EscapeDataString(id)}/evidence",
            new SamOpportunityEvidenceRequest(EvidenceAcknowledgement),
            SamOpportunityClientStatus.EphemeralKeyRing,
            cancellationToken);

    private async Task<SamOpportunityResult<T>> GetAsync<T>(
        string path,
        CancellationToken cancellationToken)
    {
        try
        {
            using var response = await _httpClient.GetAsync(path, cancellationToken).ConfigureAwait(false);
            return await ReadAsync<T>(response, SamOpportunityClientStatus.Failed, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Shape only. This client talks to a surface whose responses can carry learner text,
            // so an exception's message and Data are not safe to log verbatim.
            _logger.LogWarning(
                "[SamOpportunities] An operator read failed. ExceptionType={ExceptionType}",
                ex.GetType().Name);
            return new SamOpportunityResult<T>(SamOpportunityClientStatus.Failed, default);
        }
    }

    private async Task<SamOpportunityResult<TResponse>> PostAsync<TRequest, TResponse>(
        string path,
        TRequest body,
        SamOpportunityClientStatus conflictStatus,
        CancellationToken cancellationToken)
    {
        try
        {
            using var response = await _httpClient
                .PostAsJsonAsync(path, body, SerializerOptions, cancellationToken)
                .ConfigureAwait(false);

            return await ReadAsync<TResponse>(response, conflictStatus, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                "[SamOpportunities] An operator write failed. ExceptionType={ExceptionType}",
                ex.GetType().Name);
            return new SamOpportunityResult<TResponse>(SamOpportunityClientStatus.Failed, default);
        }
    }

    private static async Task<SamOpportunityResult<T>> ReadAsync<T>(
        HttpResponseMessage response,
        SamOpportunityClientStatus conflictStatus,
        CancellationToken cancellationToken)
    {
        var status = response.StatusCode switch
        {
            HttpStatusCode.OK => SamOpportunityClientStatus.Success,

            // 404 and 401 both mean "no entry point", and the page renders them identically.
            // Telling them apart on screen would leak which of the four gates refused.
            //
            // A cross-owner refusal now arrives here too: the server answers it with the same 404
            // it answers "no such row" with, so this client cannot tell them apart and must not
            // try. That is the point — a distinguishable response would confirm the row exists.
            HttpStatusCode.NotFound => SamOpportunityClientStatus.NotAvailable,
            HttpStatusCode.Unauthorized => SamOpportunityClientStatus.NotAvailable,

            HttpStatusCode.BadRequest => SamOpportunityClientStatus.InvalidRequest,

            // Route-specific: the review route's 409 is a refused transition, the evidence
            // route's is an ephemeral key ring, and no route returns both.
            HttpStatusCode.Conflict => conflictStatus,

            _ => SamOpportunityClientStatus.Failed
        };

        if (status != SamOpportunityClientStatus.Success)
        {
            return new SamOpportunityResult<T>(status, default);
        }

        var value = await response.Content
            .ReadFromJsonAsync<T>(SerializerOptions, cancellationToken)
            .ConfigureAwait(false);

        return new SamOpportunityResult<T>(
            value is null ? SamOpportunityClientStatus.Failed : SamOpportunityClientStatus.Success,
            value);
    }
}
