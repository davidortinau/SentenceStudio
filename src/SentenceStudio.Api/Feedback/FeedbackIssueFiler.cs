using System.Net;
using System.Text;
using System.Text.Json;
using SentenceStudio.Api.Feedback.Persistence;

namespace SentenceStudio.Api.Feedback;

/// <summary>What happened when a signed feedback payload was filed.</summary>
public enum FeedbackFileOutcomeKind
{
    /// <summary>A new issue was created.</summary>
    Created,

    /// <summary>This payload was already filed; the existing issue is returned.</summary>
    Replayed,

    /// <summary>Refused by a per-owner limit. Nothing was sent.</summary>
    RateLimited,

    /// <summary>The payload was already used and closed without an issue. Nothing was sent.</summary>
    Closed,

    /// <summary>An earlier attempt may have created an issue; it will not be re-sent.</summary>
    InDoubt,

    /// <summary>No GitHub token is configured. Nothing was sent.</summary>
    Unconfigured,

    /// <summary>GitHub refused the request. No issue exists.</summary>
    GitHubRefused,

    /// <summary>GitHub could not be reached before anything was sent. No issue exists; try again later.</summary>
    GitHubUnavailable,

    /// <summary>GitHub could not be reached; an issue may or may not exist.</summary>
    GitHubUnreachable,

    /// <summary>The issue was created but its link could not be recorded.</summary>
    SettlementFailed
}

/// <summary>The result of <see cref="IFeedbackIssueFiler.FileAsync"/>. Carries no learner text.</summary>
public sealed record FeedbackFileOutcome(
    FeedbackFileOutcomeKind Kind,
    int IssueNumber = 0,
    string IssueUrl = "",
    string IssueTitle = "",
    FeedbackRateDecision? Rate = null,
    HttpStatusCode? GitHubStatus = null)
{
    public bool Filed => Kind is FeedbackFileOutcomeKind.Created or FeedbackFileOutcomeKind.Replayed;
}

/// <summary>
/// Redeems one signed feedback payload for exactly one public GitHub issue.
/// </summary>
/// <remarks>
/// Shared by the Feedback page's submit endpoint and Sam's feedback write handler, so there is one
/// place that calls GitHub. The caller has already verified the payload and its owner.
/// <list type="number">
/// <item><b>Look the payload up before touching the limiter.</b> A replay creates no issue and costs nothing.</item>
/// <item><b>Peek the limit, then claim, then consume.</b> Only the claim winner pays.</item>
/// <item><b>Call GitHub only while holding the claim.</b></item>
/// <item><b>Settle by what each failure proves.</b> A refusal proves no issue exists and closes the
/// row; a transport failure proves nothing and leaves it in doubt; no path reopens a row for retry.</item>
/// </list>
/// </remarks>
public interface IFeedbackIssueFiler
{
    Task<FeedbackFileOutcome> FileAsync(
        FeedbackPreviewPayload payload, string userProfileId, CancellationToken cancellationToken);
}

/// <inheritdoc />
public sealed class FeedbackIssueFiler : IFeedbackIssueFiler
{
    public const string GitHubRepo = "davidortinau/SentenceStudio";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly IGitHubTokenProvider _tokens;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IFeedbackRateLimiter _rateLimiter;
    private readonly IFeedbackSubmissionLedger _ledger;
    private readonly ILogger<FeedbackIssueFiler> _logger;

    public FeedbackIssueFiler(
        IGitHubTokenProvider tokens,
        IHttpClientFactory httpClientFactory,
        IFeedbackRateLimiter rateLimiter,
        IFeedbackSubmissionLedger ledger,
        ILogger<FeedbackIssueFiler> logger)
    {
        _tokens = tokens;
        _httpClientFactory = httpClientFactory;
        _rateLimiter = rateLimiter;
        _ledger = ledger;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<FeedbackFileOutcome> FileAsync(
        FeedbackPreviewPayload payload, string userProfileId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(payload);
        ArgumentException.ThrowIfNullOrEmpty(userProfileId);

        var digest = FeedbackPreviewToken.ContentDigest(
            payload.Title, payload.Body, payload.Labels, payload.FeedbackType);

        // Already answered? A replay creates nothing and is never rate limited.
        var existing = await _ledger.LookupAsync(payload.Jti, userProfileId, cancellationToken)
            .ConfigureAwait(false);

        if (existing.Outcome != FeedbackClaimOutcome.Won)
        {
            // A sibling request may have claimed a moment ago and still be in flight, which is what
            // a double-click looks like. Wait for it rather than refusing.
            return await AnswerFromLedgerAsync(existing, payload.Jti, userProfileId, cancellationToken)
                .ConfigureAwait(false);
        }

        // Cheap refusal before a claim row exists, so an over-limit caller does not burn its token.
        var peek = await _rateLimiter
            .PeekAsync(userProfileId, FeedbackRateKind.Submit, cancellationToken)
            .ConfigureAwait(false);

        if (!peek.Allowed)
        {
            return new FeedbackFileOutcome(FeedbackFileOutcomeKind.RateLimited, Rate: peek);
        }

        // The claim. Exactly one concurrent caller leaves here with Won.
        var claim = await _ledger.TryClaimAsync(
                new FeedbackClaimRequest(
                    payload.Jti,
                    userProfileId,
                    digest,
                    payload.RouteCategory,
                    payload.Platform,
                    payload.AppVersion,
                    DateTimeOffset.FromUnixTimeSeconds(payload.Exp)),
                cancellationToken)
            .ConfigureAwait(false);

        if (claim.Outcome != FeedbackClaimOutcome.Won)
        {
            return await AnswerFromLedgerAsync(claim, payload.Jti, userProfileId, cancellationToken)
                .ConfigureAwait(false);
        }

        var claimed = claim.Row!;

        // The winner pays.
        var allowance = await _rateLimiter
            .TryConsumeAsync(userProfileId, FeedbackRateKind.Submit, cancellationToken)
            .ConfigureAwait(false);

        if (!allowance.Allowed)
        {
            // Closed as Failed: nothing external has happened, so "no issue was created" is known.
            await _ledger.MarkFailedAsync(
                    payload.Jti, userProfileId, claimed.Version,
                    FeedbackFailureCodes.RateLimited, cancellationToken)
                .ConfigureAwait(false);

            return new FeedbackFileOutcome(FeedbackFileOutcomeKind.RateLimited, Rate: allowance);
        }

        // The token is fetched only by the claim winner. Every failure here happens before the
        // issue request is sent, so "no issue was created" is known and the row is closed — even
        // when the fetch throws or the caller has gone, or the row would sit in doubt forever.
        GitHubToken token;
        try
        {
            token = await _tokens.GetTokenAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                "A GitHub token could not be obtained; nothing was sent. Code={FailureCode} Exception={ExceptionType}",
                FeedbackFailureCodes.GitHubUnreachable, ex.GetType().Name);
            await _ledger.MarkFailedAsync(
                    payload.Jti, userProfileId, claimed.Version,
                    FeedbackFailureCodes.GitHubUnreachable, CancellationToken.None)
                .ConfigureAwait(false);
            if (ex is OperationCanceledException)
            {
                throw;
            }

            return new FeedbackFileOutcome(FeedbackFileOutcomeKind.GitHubUnavailable);
        }

        if (token.Value is null)
        {
            var (code, kind) = token.Failure switch
            {
                GitHubTokenFailure.Unauthorized => (FeedbackFailureCodes.GitHubUnauthorized, FeedbackFileOutcomeKind.GitHubRefused),
                GitHubTokenFailure.Unavailable => (FeedbackFailureCodes.GitHubUnreachable, FeedbackFileOutcomeKind.GitHubUnavailable),
                _ => (FeedbackFailureCodes.GitHubUnconfigured, FeedbackFileOutcomeKind.Unconfigured)
            };

            _logger.LogError("Feedback submission is unavailable. Code={FailureCode}", code);

            await _ledger.MarkFailedAsync(
                    payload.Jti, userProfileId, claimed.Version, code, cancellationToken)
                .ConfigureAwait(false);

            return new FeedbackFileOutcome(
                kind,
                GitHubStatus: kind == FeedbackFileOutcomeKind.GitHubRefused ? HttpStatusCode.Unauthorized : null);
        }

        return await CreateIssueAsync(payload, claimed, userProfileId, token.Value, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>The only place in the feedback lane that calls GitHub. Reached only while holding the claim.</summary>
    private async Task<FeedbackFileOutcome> CreateIssueAsync(
        FeedbackPreviewPayload payload,
        FeedbackSubmission claimed,
        string userProfileId,
        string githubToken,
        CancellationToken cancellationToken)
    {
        HttpResponseMessage response;

        try
        {
            var client = _httpClientFactory.CreateClient("GitHub");
            client.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", githubToken);

            // The signed payload, verbatim: the title, body, and labels here are the exact values
            // the learner approved.
            var issueBody = new
            {
                title = payload.Title,
                body = payload.Body,
                labels = payload.Labels
            };

            var json = JsonSerializer.Serialize(issueBody, JsonOptions);
            using var content = new StringContent(json, Encoding.UTF8, "application/json");

            response = await client
                .PostAsync($"/repos/{GitHubRepo}/issues", content, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException)
        {
            // The request may have reached GitHub before the connection failed. The row stays
            // Claimed — in doubt, and refused by every later submission.
            _logger.LogError(
                "Feedback submission could not reach GitHub. Code={FailureCode} Exception={ExceptionType}",
                FeedbackFailureCodes.GitHubUnreachable,
                ex.GetType().Name);

            return new FeedbackFileOutcome(FeedbackFileOutcomeKind.GitHubUnreachable);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                var code = ClassifyGitHubFailure(response.StatusCode);
                if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                {
                    // A revoked token or a removed installation: drop it so the next report
                    // fetches a fresh one instead of failing until the cached token expires.
                    _tokens.Invalidate(githubToken);
                }

                // The response body is NOT logged: GitHub echoes the submitted title and body back
                // in validation errors.
                _logger.LogError(
                    "GitHub refused the feedback issue. Status={StatusCode} Code={FailureCode}",
                    (int)response.StatusCode,
                    code);

                await _ledger.MarkFailedAsync(
                        payload.Jti, userProfileId, claimed.Version, code, cancellationToken)
                    .ConfigureAwait(false);

                return new FeedbackFileOutcome(
                    FeedbackFileOutcomeKind.GitHubRefused, GitHubStatus: response.StatusCode);
            }

            // Past this line an issue exists.
            int issueNumber;
            string issueUrl;
            string issueTitle;

            try
            {
                using var doc = await JsonDocument
                    .ParseAsync(
                        await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false),
                        cancellationToken: cancellationToken)
                    .ConfigureAwait(false);

                var root = doc.RootElement;
                issueUrl = root.TryGetProperty("html_url", out var urlElement)
                    ? urlElement.GetString() ?? string.Empty
                    : string.Empty;
                issueNumber = root.TryGetProperty("number", out var numberElement)
                    ? numberElement.GetInt32()
                    : 0;
                issueTitle = root.TryGetProperty("title", out var titleElement)
                    ? titleElement.GetString() ?? payload.Title
                    : payload.Title;
            }
            catch (Exception ex)
            {
                await _ledger.MarkCommittedAsync(
                        payload.Jti, userProfileId, claimed.Version,
                        FeedbackFailureCodes.SettlementFailed, cancellationToken)
                    .ConfigureAwait(false);

                _logger.LogError(
                    "The feedback issue was created but its identity could not be read. "
                    + "Code={FailureCode} Exception={ExceptionType}",
                    FeedbackFailureCodes.SettlementFailed,
                    ex.GetType().Name);

                return new FeedbackFileOutcome(FeedbackFileOutcomeKind.SettlementFailed);
            }

            bool settled;
            try
            {
                settled = await _ledger.SettleSubmittedAsync(
                        payload.Jti, userProfileId, claimed.Version,
                        issueNumber, issueUrl, Truncate(issueTitle, FeedbackPreviewToken.MaxTitleLength),
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    "The feedback issue was created but its receipt could not be stored. "
                    + "Code={FailureCode} Exception={ExceptionType}",
                    FeedbackFailureCodes.SettlementFailed,
                    ex.GetType().Name);
                settled = false;
            }

            if (!settled)
            {
                await _ledger.MarkCommittedAsync(
                        payload.Jti, userProfileId, claimed.Version,
                        FeedbackFailureCodes.SettlementFailed, cancellationToken)
                    .ConfigureAwait(false);

                _logger.LogError(
                    "Feedback receipt was not recorded for issue #{IssueNumber}. Code={FailureCode}",
                    issueNumber,
                    FeedbackFailureCodes.SettlementFailed);
            }

            _logger.LogInformation(
                "Created feedback issue #{IssueNumber} Route={RouteCategory} Platform={Platform} "
                + "Settled={Settled}",
                issueNumber,
                payload.RouteCategory,
                payload.Platform,
                settled);

            return new FeedbackFileOutcome(
                FeedbackFileOutcomeKind.Created, issueNumber, issueUrl, issueTitle);
        }
    }

    /// <summary>Answers a caller that did not win the claim, waiting out an in-flight winner first.</summary>
    private async Task<FeedbackFileOutcome> AnswerFromLedgerAsync(
        FeedbackClaimResult result, string jti, string userProfileId, CancellationToken cancellationToken)
    {
        var resolved = result.Outcome == FeedbackClaimOutcome.InDoubt
            ? await _ledger.WaitForSettlementAsync(jti, userProfileId, cancellationToken).ConfigureAwait(false)
            : result;

        switch (resolved.Outcome)
        {
            case FeedbackClaimOutcome.AlreadySettled when resolved.Row is { } row:
                return new FeedbackFileOutcome(
                    FeedbackFileOutcomeKind.Replayed,
                    row.IssueNumber ?? 0,
                    row.IssueUrl ?? string.Empty,
                    row.IssueTitle ?? string.Empty);

            case FeedbackClaimOutcome.ClosedWithoutIssue:
                _logger.LogInformation(
                    "A feedback submission was retried after it had already closed. Code={FailureCode}",
                    FeedbackFailureCodes.SubmissionClosed);
                return new FeedbackFileOutcome(FeedbackFileOutcomeKind.Closed);

            default:
                _logger.LogWarning(
                    "A feedback submission was retried while its outcome is unknown. Code={FailureCode}",
                    FeedbackFailureCodes.SubmissionInDoubt);
                return new FeedbackFileOutcome(FeedbackFileOutcomeKind.InDoubt);
        }
    }

    private static string ClassifyGitHubFailure(HttpStatusCode status) => status switch
    {
        HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => FeedbackFailureCodes.GitHubUnauthorized,
        HttpStatusCode.TooManyRequests => FeedbackFailureCodes.GitHubRateLimited,
        _ => FeedbackFailureCodes.GitHubRejected
    };

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max];
}
