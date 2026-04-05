using System.Buffers.Text;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.AI;
using SentenceStudio.Contracts.Feedback;

namespace SentenceStudio.Api;

public static class FeedbackEndpoints
{
    private static readonly string[] AllowedLabels = ["bug", "enhancement"];
    private static readonly TimeSpan AiTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan TokenExpiry = TimeSpan.FromMinutes(10);

    private const string GitHubRepo = "davidortinau/SentenceStudio";
    private const string GitHubApiBase = "https://api.github.com";

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

    private static async Task<IResult> PreviewFeedback(
        [FromBody] FeedbackRequest request,
        ClaimsPrincipal user,
        [FromServices] IConfiguration configuration,
        [FromServices] ILoggerFactory loggerFactory,
        [FromServices] IChatClient? chatClient,
        CancellationToken cancellationToken)
    {
        var logger = loggerFactory.CreateLogger("FeedbackEndpoints");

        var userProfileId = user.FindFirstValue("user_profile_id");
        if (string.IsNullOrEmpty(userProfileId))
            return Results.Unauthorized();

        if (string.IsNullOrWhiteSpace(request.Description))
            return Results.BadRequest("Description is required.");

        if (request.Description.Length > 5000)
            return Results.BadRequest("Description must be 5000 characters or fewer.");

        string title;
        string feedbackType;
        string formattedBody;
        string[] labels;

        if (chatClient is not null)
        {
            var draft = await TryEnrichWithAiAsync(chatClient, request, logger, cancellationToken);
            if (draft is not null)
            {
                title = Truncate(draft.Title, 80);
                feedbackType = draft.FeedbackType is "bug" or "enhancement" ? draft.FeedbackType : "enhancement";
                labels = draft.Labels?.Where(l => AllowedLabels.Contains(l)).ToArray() ?? [feedbackType];
                formattedBody = FormatMarkdownBody(draft, request.ClientMetadata);
            }
            else
            {
                (title, feedbackType, labels, formattedBody) = BuildFallbackPreview(request);
            }
        }
        else
        {
            logger.LogWarning("IChatClient not available — using raw description for feedback preview");
            (title, feedbackType, labels, formattedBody) = BuildFallbackPreview(request);
        }

        var signingKey = configuration["Jwt:SigningKey"]
            ?? throw new InvalidOperationException("Jwt:SigningKey is not configured.");

        var previewToken = CreatePreviewToken(title, formattedBody, labels, feedbackType, signingKey);

        return Results.Ok(new FeedbackPreviewResponse
        {
            Title = title,
            FormattedBody = formattedBody,
            Labels = labels,
            FeedbackType = feedbackType,
            PreviewToken = previewToken
        });
    }

    private static async Task<IResult> SubmitFeedback(
        [FromBody] FeedbackSubmitRequest request,
        ClaimsPrincipal user,
        [FromServices] IConfiguration configuration,
        [FromServices] IHttpClientFactory httpClientFactory,
        [FromServices] ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        var logger = loggerFactory.CreateLogger("FeedbackEndpoints");

        var userProfileId = user.FindFirstValue("user_profile_id");
        if (string.IsNullOrEmpty(userProfileId))
            return Results.Unauthorized();

        if (string.IsNullOrWhiteSpace(request.PreviewToken))
            return Results.BadRequest("PreviewToken is required.");

        var signingKey = configuration["Jwt:SigningKey"]
            ?? throw new InvalidOperationException("Jwt:SigningKey is not configured.");

        var payload = ValidatePreviewToken(request.PreviewToken, signingKey);
        if (payload is null)
            return Results.BadRequest("Invalid or expired preview token.");

        var githubPat = configuration["GitHub:Pat"];
        if (string.IsNullOrWhiteSpace(githubPat))
        {
            logger.LogError("GitHub PAT is not configured — cannot create issue");
            return Results.Problem("Feedback submission is not available.", statusCode: 503);
        }

        try
        {
            var client = httpClientFactory.CreateClient("GitHub");
            client.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", githubPat);

            var issueBody = new
            {
                title = payload.Title,
                body = payload.Body,
                labels = payload.Labels
            };

            var json = JsonSerializer.Serialize(issueBody, JsonOptions);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            var response = await client.PostAsync(
                $"/repos/{GitHubRepo}/issues",
                content,
                cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
                logger.LogError("GitHub API returned {StatusCode}: {Body}",
                    (int)response.StatusCode, Truncate(errorBody, 500));

                return response.StatusCode switch
                {
                    System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden
                        => Results.Problem("GitHub authentication failed.", statusCode: 502),
                    System.Net.HttpStatusCode.UnprocessableEntity
                        => Results.Problem("GitHub rejected the issue. Labels may not exist.", statusCode: 422),
                    _ when errorBody.Contains("rate limit", StringComparison.OrdinalIgnoreCase)
                        => Results.Problem("GitHub rate limit exceeded. Try again later.", statusCode: 429),
                    _ => Results.Problem("Failed to create GitHub issue.", statusCode: 502)
                };
            }

            using var doc = await JsonDocument.ParseAsync(
                await response.Content.ReadAsStreamAsync(cancellationToken),
                cancellationToken: cancellationToken);

            var root = doc.RootElement;
            var issueUrl = root.GetProperty("html_url").GetString() ?? string.Empty;
            var issueNumber = root.GetProperty("number").GetInt32();
            var issueTitle = root.GetProperty("title").GetString() ?? string.Empty;

            logger.LogInformation("Created GitHub issue #{Number}: {Url}", issueNumber, issueUrl);

            return Results.Ok(new FeedbackSubmitResponse
            {
                IssueUrl = issueUrl,
                IssueNumber = issueNumber,
                Title = issueTitle
            });
        }
        catch (TaskCanceledException)
        {
            return Results.Problem("Request was cancelled.", statusCode: 499);
        }
        catch (HttpRequestException ex)
        {
            logger.LogError(ex, "Failed to reach GitHub API");
            return Results.Problem("Could not reach GitHub. Try again later.", statusCode: 502);
        }
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
            logger.LogWarning(ex, "AI enrichment failed — using raw description");
            return null;
        }
    }

    private static (string Title, string FeedbackType, string[] Labels, string FormattedBody) BuildFallbackPreview(
        FeedbackRequest request)
    {
        var feedbackType = request.FeedbackType is "bug" or "enhancement" ? request.FeedbackType : "enhancement";
        var title = feedbackType == "bug" ? "Bug Report" : "Feature Request";
        var labels = new[] { feedbackType };

        var sb = new StringBuilder();
        sb.AppendLine("## Description");
        sb.AppendLine();
        sb.AppendLine(request.Description);

        AppendClientMetadata(sb, request.ClientMetadata);

        return (title, feedbackType, labels, sb.ToString());
    }

    #endregion

    #region Markdown Formatting

    private static string FormatMarkdownBody(FeedbackDraft draft, ClientMetadata? metadata)
    {
        var sb = new StringBuilder();

        if (draft.FeedbackType == "bug")
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

    private static void AppendClientMetadata(StringBuilder sb, ClientMetadata? metadata)
    {
        if (metadata is null) return;

        sb.AppendLine();
        sb.AppendLine("<details>");
        sb.AppendLine("<summary>Client Metadata</summary>");
        sb.AppendLine();
        if (!string.IsNullOrWhiteSpace(metadata.AppVersion))
            sb.AppendLine($"- **App Version:** {metadata.AppVersion}");
        if (!string.IsNullOrWhiteSpace(metadata.Platform))
            sb.AppendLine($"- **Platform:** {metadata.Platform}");
        if (!string.IsNullOrWhiteSpace(metadata.CurrentRoute))
            sb.AppendLine($"- **Current Route:** {metadata.CurrentRoute}");
        if (metadata.Timestamp.HasValue)
            sb.AppendLine($"- **Timestamp:** {metadata.Timestamp.Value:u}");
        sb.AppendLine();
        sb.AppendLine("</details>");
    }

    #endregion

    #region HMAC Preview Token

    private sealed record PreviewPayload(string Title, string Body, string[] Labels, string FeedbackType, long Exp);

    private static string CreatePreviewToken(string title, string body, string[] labels, string feedbackType, string signingKey)
    {
        var payload = new PreviewPayload(
            title,
            body,
            labels,
            feedbackType,
            DateTimeOffset.UtcNow.Add(TokenExpiry).ToUnixTimeSeconds());

        var payloadJson = JsonSerializer.Serialize(payload, JsonOptions);
        var payloadBytes = Encoding.UTF8.GetBytes(payloadJson);
        var payloadBase64 = Base64UrlEncode(payloadBytes);

        var keyBytes = Encoding.UTF8.GetBytes(signingKey);
        var signatureBytes = HMACSHA256.HashData(keyBytes, payloadBytes);
        var signatureBase64 = Base64UrlEncode(signatureBytes);

        return $"{payloadBase64}.{signatureBase64}";
    }

    private static PreviewPayload? ValidatePreviewToken(string token, string signingKey)
    {
        var parts = token.Split('.');
        if (parts.Length != 2) return null;

        byte[] payloadBytes;
        byte[] providedSignature;
        try
        {
            payloadBytes = Base64UrlDecode(parts[0]);
            providedSignature = Base64UrlDecode(parts[1]);
        }
        catch
        {
            return null;
        }

        var keyBytes = Encoding.UTF8.GetBytes(signingKey);
        var expectedSignature = HMACSHA256.HashData(keyBytes, payloadBytes);

        if (!CryptographicOperations.FixedTimeEquals(expectedSignature, providedSignature))
            return null;

        try
        {
            var payload = JsonSerializer.Deserialize<PreviewPayload>(payloadBytes, JsonOptions);
            if (payload is null) return null;

            if (DateTimeOffset.UtcNow.ToUnixTimeSeconds() > payload.Exp)
                return null;

            return payload;
        }
        catch
        {
            return null;
        }
    }

    private static string Base64UrlEncode(byte[] data)
    {
        return Convert.ToBase64String(data)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    private static byte[] Base64UrlDecode(string base64Url)
    {
        var s = base64Url.Replace('-', '+').Replace('_', '/');
        switch (s.Length % 4)
        {
            case 2: s += "=="; break;
            case 3: s += "="; break;
        }
        return Convert.FromBase64String(s);
    }

    #endregion

    private static string Truncate(string value, int maxLength)
        => value.Length <= maxLength ? value : value[..maxLength];
}
