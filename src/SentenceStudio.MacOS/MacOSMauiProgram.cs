using CommunityToolkit.Maui;
using CommunityToolkit.Maui.Storage;
#if DEBUG
using Microsoft.Maui.DevFlow.Agent;
using Microsoft.Maui.DevFlow.Blazor;
#endif
using Microsoft.AspNetCore.Components.WebView.Maui;
using Microsoft.Maui.Platforms.MacOS.Essentials;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Maui.Platforms.MacOS.Hosting;
using Plugin.Maui.Audio;
using SentenceStudio;
using SentenceStudio.WebUI.Services;

namespace SentenceStudio.MacOS;

public static class MacOSMauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();
        builder
            .UseMauiAppMacOS<MacOSBlazorApp>()
            .AddMacOSEssentials()
            .UseMauiCommunityToolkit()
            .UseSentenceStudioApp();

        builder.AddMauiServiceDefaults("MacOS");

        builder.Configuration.AddEmbeddedAppSettings();

        builder.AddAudio();

        builder.Services.AddMauiBlazorWebView();
        builder.AddMacOSBlazorWebView();
        builder.Services.AddBlazorUIServices();

#if DEBUG
        builder.Services.AddBlazorWebViewDeveloperTools();
        builder.Logging
            .AddDebug()
            .AddConsole()
            .SetMinimumLevel(LogLevel.Debug);
        builder.AddMauiDevFlowAgent(options => { options.Port = 9225; });
        builder.AddMauiBlazorDevFlowTools();
#endif

        var app = builder.Build();
        return SentenceStudioAppBuilder.InitializeApp(app);
    }

}
