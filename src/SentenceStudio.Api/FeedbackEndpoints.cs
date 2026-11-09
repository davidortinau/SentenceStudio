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

    private const int MaxDescriptionLength = 5000;

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
        [FromServices] ILoggerFactory loggerFactory,
        [FromServices] IFeedbackHmacKeyProvider keyProvider,
        [FromServices] IFeedbackIssueFiler filer,
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

        // Steps (2) to (5) — lookup, limit, claim, GitHub, settle — live in the filer, which Sam's
        // feedback handler shares, so there is one place in the app that calls GitHub.
        var outcome = await filer.FileAsync(payload, userProfileId, cancellationToken)
            .ConfigureAwait(false);

        return ToResult(http, logger, outcome);
    }

    /// <summary>Maps a filing outcome onto this endpoint's established responses.</summary>
    private static IResult ToResult(HttpContext http, ILogger logger, FeedbackFileOutcome outcome) =>
        outcome.Kind switch
        {
            FeedbackFileOutcomeKind.Created or FeedbackFileOutcomeKind.Replayed => Results.Ok(
                new FeedbackSubmitResponse
                {
                    IssueUrl = outcome.IssueUrl,
                    IssueNumber = outcome.IssueNumber,
                    Title = outcome.IssueTitle,
                    Outcome = outcome.Kind == FeedbackFileOutcomeKind.Created
                        ? FeedbackSubmitOutcome.Created
                        : FeedbackSubmitOutcome.Replayed
                }),

            FeedbackFileOutcomeKind.RateLimited =>
                RateLimited(http, logger, FeedbackRateKind.Submit, outcome.Rate!.Value),

            FeedbackFileOutcomeKind.Closed => Closed(),

            FeedbackFileOutcomeKind.Unconfigured =>
                Results.Problem("Feedback submission is not available.", statusCode: 503),

            FeedbackFileOutcomeKind.GitHubUnavailable => Results.Problem(
                "GitHub could not be reached and nothing was sent. Please try again in a few minutes.",
                statusCode: 503),

            FeedbackFileOutcomeKind.GitHubUnreachable => Results.Problem(
                "We could not confirm whether your report reached GitHub. Please check the "
                + "repository before submitting again — we will not re-send it automatically.",
                statusCode: 502),

            FeedbackFileOutcomeKind.GitHubRefused => outcome.GitHubStatus switch
            {
                HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
                    => Results.Problem("GitHub authentication failed.", statusCode: 502),
                HttpStatusCode.UnprocessableEntity
                    => Results.Problem("GitHub rejected the issue. Labels may not exist.", statusCode: 422),
                HttpStatusCode.TooManyRequests
                    => Results.Problem("GitHub rate limit exceeded. Try again later.", statusCode: 429),
                _ => Results.Problem("Failed to create GitHub issue.", statusCode: 502)
            },

            FeedbackFileOutcomeKind.SettlementFailed => Results.Problem(
                "Your report was filed, but we could not record the link to it. It will not be "
                + "sent again.",
                statusCode: 502),

            _ => InDoubt()
        };

    private static IResult Closed()
    {

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

    private static IResult InDoubt()
    {

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
