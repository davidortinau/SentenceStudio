using CommunityToolkit.Maui;
using CommunityToolkit.Maui.Storage;
using Microsoft.Maui.DevFlow.Agent;
using Microsoft.Maui.DevFlow.Blazor;
using Microsoft.Extensions.Logging;
using Plugin.Maui.Audio;
using SentenceStudio;
using SentenceStudio.WebUI.Services;

namespace SentenceStudio.Windows;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();
        builder.Configuration.AddEmbeddedAppSettings();

        builder
            .UseMauiApp<BlazorApp>()
            .UseMauiCommunityToolkit()
            .UseSentenceStudioApp();

        builder.AddAudio();

        builder.Services.AddMauiBlazorWebView();
        builder.Services.AddBlazorUIServices();

#if DEBUG
        builder.Logging
            .AddDebug()
            .AddConsole()
            .SetMinimumLevel(LogLevel.Debug);
        builder.Services.AddBlazorWebViewDeveloperTools();
        builder.AddMauiDevFlowAgent(options => { options.Port = 9224; });
        builder.AddMauiBlazorDevFlowTools();
#endif

        var app = builder.Build();
        return SentenceStudioAppBuilder.InitializeApp(app);
    }

}
