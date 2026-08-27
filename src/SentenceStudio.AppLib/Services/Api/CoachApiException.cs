using System.Net;

namespace SentenceStudio.Services.Api;

/// <summary>
/// Typed failure raised by <see cref="CoachApiClient"/> when the coach API answers with a
/// problem response. The UI maps <see cref="ProblemType"/> onto a coach workspace state
/// (expired, limited, plan-version conflict, ...) instead of showing a generic failure.
/// </summary>
/// <remarks>
/// Problem type constants live in <c>SentenceStudio.Contracts.Coach.CoachProblemTypes</c>.
/// A caller that cannot match the type should fall back to the generic failed state.
/// </remarks>
public sealed class CoachApiException : Exception
{
    public CoachApiException(
        HttpStatusCode statusCode,
        string? problemType,
        string? title,
        string? detail,
        Exception? innerException = null)
        : base(BuildMessage(statusCode, problemType, title, detail), innerException)
    {
        StatusCode = statusCode;
        ProblemType = problemType;
        Title = title;
        Detail = detail;
    }

    /// <summary>HTTP status code returned by the coach API.</summary>
    public HttpStatusCode StatusCode { get; }

    /// <summary>
    /// RFC 7807 <c>type</c> value, matching a constant on <c>CoachProblemTypes</c> when the
    /// server produced a recognized coach problem. Null when the response had no problem body.
    /// </summary>
    public string? ProblemType { get; }

    /// <summary>RFC 7807 <c>title</c>, for diagnostics only. Never shown to the learner.</summary>
    public string? Title { get; }

    /// <summary>RFC 7807 <c>detail</c>, for diagnostics only. Never shown to the learner.</summary>
    public string? Detail { get; }

    private static string BuildMessage(HttpStatusCode statusCode, string? problemType, string? title, string? detail)
    {
        var descriptor = problemType ?? title ?? "coach-request-failed";
        return string.IsNullOrWhiteSpace(detail)
            ? $"Coach API request failed ({(int)statusCode} {statusCode}): {descriptor}"
            : $"Coach API request failed ({(int)statusCode} {statusCode}): {descriptor} - {detail}";
    }
}
