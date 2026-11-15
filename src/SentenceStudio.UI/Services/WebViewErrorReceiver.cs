using Microsoft.JSInterop;
using SentenceStudio.Abstractions;

namespace SentenceStudio.WebUI.Services;

/// <summary>
/// The .NET end of <c>wwwroot/js/webview-errors.js</c> (#169): JavaScript calls <see cref="Report"/>
/// and the host's <see cref="IWebViewErrorSink"/> records it.
/// </summary>
public sealed class WebViewErrorReceiver(IWebViewErrorSink sink)
{
    [JSInvokable]
    public void Report(WebViewErrorPayload payload)
    {
        if (payload is null)
            return;
        sink.Report(new WebViewError(
            payload.Kind is "unhandledrejection" ? "unhandledrejection" : "error",
            string.IsNullOrWhiteSpace(payload.Message) ? "Unknown error" : payload.Message,
            payload.Source, payload.Line, payload.Column, payload.Stack, payload.Route));
    }
}

/// <summary>The JSON the script sends; JavaScript's camelCase names bind case-insensitively.</summary>
public sealed record WebViewErrorPayload(
    string? Kind, string? Message, string? Source, int? Line, int? Column, string? Stack, string? Route);
