namespace SentenceStudio.Abstractions;

/// <summary>
/// Receives unhandled JavaScript errors from a Blazor WebView host page (#169). Registered only
/// by hosts that run the shared UI inside a WebView (the MAUI apps); the WebApp registers none,
/// so the shared layout never attaches there.
/// </summary>
public interface IWebViewErrorSink
{
    void Report(WebViewError error);
}

/// <summary>An unhandled JavaScript error or Promise rejection captured in the WebView.</summary>
/// <param name="Kind"><c>error</c> or <c>unhandledrejection</c>.</param>
public sealed record WebViewError(
    string Kind,
    string Message,
    string? Source,
    int? Line,
    int? Column,
    string? Stack,
    string? Route);
