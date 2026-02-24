using Microsoft.AspNetCore.Components.WebView.Maui;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Platform.MacOS.Controls;
using SentenceStudio.WebUI;

namespace SentenceStudio.MacOS;

public class MacOSBlazorHostPage : ContentPage
{
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
    }
}
