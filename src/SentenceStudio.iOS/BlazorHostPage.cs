#nullable enable

using Microsoft.AspNetCore.Components.WebView.Maui;
using Microsoft.Maui.Controls;
using SentenceStudio.WebUI;

namespace SentenceStudio;

public class BlazorHostPage : ContentPage
{
    public BlazorHostPage()
    {
        SafeAreaEdges = Microsoft.Maui.SafeAreaEdges.None;

        var blazorWebView = new BlazorWebView
        {
            HostPage = "wwwroot/index.html"
        };
        blazorWebView.RootComponents.Add(new RootComponent
        {
            Selector = "#app",
            ComponentType = typeof(Routes)
        });
        // The page is this app's own bundled UI. iOS has already asked the user for microphone access
        // (NSMicrophoneUsageDescription), so grant WebKit's extra per-page prompt for that origin only.
        // MAUI installs its own UI delegate for alert/confirm right after this event fires, so wrap it
        // on the next dispatcher turn rather than replacing it here.
        blazorWebView.BlazorWebViewInitialized += (_, e) =>
        {
            var webView = e.WebView;
            Dispatcher.Dispatch(() =>
            {
                if (webView.UIDelegate is not AppOriginMediaCaptureUIDelegate)
                {
                    webView.UIDelegate = new AppOriginMediaCaptureUIDelegate(webView.UIDelegate);
                }
            });
        };
        Content = blazorWebView;
    }
}

/// <summary>
/// Grants microphone capture to the app's own BlazorWebView origin (<c>app://0.0.0.1</c>) without a
/// second WebKit prompt, and forwards alert and confirm dialogs to the delegate MAUI installed.
/// </summary>
/// <remarks>
/// <c>prompt()</c> is not forwarded: the binding for that callback is obsolete (it cannot return null), and
/// the app does not use <c>prompt()</c>. Without it WebKit returns null, the standard cancelled result.
/// </remarks>
internal sealed class AppOriginMediaCaptureUIDelegate : WebKit.WKUIDelegate
{
    private readonly WebKit.IWKUIDelegate? _inner;

    public AppOriginMediaCaptureUIDelegate(WebKit.IWKUIDelegate? inner) => _inner = inner;

    public override void RequestMediaCapturePermission(
        WebKit.WKWebView webView,
        WebKit.WKSecurityOrigin origin,
        WebKit.WKFrameInfo frame,
        WebKit.WKMediaCaptureType type,
        Action<WebKit.WKPermissionDecision> decisionHandler)
    {
        var isAppOrigin = origin.Protocol == "app" && origin.Host == "0.0.0.1";
        decisionHandler(isAppOrigin && type == WebKit.WKMediaCaptureType.Microphone
            ? WebKit.WKPermissionDecision.Grant
            : WebKit.WKPermissionDecision.Prompt);
    }

    public override void RunJavaScriptAlertPanel(WebKit.WKWebView webView, string message, WebKit.WKFrameInfo frame, Action completionHandler)
    {
        if (InnerResponds("webView:runJavaScriptAlertPanelWithMessage:initiatedByFrame:completionHandler:"))
            _inner!.RunJavaScriptAlertPanel(webView, message, frame, completionHandler);
        else
            completionHandler();
    }

    public override void RunJavaScriptConfirmPanel(WebKit.WKWebView webView, string message, WebKit.WKFrameInfo frame, Action<bool> completionHandler)
    {
        if (InnerResponds("webView:runJavaScriptConfirmPanelWithMessage:initiatedByFrame:completionHandler:"))
            _inner!.RunJavaScriptConfirmPanel(webView, message, frame, completionHandler);
        else
            completionHandler(false);
    }

    private bool InnerResponds(string selector) =>
        _inner is Foundation.NSObject inner && inner.RespondsToSelector(new ObjCRuntime.Selector(selector));
}
