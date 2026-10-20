namespace SentenceStudio.Native.iOS.Platform;

internal static class NativeDashboardCardAccessibility
{
    internal static string Label(string title, string metadata, string? completedStatus,
        string? unavailableStatus = null) =>
        string.Join(". ", new[] { title, metadata, completedStatus, unavailableStatus }
            .Where(part => !string.IsNullOrWhiteSpace(part)));
}
