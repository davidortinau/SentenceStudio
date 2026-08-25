using SentenceStudio.Contracts.Feedback;

namespace SentenceStudio.WebUI.Services;

/// <summary>
/// Maps the browser's current path onto the closed <see cref="FeedbackRouteCategory"/> set.
/// </summary>
/// <remarks>
/// <para>
/// The point of this type is what it <em>cannot</em> return. It takes a route — which may carry an
/// entity id, a query string, or a fragment — and returns a member of a fixed enum. There is no
/// pass-through branch, no "unrecognised, send it as text" fallback, and no interpolation of any
/// part of the input into the result. An unfamiliar route becomes
/// <see cref="FeedbackRouteCategory.Unknown"/>, which is a small triage loss and the reason a
/// future page cannot leak its parameters into a public issue by being added without anyone
/// thinking about feedback.
/// </para>
/// <para>
/// The server does not rely on this. It re-normalises whatever arrives and clamps undeclared
/// ordinals to <see cref="FeedbackRouteCategory.Unknown"/>, because a client is not a trust
/// boundary. This exists so the honest client sends something useful, not so the server can stop
/// checking.
/// </para>
/// </remarks>
public static class FeedbackRouteCategoryMap
{
    /// <summary>
    /// The category for <paramref name="relativePath"/>, which may be absolute-from-root, may
    /// carry a query or fragment, and may be null.
    /// </summary>
    public static FeedbackRouteCategory Categorize(string? relativePath)
    {
        var segment = FirstSegment(relativePath);

        if (segment.Length == 0)
        {
            return FeedbackRouteCategory.Home;
        }

        return segment switch
        {
            "feedback" => FeedbackRouteCategory.Feedback,

            "auth" or "onboarding" => FeedbackRouteCategory.Account,

            "profile" or "settings" => FeedbackRouteCategory.Profile,

            "coach" => FeedbackRouteCategory.Coach,

            "resources" or "vocabulary" or "import" or "import-content" or "media-import"
                => FeedbackRouteCategory.Resources,

            "skills" => FeedbackRouteCategory.Skills,

            "activity-log" or "diary" or "debug" => FeedbackRouteCategory.Progress,

            "cloze" or "conversation" or "flashcard-activity" or "how-do-you-say"
                or "minimal-pairs" or "numberdrill" or "reading" or "scene" or "shadowing"
                or "translation" or "video-watching" or "vocab-matching" or "vocab-quiz"
                or "word-association" or "writing"
                => FeedbackRouteCategory.Activity,

            // No default that echoes the input. A route nobody classified is Unknown.
            _ => FeedbackRouteCategory.Unknown
        };
    }

    /// <summary>
    /// The first path segment, lower-cased, with any query or fragment removed.
    /// </summary>
    /// <remarks>
    /// Only the first segment is ever examined. Everything after it is where identifiers live —
    /// <c>/resources/edit/4821</c>, <c>/diary/2026-08-21</c> — and reading it would be the first
    /// step towards putting it somewhere.
    /// </remarks>
    private static string FirstSegment(string? relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            return string.Empty;
        }

        var span = relativePath.AsSpan().Trim();

        var cut = span.IndexOfAny('?', '#');
        if (cut >= 0)
        {
            span = span[..cut];
        }

        span = span.TrimStart('/');

        var slash = span.IndexOf('/');
        if (slash >= 0)
        {
            span = span[..slash];
        }

        return span.IsEmpty ? string.Empty : span.ToString().ToLowerInvariant();
    }
}
