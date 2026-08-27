using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using SentenceStudio.Contracts.Feedback;

namespace SentenceStudio.Services.Api;

/// <summary>
/// Talks to the feedback endpoints, and — critically — never retries a submission.
/// </summary>
/// <remarks>
/// <para>
/// Every other API client in the app can be retried freely because every other call is either a
/// read or an idempotent write into our own database. This one can create a public GitHub issue
/// that no credential the app holds can delete, so the client's job is to carry the server's
/// answer back faithfully and stop.
/// </para>
/// <para>
/// That is why this returns a result rather than throwing. <c>EnsureSuccessStatusCode</c> collapses
/// "wait 43 seconds", "your report is already being filed, do not send it again", and "the network
/// blipped" into one exception, and a caller holding that exception has no way to tell the third
/// from the second — so it retries, and files a duplicate.
/// </para>
/// </remarks>
public sealed class FeedbackApiClient(HttpClient httpClient) : IFeedbackApiClient
{
    private readonly HttpClient _httpClient = httpClient;

    public async Task<FeedbackApiResult<FeedbackPreviewResponse>> PreviewAsync(
        FeedbackRequest request, CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await _httpClient
                .PostAsJsonAsync("/api/v1/feedback/preview", request, cancellationToken)
                .ConfigureAwait(false);

            if (response.IsSuccessStatusCode)
            {
                var value = await response.Content
                    .ReadFromJsonAsync<FeedbackPreviewResponse>(cancellationToken: cancellationToken)
                    .ConfigureAwait(false);

                return value is null
                    ? FeedbackApiResult<FeedbackPreviewResponse>.Failed(FeedbackApiFailure.Unavailable)
                    : FeedbackApiResult<FeedbackPreviewResponse>.Success(value);
            }

            return FeedbackApiResult<FeedbackPreviewResponse>.Failed(
                ClassifyPreview(response.StatusCode), RetryAfterOf(response));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return FeedbackApiResult<FeedbackPreviewResponse>.Failed(FeedbackApiFailure.Unavailable);
        }
    }

    public async Task<FeedbackApiResult<FeedbackSubmitResponse>> SubmitAsync(
        FeedbackSubmitRequest request, CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await _httpClient
                .PostAsJsonAsync("/api/v1/feedback/submit", request, cancellationToken)
                .ConfigureAwait(false);

            if (response.IsSuccessStatusCode)
            {
                var value = await response.Content
                    .ReadFromJsonAsync<FeedbackSubmitResponse>(cancellationToken: cancellationToken)
                    .ConfigureAwait(false);

                return value is null
                    ? FeedbackApiResult<FeedbackSubmitResponse>.Failed(FeedbackApiFailure.InDoubt)
                    : FeedbackApiResult<FeedbackSubmitResponse>.Success(value);
            }

            var code = await ProblemCodeOfAsync(response, cancellationToken).ConfigureAwait(false);

            return FeedbackApiResult<FeedbackSubmitResponse>.Failed(
                ClassifySubmit(response.StatusCode, code), RetryAfterOf(response));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            // The request may have reached the server, which may have filed the issue. The client
            // has no more information than the server does in the same situation, so it makes the
            // same call: assume nothing, and never re-send.
            return FeedbackApiResult<FeedbackSubmitResponse>.Failed(FeedbackApiFailure.InDoubt);
        }
    }

    /// <summary>
    /// Classifies a failed preview.
    /// </summary>
    /// <remarks>
    /// Deliberately different from <see cref="ClassifySubmit"/> on one status: a 400 here is the
    /// server refusing the <em>description</em> — empty, or past the length limit — and has nothing
    /// to do with a token, because a preview does not present one. Reusing the submit mapping would
    /// tell a learner whose report was too long that "this preview is no longer valid", and would
    /// take away a Submit button they had not yet reached.
    /// </remarks>
    private static FeedbackApiFailure ClassifyPreview(HttpStatusCode status) => status switch
    {
        HttpStatusCode.TooManyRequests => FeedbackApiFailure.RateLimited,
        _ => FeedbackApiFailure.Unavailable
    };

    /// <summary>
    /// Classifies a failed submission, using the server's closed problem code where it has one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// 409 is the status that matters, and it carries two opposite meanings: the submission closed
    /// having filed nothing, or its outcome is unknown. The status alone cannot separate them, so
    /// the code does.
    /// </para>
    /// <para>
    /// The fallback for a 409 with no recognised code is <see cref="FeedbackApiFailure.InDoubt"/>,
    /// not <see cref="FeedbackApiFailure.Closed"/>. That direction is deliberate: "we do not know"
    /// is always a safe thing to tell a learner, whereas asserting "nothing was filed" without the
    /// server having said so is a claim this client is not in a position to make.
    /// </para>
    /// </remarks>
    private static FeedbackApiFailure ClassifySubmit(HttpStatusCode status, string? code)
    {
        if (code == FeedbackProblemCodes.SubmissionClosed)
        {
            return FeedbackApiFailure.Closed;
        }

        if (code == FeedbackProblemCodes.SubmissionInDoubt)
        {
            return FeedbackApiFailure.InDoubt;
        }

        return status switch
        {
            HttpStatusCode.TooManyRequests => FeedbackApiFailure.RateLimited,
            HttpStatusCode.Conflict => FeedbackApiFailure.InDoubt,
            HttpStatusCode.BadRequest => FeedbackApiFailure.TokenRejected,
            _ => FeedbackApiFailure.Unavailable
        };
    }

    /// <summary>
    /// The <c>code</c> extension from a problem-details body, or null when there is not one.
    /// </summary>
    /// <remarks>
    /// Never throws. A failure response that is not JSON, or is JSON without the extension, is an
    /// ordinary outcome — an intermediary returning an HTML error page, a proxy timing out — and
    /// must degrade to "no code" so the status-based fallback decides.
    /// </remarks>
    private static async Task<string?> ProblemCodeOfAsync(
        HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            using var stream = await response.Content
                .ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);

            using var document = await JsonDocument
                .ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);

            if (document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty(
                    FeedbackProblemCodes.ExtensionName, out var element)
                && element.ValueKind == JsonValueKind.String)
            {
                return element.GetString();
            }
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or IOException)
        {
            // Fall through to the status-based classification.
        }

        return null;
    }

    /// <summary>
    /// The server's Retry-After, or null. Deliberately not defaulted to a guess: a made-up wait
    /// that is too short trains the client to hammer, and one that is too long is a worse
    /// experience than saying nothing.
    /// </summary>
    private static TimeSpan? RetryAfterOf(HttpResponseMessage response)
    {
        var retryAfter = response.Headers.RetryAfter;
        if (retryAfter is null)
        {
            return null;
        }

        if (retryAfter.Delta is { } delta)
        {
            return delta;
        }

        if (retryAfter.Date is { } date)
        {
            var wait = date - DateTimeOffset.UtcNow;
            return wait > TimeSpan.Zero ? wait : TimeSpan.Zero;
        }

        return null;
    }
}
