using Microsoft.AspNetCore.Components.WebView.Maui;
using AppKit;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Platforms.MacOS.Controls;
using SentenceStudio.WebUI;
using WebKit;

namespace SentenceStudio.MacOS;

public class MacOSBlazorHostPage : ContentPage
{
    private static MacOSBlazorWebView? _webView;

    public MacOSBlazorHostPage()
    {
        var blazorWebView = new MacOSBlazorWebView
        {
            HostPage = "wwwroot/index.html"
        };

        blazorWebView.RootComponents.Add(new BlazorRootComponent
        {
            Selector = "#app",
            ComponentType = typeof(Routes)
        });

        Content = blazorWebView;
        _webView = blazorWebView;
    }

    /// <summary>
    /// Sends the Blazor app to the login route via a full WebView navigation. Used by the
    /// native Account menu's logout action — reliable even from pages that aren't
    /// [Authorize]-gated (onboarding) or behind a sync overlay.
    /// </summary>
    public static void NavigateToLogin()
    {
        // WKWebView access and EvaluateJavaScript must run on the UI thread; the caller
        // may resume on a thread-pool thread after awaiting LogOutAsync.
        var app = NSApplication.SharedApplication;
        if (app is not null)
            app.BeginInvokeOnMainThread(NavigateToLoginCore);
        else
            NavigateToLoginCore();
    }

    static void NavigateToLoginCore()
    {
        if (_webView?.Handler?.PlatformView is WKWebView wk)
            wk.EvaluateJavaScript("window.location.assign('/auth/login');", (_, _) => { });
    }
}
