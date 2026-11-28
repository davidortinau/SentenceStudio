using System.Runtime.CompilerServices;

namespace SentenceStudio.Native.iOS.Tests;

/// <summary>
/// Cuts a piece out of a source file between two markers, for the source contract tests.
/// A marker that is no longer in the source fails the test by naming the marker and the
/// source it was looked for in. The slices this replaces,
/// <c>source[source.IndexOf(start)..source.IndexOf(end)]</c>, failed with an
/// ArgumentOutOfRangeException and a negative length instead (issue 346).
/// </summary>
internal static class SourceSlice
{
    /// <summary>
    /// From <paramref name="start"/> up to, not including, <paramref name="end"/>. Each marker
    /// is its first occurrence in the whole source, as in the slices this replaces.
    /// </summary>
    internal static string Between(string source, string start, string end,
        [CallerArgumentExpression(nameof(source))] string? sourceName = null)
    {
        var from = Find(source, start, "start", sourceName);
        var to = Find(source, end, "end", sourceName);
        if (to < from)
        {
            Assert.Fail($"The end marker {Quoted(end)} comes before the start marker "
                + $"{Quoted(start)} in '{sourceName}'. The code between them was reordered: "
                + "read the source and update the markers in this test.");
        }

        return source[from..to];
    }

    /// <summary>From <paramref name="start"/> to the end of the source.</summary>
    internal static string From(string source, string start,
        [CallerArgumentExpression(nameof(source))] string? sourceName = null) =>
        source[Find(source, start, "start", sourceName)..];

    /// <summary>From the beginning of the source up to, not including, <paramref name="end"/>.</summary>
    internal static string Until(string source, string end,
        [CallerArgumentExpression(nameof(source))] string? sourceName = null) =>
        source[..Find(source, end, "end", sourceName)];

    private static int Find(string source, string marker, string role, string? sourceName)
    {
        var index = source.IndexOf(marker, StringComparison.Ordinal);
        if (index < 0)
        {
            Assert.Fail($"The {role} marker {Quoted(marker)} was not found in '{sourceName}'. "
                + "The code it marks was renamed, moved or removed: read the source and update "
                + "the marker in this test.");
        }

        return index;
    }

    private static string Quoted(string marker) =>
        "\"" + marker.Replace("\r", "\\r").Replace("\n", "\\n") + "\"";
}
