using System.Net;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using SentenceStudio.Api.Coach.Telemetry;
using SentenceStudio.Api.Feedback;
using SentenceStudio.Api.Feedback.Persistence;
using SentenceStudio.Contracts;
using SentenceStudio.Contracts.Feedback;

namespace SentenceStudio.Api;

public static class FeedbackEndpoints
{
    private static readonly TimeSpan AiTimeout = TimeSpan.FromSeconds(15);

    private const string GitHubRepo = "davidortinau/SentenceStudio";
    private const int MaxDescriptionLength = 5000;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static WebApplication MapFeedbackEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/v1/feedback").RequireAuthorization();

        group.MapPost("/preview", PreviewFeedback);
        group.MapPost("/submit", SubmitFeedback);

        return app;
    }

    // ------------------------------------------------------------------------------ preview

    private static async Task<IResult> PreviewFeedback(
        [FromBody] FeedbackRequest request,
        ClaimsPrincipal user,
        HttpContext http,
        [FromServices] ILoggerFactory loggerFactory,
        [FromServices] IFeedbackHmacKeyProvider keyProvider,
        [FromServices] IFeedbackRateLimiter rateLimiter,
        [FromServices] IOptions<FeedbackOptions> options,
        [FromServices] TimeProvider time,
        [FromServices] IChatClient? chatClient,
        CancellationToken cancellationToken)
    {
        var logger = loggerFactory.CreateLogger("FeedbackEndpoints");

        var userProfileId = user.FindFirstValue(AuthClaimTypes.UserProfileId);
        if (string.IsNullOrEmpty(userProfileId))
            return Results.Unauthorized();

        if (string.IsNullOrWhiteSpace(request.Description))
            return Results.BadRequest("Description is required.");

        if (request.Description.Length > MaxDescriptionLength)
            return Results.BadRequest($"Description must be {MaxDescriptionLength} characters or fewer.");

        // Consumed before the AI call, not after. A preview costs a model round-trip against
        // caller-supplied text, so the limit has to gate the spend rather than the response.
        var allowance = await rateLimiter
            .TryConsumeAsync(userProfileId, FeedbackRateKind.Preview, cancellationToken)
            .ConfigureAwait(false);

        if (!allowance.Allowed)
        {
            return RateLimited(http, logger, FeedbackRateKind.Preview, allowance);
        }

        var metadata = FeedbackClientMetadataNormalizer.Normalize(request.ClientMetadata);

        string title;
        string feedbackType;
        string formattedBody;
        string[] labels;

        var draft = chatClient is not null
            ? await TryEnrichWithAiAsync(chatClient, request, logger, cancellationToken).ConfigureAwait(false)
            : null;

        if (chatClient is null)
        {
            logger.LogWarning("IChatClient not available — using raw description for feedback preview");
        }

        if (draft is not null)
        {
            title = Truncate(
                string.IsNullOrWhiteSpace(draft.Title) ? DefaultTitle(draft.FeedbackType) : draft.Title,
                FeedbackPreviewToken.MaxTitleLength);
            feedbackType = FeedbackLabels.NormalizeType(draft.FeedbackType);
            labels = FeedbackLabels.Sanitize(draft.Labels, feedbackType);
            formattedBody = FormatMarkdownBody(draft, feedbackType, metadata);
        }
        else
        {
            (title, feedbackType, labels, formattedBody) = BuildFallbackPreview(request, metadata);
        }

        formattedBody = Truncate(formattedBody, FeedbackPreviewToken.MaxBodyLength);

        var now = time.GetUtcNow();
        var payload = new FeedbackPreviewPayload(
            FeedbackPreviewToken.NewJti(),
            title,
            formattedBody,
            labels,
            feedbackType,
            userProfileId,
            metadata.RouteCategory,
            metadata.Platform,
            metadata.AppVersion,
            now.ToUnixTimeSeconds(),
            now.Add(options.Value.TokenLifetime).ToUnixTimeSeconds());

        var previewToken = FeedbackPreviewToken.Create(payload, keyProvider.Key);

        return Results.Ok(new FeedbackPreviewResponse
        {
            Title = title,
            FormattedBody = formattedBody,
            Labels = labels,
            FeedbackType = feedbackType,
            PreviewToken = previewToken
        });
    }

    // ------------------------------------------------------------------------------- submit

    /// <summary>
    /// Redeems a preview token for exactly one public GitHub issue.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The ordering below is the design, and each step is where it is for a reason that the
    /// obvious alternative gets wrong.
    /// </para>
    /// <list type="number">
    /// <item>
    /// <b>Verify, then check ownership.</b> Nothing unsigned reaches a decision, and a token
    /// presented by somebody other than its owner is refused with the same message an invalid one
    /// gets — a distinguishable response would turn this route into an oracle for whether a
    /// captured token is still live.
    /// </item>
    /// <item>
    /// <b>Look the token up before touching the limiter.</b> A replay creates no issue and costs
    /// nothing, so charging it would punish exactly the client that is behaving correctly: one
    /// whose response was lost and that retried.
    /// </item>
    /// <item>
    /// <b>Peek the limit, then claim, then consume.</b> Consuming before the claim would charge
    /// every loser of a double-submit race for an issue it never filed. Consuming after the claim
    /// means the winner — the only caller that can call GitHub — is the only caller that pays.
    /// </item>
    /// <item>
    /// <b>Call GitHub only while holding the claim.</b> Every other path returns before that point.
    /// </item>
    /// <item>
    /// <b>Settle, and treat every failure by what it proves.</b> A non-success status proves no
    /// issue exists and closes the row; a transport failure proves nothing and leaves it in doubt;
    /// a created issue whose receipt will not store is recorded as committed. No path reopens a row
    /// for retry, because once the request has left there is no failure that proves an issue was
    /// <em>not</em> created.
    /// </item>
    /// </list>
    /// </remarks>
    private static async Task<IResult> SubmitFeedback(
        [FromBody] FeedbackSubmitRequest request,
        ClaimsPrincipal user,
        HttpContext http,
        [FromServices] IConfiguration configuration,
        [FromServices] IHttpClientFactory httpClientFactory,
        [FromServices] ILoggerFactory loggerFactory,
        [FromServices] IFeedbackHmacKeyProvider keyProvider,
        [FromServices] IFeedbackRateLimiter rateLimiter,
        [FromServices] IFeedbackSubmissionLedger ledger,
        [FromServices] TimeProvider time,
        CancellationToken cancellationToken)
    {
        var logger = loggerFactory.CreateLogger("FeedbackEndpoints");

        var userProfileId = user.FindFirstValue(AuthClaimTypes.UserProfileId);
        if (string.IsNullOrEmpty(userProfileId))
            return Results.Unauthorized();

        var rejection = FeedbackPreviewToken.TryValidate(
            request.PreviewToken, keyProvider.Key, time.GetUtcNow(), out var payload);

        if (rejection != FeedbackTokenRejection.None || payload is null)
        {
            return RejectToken(logger, rejection);
        }

        if (!string.Equals(payload.OwnerProfileId, userProfileId, StringComparison.Ordinal))
        {
            // Neither identifier is logged. Naming both would write a durable, searchable record
            // linking two accounts on a path any caller can trigger by replaying a token they
            // found — an audit trail of who tried to impersonate whom is itself the disclosure.
            logger.LogWarning(
                "Feedback preview token presented by a caller that does not own it. Code={FailureCode}",
                FeedbackFailureCodes.TokenOwnerMismatch);

            return RejectToken(logger, FeedbackTokenRejection.Invalid, alreadyLogged: true);
        }

        var digest = FeedbackPreviewToken.ContentDigest(
            payload.Title, payload.Body, payload.Labels, payload.FeedbackType);

        // (2) Already answered? A replay creates nothing and is never rate limited.
        var existing = await ledger.LookupAsync(payload.Jti, userProfileId, cancellationToken)
            .ConfigureAwait(false);

        if (existing.Outcome != FeedbackClaimOutcome.Won)
        {
            // Not necessarily settled: a sibling request may have claimed a moment ago and still be
            // in flight, which is precisely what a double-click looks like. Wait for it rather than
            // refusing, or the learner is told their report failed while it is being filed.
            return await AnswerFromLedgerAsync(
                    ledger, logger, existing, payload.Jti, userProfileId, cancellationToken)
                .ConfigureAwait(false);
        }

        // (3a) Cheap refusal before a claim row exists, so an over-limit caller does not burn its
        // token. Advisory only — another replica may take the last slot before the consume below.
        var peek = await rateLimiter
            .PeekAsync(userProfileId, FeedbackRateKind.Submit, cancellationToken)
            .ConfigureAwait(false);

        if (!peek.Allowed)
        {
            return RateLimited(http, logger, FeedbackRateKind.Submit, peek);
        }

        // (3b) The claim. Exactly one concurrent caller leaves here with Won.
        var claim = await ledger.TryClaimAsync(
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
            // Lost the race, or arrived after it. Wait briefly for the winner so the honest answer
            // is its receipt rather than a refusal for a submission that is succeeding right now.
            return await AnswerFromLedgerAsync(
                    ledger, logger, claim, payload.Jti, userProfileId, cancellationToken)
                .ConfigureAwait(false);
        }

        var claimed = claim.Row!;

        // (3c) The winner pays.
        var allowance = await rateLimiter
            .TryConsumeAsync(userProfileId, FeedbackRateKind.Submit, cancellationToken)
            .ConfigureAwait(false);

        if (!allowance.Allowed)
        {
            // Closed as Failed, not deleted and not left open: nothing external has happened, so
            // "no issue was created" is known rather than assumed, and a terminal row keeps another
            // replica from retrying the same token in the same second.
            await ledger.MarkFailedAsync(
                    payload.Jti, userProfileId, claimed.Version,
                    FeedbackFailureCodes.RateLimited, cancellationToken)
                .ConfigureAwait(false);

            return RateLimited(http, logger, FeedbackRateKind.Submit, allowance);
        }

        var githubPat = configuration["GitHub:Pat"];
        if (string.IsNullOrWhiteSpace(githubPat))
        {
            logger.LogError(
                "Feedback submission is unavailable. Code={FailureCode}",
                FeedbackFailureCodes.GitHubUnconfigured);

            await ledger.MarkFailedAsync(
                    payload.Jti, userProfileId, claimed.Version,
                    FeedbackFailureCodes.GitHubUnconfigured, cancellationToken)
                .ConfigureAwait(false);

            return Results.Problem("Feedback submission is not available.", statusCode: 503);
        }

        return await CreateIssueAsync(
                payload, claimed, userProfileId, githubPat,
                httpClientFactory, ledger, logger, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// The only place in the feedback lane that calls GitHub. Reached only while holding the claim.
    /// </summary>
    private static async Task<IResult> CreateIssueAsync(
        FeedbackPreviewPayload payload,
        FeedbackSubmission claimed,
        string userProfileId,
        string githubPat,
        IHttpClientFactory httpClientFactory,
        IFeedbackSubmissionLedger ledger,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        HttpResponseMessage response;

        try
        {
            var client = httpClientFactory.CreateClient("GitHub");
            client.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", githubPat);

            // The signed payload, verbatim. Nothing between the preview the learner approved and
            // this request body re-derives, re-formats, or re-labels anything: the title, body, and
            // labels here are the exact values the signature covers, which is what makes the
            // preview a promise rather than an illustration.
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
            // The request may have reached GitHub and created an issue before the connection
            // failed. Nothing here can distinguish that from a request that never arrived, so the
            // row stays Claimed — in doubt, and refused by every later submission. Closing it as
            // Failed would assert something unknown, and the cost of being wrong is a duplicate
            // public issue.
            logger.LogError(
                "Feedback submission could not reach GitHub. Code={FailureCode} Exception={ExceptionType}",
                FeedbackFailureCodes.GitHubUnreachable,
                ex.GetType().Name);

            return Results.Problem(
                "We could not confirm whether your report reached GitHub. Please check the "
                + "repository before submitting again — we will not re-send it automatically.",
                statusCode: 502);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                // GitHub's issue creation is atomic per request: a non-success status means no
                // issue exists, which is the one external failure that can be closed as Failed.
                var code = ClassifyGitHubFailure(response.StatusCode);

                // The response body is NOT logged. GitHub echoes the submitted title and body back
                // in validation errors, so logging it would copy learner text into operator logs on
                // exactly the path where something already went wrong.
                logger.LogError(
                    "GitHub refused the feedback issue. Status={StatusCode} Code={FailureCode}",
                    (int)response.StatusCode,
                    code);

                await ledger.MarkFailedAsync(
                        payload.Jti, userProfileId, claimed.Version, code, cancellationToken)
                    .ConfigureAwait(false);

                return response.StatusCode switch
                {
                    HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
                        => Results.Problem("GitHub authentication failed.", statusCode: 502),
                    HttpStatusCode.UnprocessableEntity
                        => Results.Problem("GitHub rejected the issue. Labels may not exist.", statusCode: 422),
                    HttpStatusCode.TooManyRequests
                        => Results.Problem("GitHub rate limit exceeded. Try again later.", statusCode: 429),
                    _ => Results.Problem("Failed to create GitHub issue.", statusCode: 502)
                };
            }

            // Past this line an issue exists. Everything below is bookkeeping about an event that
            // has already happened in public and cannot be taken back.
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
                await ledger.MarkCommittedAsync(
                        payload.Jti, userProfileId, claimed.Version,
                        FeedbackFailureCodes.SettlementFailed, cancellationToken)
                    .ConfigureAwait(false);

                logger.LogError(
                    "The feedback issue was created but its identity could not be read. "
                    + "Code={FailureCode} Exception={ExceptionType}",
                    FeedbackFailureCodes.SettlementFailed,
                    ex.GetType().Name);

                return Results.Problem(
                    "Your report was filed, but we could not record the link to it. It will not be "
                    + "sent again.",
                    statusCode: 502);
            }

            bool settled;
            try
            {
                settled = await ledger.SettleSubmittedAsync(
                        payload.Jti, userProfileId, claimed.Version,
                        issueNumber, issueUrl, Truncate(issueTitle, FeedbackPreviewToken.MaxTitleLength),
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                logger.LogError(
                    "The feedback issue was created but its receipt could not be stored. "
                    + "Code={FailureCode} Exception={ExceptionType}",
                    FeedbackFailureCodes.SettlementFailed,
                    ex.GetType().Name);
                settled = false;
            }

            if (!settled)
            {
                // Best effort: record that an issue definitely exists even though we cannot say
                // which. If this write fails too the row stays Claimed, which also refuses every
                // retry — there is no failure ordering that produces a re-postable row.
                await ledger.MarkCommittedAsync(
                        payload.Jti, userProfileId, claimed.Version,
                        FeedbackFailureCodes.SettlementFailed, cancellationToken)
                    .ConfigureAwait(false);

                logger.LogError(
                    "Feedback receipt was not recorded for issue #{IssueNumber}. Code={FailureCode}",
                    issueNumber,
                    FeedbackFailureCodes.SettlementFailed);
            }

            logger.LogInformation(
                "Created feedback issue #{IssueNumber} Route={RouteCategory} Platform={Platform} "
                + "Settled={Settled}",
                issueNumber,
                payload.RouteCategory,
                payload.Platform,
                settled);

            return Results.Ok(new FeedbackSubmitResponse
            {
                IssueUrl = issueUrl,
                IssueNumber = issueNumber,
                Title = issueTitle,
                Outcome = FeedbackSubmitOutcome.Created
            });
        }
    }

    // ------------------------------------------------------------------------------ answers

    /// <summary>
    /// Answers a caller that did not win the claim, waiting out an in-flight winner first.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The single place a non-winner is answered, deliberately. There are two of them — the caller
    /// that found a row before claiming, and the caller whose insert lost — and when they were
    /// handled separately only one of them waited. That asymmetry is invisible in a serial test and
    /// produces, under a real double-click, a 409 for a report that was filed successfully one
    /// hundred milliseconds later.
    /// </para>
    /// <para>
    /// The wait is bounded and only applies to the in-doubt case. A row that is already settled or
    /// already closed has nothing to wait for.
    /// </para>
    /// </remarks>
    private static async Task<IResult> AnswerFromLedgerAsync(
        IFeedbackSubmissionLedger ledger,
        ILogger logger,
        FeedbackClaimResult result,
        string jti,
        string userProfileId,
        CancellationToken cancellationToken)
    {
        var resolved = result.Outcome == FeedbackClaimOutcome.InDoubt
            ? await ledger.WaitForSettlementAsync(jti, userProfileId, cancellationToken)
                .ConfigureAwait(false)
            : result;

        return AnswerFromLedger(logger, resolved);
    }

    private static IResult AnswerFromLedger(ILogger logger, FeedbackClaimResult result) =>
        result.Outcome switch
        {
            FeedbackClaimOutcome.AlreadySettled when result.Row is { } row => Results.Ok(
                new FeedbackSubmitResponse
                {
                    IssueUrl = row.IssueUrl ?? string.Empty,
                    IssueNumber = row.IssueNumber ?? 0,
                    Title = row.IssueTitle ?? string.Empty,
                    Outcome = FeedbackSubmitOutcome.Replayed
                }),

            FeedbackClaimOutcome.ClosedWithoutIssue => Closed(logger),

            // Won is impossible here — this method is only reached when the caller did not win —
            // and every remaining case is in doubt, which fails closed.
            _ => InDoubt(logger)
        };

    private static IResult Closed(ILogger logger)
    {
        logger.LogInformation(
            "A feedback submission was retried after it had already closed. Code={FailureCode}",
            FeedbackFailureCodes.SubmissionClosed);

        // Same status as the in-doubt refusal, different code — and the difference is the whole
        // point. This row proves no issue was created, so the honest message is "it was not filed,
        // write it again". Answering with the in-doubt copy would send a learner to search a public
        // repository for something that is definitely not there, and would leave them believing
        // they might have filed it.
        return Problem(
            "That report was not filed. Its preview has already been used, so please write it "
            + "again — nothing was sent to GitHub.",
            StatusCodes.Status409Conflict,
            FeedbackFailureCodes.SubmissionClosed);
    }

    private static IResult InDoubt(ILogger logger)
    {
        logger.LogWarning(
            "A feedback submission was retried while its outcome is unknown. Code={FailureCode}",
            FeedbackFailureCodes.SubmissionInDoubt);

        return Problem(
            "A submission for this report is already in progress or its result is unknown. We will "
            + "not send it twice — please check GitHub before writing it again.",
            StatusCodes.Status409Conflict,
            FeedbackFailureCodes.SubmissionInDoubt);
    }

    /// <summary>
    /// A problem response carrying a closed discriminator alongside the prose.
    /// </summary>
    /// <remarks>
    /// The detail string is for a human reading a log or a network trace; the code is what the
    /// client branches on. Parsing the prose would be the alternative, and it would break the first
    /// time somebody improved the wording or a translation was introduced.
    /// </remarks>
    private static IResult Problem(string detail, int statusCode, string code) =>
        Results.Problem(
            detail: detail,
            statusCode: statusCode,
            extensions: new Dictionary<string, object?>
            {
                [FeedbackProblemCodes.ExtensionName] = code
            });

    private static IResult RejectToken(
        ILogger logger, FeedbackTokenRejection rejection, bool alreadyLogged = false)
    {
        if (!alreadyLogged)
        {
            var code = rejection switch
            {
                FeedbackTokenRejection.Expired => FeedbackFailureCodes.TokenExpired,
                FeedbackTokenRejection.PayloadRejected => FeedbackFailureCodes.TokenPayloadRejected,
                _ => FeedbackFailureCodes.TokenInvalid
            };

            logger.LogWarning("Feedback preview token refused. Code={FailureCode}", code);
        }

        // One message for every rejection reason. Distinguishing "expired" from "not yours" from
        // "forged" would let a caller holding a token they should not have learn which it is.
        return Results.BadRequest("Invalid or expired preview token.");
    }

    private static IResult RateLimited(
        HttpContext http, ILogger logger, FeedbackRateKind kind, FeedbackRateDecision decision)
    {
        var seconds = decision.RetryAfterSeconds;

        // Truthful, and on the header the client actually reads. A body-only hint is invisible to
        // every HTTP client's built-in retry, and a padded value trains clients to ignore it.
        http.Response.Headers.RetryAfter =
            seconds.ToString(System.Globalization.CultureInfo.InvariantCulture);

        logger.LogInformation(
            "Feedback request refused by a per-owner limit. Kind={RateKind} Code={FailureCode} "
            + "RetryAfterSeconds={RetryAfterSeconds}",
            kind,
            decision.Reason ?? FeedbackFailureCodes.RateLimited,
            seconds);

        return Problem(
            $"You have reached the feedback limit. Try again in {seconds} second(s).",
            StatusCodes.Status429TooManyRequests,
            FeedbackFailureCodes.RateLimited);
    }

    private static string ClassifyGitHubFailure(HttpStatusCode status) => status switch
    {
        HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => FeedbackFailureCodes.GitHubUnauthorized,
        HttpStatusCode.TooManyRequests => FeedbackFailureCodes.GitHubRateLimited,
        _ => FeedbackFailureCodes.GitHubRejected
    };

    #region AI Enrichment

    private record FeedbackDraft(
        string Title,
        string FeedbackType,
        string Summary,
        string[] StepsToReproduce,
        string? ExpectedBehavior,
        string? ActualBehavior,
        string[] Labels);

    private static async Task<FeedbackDraft?> TryEnrichWithAiAsync(
        IChatClient chatClient,
        FeedbackRequest request,
        ILogger logger,
        CancellationToken requestCancellation)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(requestCancellation);
            cts.CancelAfter(AiTimeout);

            var userMessage = $"Feedback type: {request.FeedbackType ?? "auto-detect"}\n\n{request.Description}";

            var options = new ChatOptions
            {
                Instructions = """
                    You are a technical writer helping users file clear GitHub issues for a language-learning app called SentenceStudio.

                    Given the user's feedback, produce a structured report:
                    - title: concise issue title (max 80 chars)
                    - feedbackType: "bug" or "enhancement"
                    - summary: 1-2 sentence description of the issue
                    - stepsToReproduce: array of strings (for bugs only, infer from description)
                    - expectedBehavior: what should happen (for bugs)
                    - actualBehavior: what actually happens (for bugs)
                    - labels: array from ["bug", "enhancement"] only

                    Be concise. Do not invent details the user did not mention.
                    If the user's description is too vague to determine steps, leave stepsToReproduce empty.
                    """
            };

            var response = await chatClient.GetResponseAsync<FeedbackDraft>(
                [new ChatMessage(ChatRole.User, userMessage)],
                options,
                cancellationToken: cts.Token);

            return response.Result;
        }
        catch (OperationCanceledException) when (!requestCancellation.IsCancellationRequested)
        {
            logger.LogWarning("AI enrichment timed out after {Timeout}s — using raw description", AiTimeout.TotalSeconds);
            return null;
        }
        catch (Exception ex)
        {
            // The submitted feedback text is user content and is echoed back by content-filter
            // and token-limit failures, so only content-free facts are logged.
            // See CoachExceptionSanitizer.
            var facts = CoachExceptionSanitizer.Describe(ex);
            logger.LogWarning(
                "AI enrichment failed — using raw description. " +
                "Category={FailureCategory} ProviderStatus={ProviderStatus} " +
                "ProviderCode={ProviderErrorCode} InnerDepth={InnerDepth}",
                facts.Category, facts.ProviderStatus, facts.ProviderErrorCode, facts.InnerDepth);
            return null;
        }
    }

    private static string DefaultTitle(string? feedbackType) =>
        FeedbackLabels.NormalizeType(feedbackType) == FeedbackLabels.Bug
            ? "Bug Report"
            : "Feature Request";

    private static (string Title, string FeedbackType, string[] Labels, string FormattedBody) BuildFallbackPreview(
        FeedbackRequest request,
        NormalizedClientMetadata metadata)
    {
        var feedbackType = FeedbackLabels.NormalizeType(request.FeedbackType);
        var title = DefaultTitle(feedbackType);
        var labels = FeedbackLabels.Sanitize([feedbackType], feedbackType);

        var sb = new StringBuilder();
        sb.AppendLine("## Description");
        sb.AppendLine();
        sb.AppendLine(request.Description);

        AppendClientMetadata(sb, metadata);

        return (title, feedbackType, labels, sb.ToString());
    }

    #endregion

    #region Markdown Formatting

    private static string FormatMarkdownBody(
        FeedbackDraft draft, string feedbackType, NormalizedClientMetadata metadata)
    {
        var sb = new StringBuilder();

        if (feedbackType == FeedbackLabels.Bug)
        {
            sb.AppendLine("## Bug Report");
            sb.AppendLine();
            sb.AppendLine("### Description");
            sb.AppendLine(draft.Summary);

            if (draft.StepsToReproduce is { Length: > 0 })
            {
                sb.AppendLine();
                sb.AppendLine("### Steps to Reproduce");
                for (var i = 0; i < draft.StepsToReproduce.Length; i++)
                    sb.AppendLine($"{i + 1}. {draft.StepsToReproduce[i]}");
            }

            if (!string.IsNullOrWhiteSpace(draft.ExpectedBehavior))
            {
                sb.AppendLine();
                sb.AppendLine("### Expected Behavior");
                sb.AppendLine(draft.ExpectedBehavior);
            }

            if (!string.IsNullOrWhiteSpace(draft.ActualBehavior))
            {
                sb.AppendLine();
                sb.AppendLine("### Actual Behavior");
                sb.AppendLine(draft.ActualBehavior);
            }
        }
        else
        {
            sb.AppendLine("## Feature Request");
            sb.AppendLine();
            sb.AppendLine("### Description");
            sb.AppendLine(draft.Summary);
        }

        AppendClientMetadata(sb, metadata);

        return sb.ToString();
    }

    /// <summary>
    /// Renders the client context into the public issue body.
    /// </summary>
    /// <remarks>
    /// Takes <see cref="NormalizedClientMetadata"/> rather than the wire type on purpose: the only
    /// way to reach this method is through the normaliser, so "was this scrubbed?" is answered by
    /// the signature instead of by remembering to call something.
    /// </remarks>
    private static void AppendClientMetadata(StringBuilder sb, NormalizedClientMetadata metadata)
    {
        if (metadata.IsEmpty) return;

        sb.AppendLine();
        sb.AppendLine("<details>");
        sb.AppendLine("<summary>Client Metadata</summary>");
        sb.AppendLine();
        if (metadata.AppVersion != FeedbackClientMetadataNormalizer.UnknownVersion)
            sb.AppendLine($"- **App Version:** {metadata.AppVersion}");
        if (metadata.Platform != FeedbackPlatform.Unknown)
            sb.AppendLine($"- **Platform:** {metadata.Platform}");
        if (metadata.RouteCategory != FeedbackRouteCategory.Unknown)
            sb.AppendLine($"- **Area:** {metadata.RouteCategory}");
        if (metadata.TimestampUtc.HasValue)
            sb.AppendLine($"- **Timestamp:** {metadata.TimestampUtc.Value:u}");
        sb.AppendLine();
        sb.AppendLine("</details>");
    }

    #endregion

    private static string Truncate(string value, int maxLength)
        => value.Length <= maxLength ? value : value[..maxLength];
}
