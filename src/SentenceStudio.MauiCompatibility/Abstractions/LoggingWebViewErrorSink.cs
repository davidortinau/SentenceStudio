using Microsoft.Extensions.Logging;
using SentenceStudio.Abstractions;

namespace SentenceStudio.Abstractions;

/// <summary>
/// Records unhandled JavaScript errors from the MAUI Blazor WebView (#169). Each one is logged as
/// an exception, so the OpenTelemetry exporter sends it to Application Insights' exceptions table
/// next to the app's .NET crashes, under the current trace when there is one.
/// </summary>
public sealed class LoggingWebViewErrorSink(ILogger<LoggingWebViewErrorSink> logger) : IWebViewErrorSink
{
    public void Report(WebViewError error)
    {
        ArgumentNullException.ThrowIfNull(error);
        logger.LogError(new WebViewJavaScriptException(error),
            "Unhandled JavaScript {Kind} in the Blazor WebView on {Route} at {Source}:{Line}:{Column}",
            error.Kind, error.Route, error.Source, error.Line, error.Column);
    }
}

/// <summary>A JavaScript error carried as a .NET exception, keeping the script's own stack.</summary>
public sealed class WebViewJavaScriptException(WebViewError error) : Exception(error.Message)
{
    public string Kind { get; } = error.Kind;

    public override string? StackTrace { get; } = error.Stack;
}
