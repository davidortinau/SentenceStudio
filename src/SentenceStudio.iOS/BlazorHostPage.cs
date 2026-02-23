using Microsoft.AspNetCore.Components.WebView.Maui;
using Microsoft.Maui.Controls;
using SentenceStudio.WebUI;

namespace SentenceStudio;

public class BlazorHostPage : ContentPage
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
            ComponentType = typeof(Routes)
        });
        Content = blazorWebView;
    }
}
