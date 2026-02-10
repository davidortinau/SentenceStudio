using Microsoft.AspNetCore.Components.WebView.Maui;
using Microsoft.Maui.Controls;

namespace SentenceStudio;

/// <summary>
/// MAUI ContentPage that hosts the BlazorWebView.
/// This will become the app's main page once all UI is migrated to Blazor.
/// </summary>
public class BlazorHostPage : Microsoft.Maui.Controls.ContentPage
{
    public BlazorHostPage()
    {
        var blazorWebView = new BlazorWebView
        {
            HostPage = "wwwroot/index.html"
        };
        blazorWebView.RootComponents.Add(new RootComponent
        {
            Selector = "#app",
            ComponentType = typeof(WebUI.Routes)
        });
        Content = blazorWebView;
    }
}
