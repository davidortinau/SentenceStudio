using System.Globalization;

namespace SentenceStudio.Api.Coach.Application.History;

/// <summary>
/// The generic title a new conversation starts with.
/// </summary>
/// <remarks>
/// <para>
/// A title is server metadata, not model output. Asking the model to name a conversation would
/// mean sending it the learner's words for a second purpose and storing the result as a label that
/// shows up in a list — a small feature with a large privacy surface. A date does the job: it is
/// recognisable, it never leaks content, and renaming is one tap away.
/// </para>
/// <para>
/// Shared so the new conversation route and the compatibility session route produce the same
/// thing. A learner who starts a session on the old client and opens the list on the new one
/// should not be able to tell which route created the row.
/// </para>
/// </remarks>
internal static class CoachHistoryTitles
{
    /// <summary>Builds the default title for a conversation started on the given local date.</summary>
    public static string Fallback(DateOnly today) => string.Format(
        CultureInfo.CurrentCulture,
        "Coach \u2014 {0}",
        today.ToString("d", CultureInfo.CurrentCulture));
}
