namespace SentenceStudio.Native;

public static class NativeVideoWatchingAccess
{
    public const string Screen = "native.video.screen";
    public const string Loading = "native.video.loading";
    public const string Title = "native.video.title";
    public const string OpenInYouTube = "native.video.open-youtube";
    public const string Player = "native.video.player";
    public const string Transcript = "native.video.transcript";
    public const string TranscriptToggle = "native.video.transcript.toggle";
    public const string TranscriptText = "native.video.transcript.text";
    public const string Error = "native.video.error";
    public const string Empty = "native.video.empty";
    public const string GoBack = "native.video.go-back";
    public const string Back = "native.video.back";
}

public static class NativeVideoWatchingText
{
    public const string Title = "Activity_VideoWatching";
    public const string LoadingVideo = "VideoWatching_LoadingVideo";
    public const string OpenInYouTube = "VideoWatching_OpenInYouTube";
    public const string Transcript = "VideoWatching_Transcript";
    public const string Hide = "VideoWatching_Hide";
    public const string Show = "VideoWatching_Show";
    public const string NoVideo = "VideoWatching_NoVideo";
    public const string GoBack = "Common_GoBack";
    public const string Unavailable = "NativeActivity_LaunchUnavailable";
    // Source literals in VideoWatching.razor, now localized.
    public const string NoResource = "VideoWatching_NoResource";
    public const string ResourceNotFound = "Reading_ResourceNotFound";
    public const string NoVideoId = "VideoWatching_NoVideoId";
    /// <summary>Format: {0} is the failure message (source "Error loading video: ...").</summary>
    public const string LoadError = "VideoWatching_LoadError";
    /// <summary>Source fallback title when the resource has none.</summary>
    public const string DefaultTitle = "VideoWatching_DefaultTitle";

    /// <summary>
    /// The source labels carry decorative arrows ("Show ▼", "↗ Open in
    /// YouTube"); screen readers get the words only.
    /// </summary>
    public static string Spoken(string label) =>
        label.Replace("▼", string.Empty, StringComparison.Ordinal)
            .Replace("▲", string.Empty, StringComparison.Ordinal)
            .Replace("↗", string.Empty, StringComparison.Ordinal)
            .Trim();
}

/// <summary>
/// Phases: Loading (busy spinner), Ready (title, Open in YouTube, player,
/// transcript card), Error (no resource / not found / no YouTube id / load
/// failure; Go Back), Empty (no video; Go Back), Blocked (owner absent or
/// changed). Destination becomes TodaysPlan after Back. The source page timer
/// runs silently while the page is open; nothing else is recorded.
/// </summary>
public sealed record NativeVideoWatchingState(
    NativePhase Phase,
    NativeDestination Destination = NativeDestination.Activity,
    string? ResourceTitle = null,
    string? VideoId = null,
    string? MediaUrl = null,
    string? Transcript = null,
    bool ShowTranscript = false,
    string? ErrorKey = null,
    string? ErrorArgument = null,
    string? TranscriptLanguageTag = null,
    bool HasPendingSync = false)
{
    /// <summary>Source: the transcript card exists only for a non-empty transcript.</summary>
    public bool HasTranscript => !string.IsNullOrEmpty(Transcript);

    /// <summary>
    /// Source shows "Open in YouTube" for any non-empty MediaUrl; native opens
    /// only an absolute http(s) URL in the system browser or YouTube app.
    /// </summary>
    public Uri? ExternalUri =>
        Uri.TryCreate(MediaUrl?.Trim(), UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp)
            ? uri : null;
}
